using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Services;

public sealed class SourceProductPersistenceService(MyShoppingListDbContext db, ProductService products,
    ShopProductService mappings, PriceService prices, ProductNormalisationService normalisation,
    ProductUrlValidator urls, TimeProvider clock, ILogger<SourceProductPersistenceService> logger)
{
    public async Task<ProductPersistenceResult> SaveAsync(long jobId, long userAccountId, Guid claimToken,
        ExtractedShopProduct source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (db.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("Source persistence requires a fresh or cleared DbContext with no unrelated tracked entities.");
        if (!ValidSource(source)) return new(ProductPersistenceStatus.InvalidData, "invalid_source_product");
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var transaction = db.Database.CurrentTransaction ?? await db.Database.BeginTransactionAsync(token);
        var savepoint = "source_" + Guid.NewGuid().ToString("N");
        if (!ownsTransaction) await transaction.CreateSavepointAsync(savepoint, token);
        var accepted = false;
        try
        {
            await ProductService.LockCatalogueAsync(db, token);
            // Lock order is catalogue, account, list, then job. Cleanup/worker writers must use the same order.
            var jobInfo = await db.ProductImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, token);
            if (jobInfo is null) return new(ProductPersistenceStatus.NotFound, "job_not_found");
            if (jobInfo.UserAccountId != userAccountId) return new(ProductPersistenceStatus.AccessDenied, "job_access_denied");
            var user = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {userAccountId} FOR UPDATE""")
                .SingleOrDefaultAsync(token);
            var listId = jobInfo.ShoppingListId;
            var list = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {listId} FOR UPDATE""")
                .SingleOrDefaultAsync(token);
            var job = await db.ProductImportJobs.FromSqlInterpolated($"""SELECT * FROM "ProductImportJobs" WHERE "Id" = {jobId} FOR UPDATE""")
                .SingleOrDefaultAsync(token);
            if (job is null) return new(ProductPersistenceStatus.NotFound, "job_not_found");
            if (job.UserAccountId != userAccountId || job.ShoppingListId != listId || list?.UserAccountId != userAccountId)
                return new(ProductPersistenceStatus.AccessDenied, "list_access_denied");
            var eligibility = Eligibility(user, list, job, claimToken);
            if (eligibility is not null) return eligibility;
            var shopCode = source.ShopCode.ToLowerInvariant();
            var shop = await db.Shops.SingleOrDefaultAsync(s => s.Code == shopCode && s.IsActive, token);
            var jobUrl = urls.Validate(job.SourceUrl);
            if (shop is null || !jobUrl.IsValid || jobUrl.Retailer!.Code != shopCode
                || job.SourceShopId.HasValue && job.SourceShopId != shop.Id)
                return new(ProductPersistenceStatus.InvalidData, "source_retailer_mismatch");

            var known = await mappings.FindAsync(shop.Id, source, token);
            if (known.Count > 1) return new(ProductPersistenceStatus.IdentityConflict, "ambiguous_retailer_mapping");
            if (known.SingleOrDefault()?.ShopProductCode is { } knownCode && !string.IsNullOrWhiteSpace(source.ShopProductCode)
                && knownCode != source.ShopProductCode.Trim())
                return new(ProductPersistenceStatus.IdentityConflict, "retailer_product_code_changed");
            var resolution = await products.ResolveAsync(source, known.SingleOrDefault(), clock.GetUtcNow().UtcDateTime, token);
            if (resolution.Status != ProductPersistenceStatus.Success) return new(resolution.Status, resolution.ErrorCode);
            var product = resolution.Product!;
            if (job.ProductId.HasValue && job.ProductId != product.Id)
                return new(ProductPersistenceStatus.IdentityConflict, "job_product_changed");
            var mapping = mappings.Save(shop, product, known.SingleOrDefault(), source, resolution.MatchConfidence);
            await db.SaveChangesAsync(token);
            var priceSaved = false;
            if (source.Offer is ProviderResult<ShopProductOffer>.Success offer)
            {
                var result = await prices.SaveAsync(mapping, offer.Value, token);
                if (result.Status == PriceUpdateStatus.InvalidOffer) return new(ProductPersistenceStatus.InvalidData, result.ErrorCode);
                priceSaved = result.Status is PriceUpdateStatus.Saved or PriceUpdateStatus.Unchanged;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var item = await db.ShoppingListProducts.SingleOrDefaultAsync(i => i.ShoppingListId == listId && i.ProductId == product.Id, token);
            var existingItem = item is not null;
            if (item is null)
            {
                item = new() { ShoppingListId = listId, ProductId = product.Id, Quantity = job.RequestedQuantity, AddedDate = now, UpdatedDate = now };
                db.ShoppingListProducts.Add(item);
                list!.UpdatedDate = now;
            }
            // Existing quantity, notes, purchased state and hidden state are deliberately preserved on retries.
            job.Product = product; job.ShoppingListProduct = item; job.SourceShopId = shop.Id;
            job.LastActivityDate = now; job.ProgressStage = ProductImportProgressStage.CheckingRetailers;
            var retailerResult = await db.ProductImportRetailerResults.SingleOrDefaultAsync(r => r.ProductImportJobId == jobId && r.ShopId == shop.Id, token);
            if (retailerResult is null)
            {
                retailerResult = new() { ProductImportJobId = jobId, ShopId = shop.Id, CreatedDate = now, StartedDate = now };
                db.ProductImportRetailerResults.Add(retailerResult);
            }
            retailerResult.ShopProduct = mapping;
            retailerResult.MatchType = mapping.MatchType;
            retailerResult.MatchConfidence = mapping.MatchConfidence;
            retailerResult.CheckedDate = source.CheckedDate.UtcDateTime;
            retailerResult.CompletedDate = now; retailerResult.UpdatedDate = now;
            retailerResult.IsFromCache = false;
            if (source.Offer is ProviderResult<ShopProductOffer>.Failure failure)
            {
                retailerResult.Status = failure.Error.Status;
                retailerResult.ErrorCode = Clip(failure.Error.Code, 100);
                retailerResult.ErrorMessage = Clip(failure.Error.Message, 2000);
            }
            else
            {
                retailerResult.Status = source.Offer is null ? RetailerLookupStatus.Unavailable : RetailerLookupStatus.Exact;
                retailerResult.ErrorCode = source.Offer is null ? "price_not_checked" : null;
                retailerResult.ErrorMessage = source.Offer is null ? "The product was identified, but a price was not checked." : null;
            }
            await db.SaveChangesAsync(token);
            // Time may have elapsed while waiting for database locks. Never commit an expired lease or trial.
            eligibility = Eligibility(user, list, job, claimToken);
            if (eligibility is not null) return eligibility;
            if (ownsTransaction) await transaction.CommitAsync(token);
            else await transaction.ReleaseSavepointAsync(savepoint, token);
            accepted = true;
            logger.LogInformation("Source persistence saved for job {ProductImportJobId}, list {ShoppingListId}, product {ProductId}", jobId, listId, product.Id);
            return new(ProductPersistenceStatus.Success, ProductId: product.Id, ShopProductId: mapping.Id,
                ShoppingListProductId: item.Id, ExistingListItem: existingItem, PriceSaved: priceSaved);
        }
        catch (DbUpdateConcurrencyException)
        { return new(ProductPersistenceStatus.LostClaim, "concurrent_job_change"); }
        catch (DbUpdateException)
        { return new(ProductPersistenceStatus.PersistenceConflict, "database_write_conflict"); }
        finally
        {
            try
            {
                if (!accepted)
                {
                    if (ownsTransaction) await transaction.RollbackAsync(CancellationToken.None);
                    else await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                }
            }
            finally
            {
                db.ChangeTracker.Clear();
                if (ownsTransaction) await transaction.DisposeAsync();
            }
        }
    }

    private ProductPersistenceResult? Eligibility(UserAccount? user, ShoppingList? list, ProductImportJob job, Guid token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (user is null || !user.IsActive) return new(ProductPersistenceStatus.AccessDenied, "inactive_account");
        if (user.IsTrial && (user.ExpiresDate is null || user.ExpiresDate <= now)) return new(ProductPersistenceStatus.ExpiredAccount, "trial_expired");
        if (list is null || list.IsArchived || list.ExpiresDate <= now) return new(ProductPersistenceStatus.ListUnavailable, "list_unavailable");
        if (job.Status != ProductImportJobStatus.Processing || token == Guid.Empty || job.ClaimToken != token || job.LeaseExpiresDate <= now
            || job.LeaseExpiresDate is null) return new(ProductPersistenceStatus.LostClaim, "job_claim_lost");
        return null;
    }

    private bool ValidSource(ExtractedShopProduct source)
    {
        var identity = source.Identity;
        var url = urls.Validate(source.ProductUrl.OriginalString);
        return url.IsValid && string.Equals(url.Retailer!.Code, source.ShopCode, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(identity.Name) && identity.Name.Length <= 500
            && Fits(identity.Brand, 200) && Fits(identity.Variant, 200) && Fits(identity.Category, 200) && Fits(identity.SubCategory, 200)
            && Fits(identity.ManufacturerPartNumber, 200) && Fits(identity.ModelNumber, 200)
            && (identity.GTIN is null || normalisation.Gtin(identity.GTIN) is not null)
            && (identity.PackQuantity is null || identity.PackQuantity > 0)
            && (identity.PackSize is null || identity.PackSize > 0 && identity.PackSize <= 1_000_000m && decimal.Round(identity.PackSize.Value, 4) == identity.PackSize)
            && Fits(identity.PackUnit, 20) && Fits(source.ShopProductCode, 200) && Fits(source.Sku, 200)
            && Fits(source.Description, 10000) && Fits(source.ItemDetail, 4000)
            && (source.ImageUrl is null || source.ImageUrl.IsAbsoluteUri && source.ImageUrl.Scheme == "https" && source.ImageUrl.AbsoluteUri.Length <= 2048)
            && Enum.IsDefined(source.SourceType) && source.CheckedDate != default && source.CheckedDate <= clock.GetUtcNow();
    }
    private static bool Fits(string? value, int maximum) => value is null || value.Length <= maximum;
    private static string Clip(string value, int maximum) => value[..Math.Min(maximum, value.Length)];
}
