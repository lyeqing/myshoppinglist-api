using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

// Every write is fenced by the same lock order and eligibility checks as source persistence.
public sealed class RetailerComparisonPersistenceService(MyShoppingListDbContext db, ShopProductService mappings,
    PriceService prices, ProductMatchingService matching, ProductUrlValidator urls,
    IOptions<ProductImportOptions> options, TimeProvider clock)
{
    public Task<bool> SaveStatusAsync(ProductImportClaim claim, long shopId, RetailerLookupStatus status,
        string? code, CancellationToken token, MatchType? match = null, int? confidence = null) =>
        WriteAsync(claim, shopId, (_, result) =>
        {
            SetResult(result, status, code);
            result.MatchType = match; result.MatchConfidence = confidence;
            return Task.FromResult(true);
        }, token);

    public Task<bool> TryCacheAsync(ProductImportClaim claim, long shopId, CancellationToken token) =>
        WriteAsync(claim, shopId, async (job, result) =>
        {
            if (!await db.Products.AnyAsync(p => p.Id == job.ProductId && !p.IsDeleted, token)) return false;
            var known = await db.ShopProducts.Where(p => p.ProductId == job.ProductId && p.ShopId == shopId
                && p.IsActive && p.MatchType == MatchType.Exact).ToListAsync(token);
            if (known.Count != 1) return false;
            var mapping = known[0];
            var observations = await db.ShopProductPrices.Where(p => p.ShopProductId == mapping.Id).ToListAsync(token);
            var now = clock.GetUtcNow().UtcDateTime;
            var cutoff = now.AddHours(-options.Value.ComparisonFreshHours);
            // The status API exposes all observations for a mapping. Do not label a mixed stale/store cache fresh.
            if (observations.Count == 0 || observations.Any(p => p.Currency != "AUD" || p.ShopLocationId != null
                || p.PriceScope is not (PriceScope.Unknown or PriceScope.Online or PriceScope.National)
                || p.CheckedDate < cutoff || p.CheckedDate > now || p.SpecialEndDate <= now
                || p.SpecialStartDate > now)) return false;
            SetResult(result, RetailerLookupStatus.Exact, null);
            result.ShopProductId = mapping.Id; result.MatchType = MatchType.Exact;
            result.MatchConfidence = mapping.MatchConfidence; result.IsFromCache = true;
            result.CheckedDate = observations.Min(p => p.CheckedDate);
            return true;
        }, token);

    public Task<bool> SaveExactAsync(ProductImportClaim claim, long shopId, ExtractedShopProduct source,
        CancellationToken token) => WriteAsync(claim, shopId, async (job, result) =>
        {
            var shop = await db.Shops.SingleAsync(s => s.Id == shopId, token);
            var product = await db.Products.SingleAsync(p => p.Id == job.ProductId && !p.IsDeleted, token);
            var url = urls.Validate(source.ProductUrl.OriginalString);
            var match = matching.Match(ProductMatchingService.Identity(product), source.Identity);
            if (!url.IsValid || url.Retailer!.Code != shop.Code || source.ShopCode != shop.Code
                || match.Type != MatchType.Exact || source.CheckedDate == default || source.CheckedDate > clock.GetUtcNow()
                || string.IsNullOrWhiteSpace(source.Identity.Name) || source.Identity.Name.Length > 500
                || source.ShopProductCode?.Length > 200 || source.Sku?.Length > 200 || source.Description?.Length > 10000
                || source.Identity.GTIN?.Length > 14 || !Enum.IsDefined(source.SourceType)
                || source.ImageUrl is { } image && (!image.IsAbsoluteUri || image.Scheme != "https" || image.AbsoluteUri.Length > 2048)) return false;
            var known = await mappings.FindAsync(shopId, source, token);
            if (known.Count > 1 || known.Any(p => p.ProductId != product.Id || p.MatchType != MatchType.Exact
                || p.ShopProductCode != null && source.ShopProductCode != null && p.ShopProductCode != source.ShopProductCode.Trim())) return false;
            var otherExact = await db.ShopProducts.AnyAsync(p => p.ProductId == product.Id && p.ShopId == shopId
                && p.IsActive && p.MatchType == MatchType.Exact && !known.Select(k => k.Id).Contains(p.Id), token);
            if (otherExact) return false;
            var mapping = mappings.Save(shop, product, known.SingleOrDefault(), source, match.Confidence);
            await db.SaveChangesAsync(token);
            if (source.Offer is ProviderResult<ShopProductOffer>.Success offer)
            {
                if (offer.Value.SourceUrl != source.ProductUrl) return false;
                var saved = await prices.SaveAsync(mapping, offer.Value, token);
                if (saved.Status == PriceUpdateStatus.InvalidOffer) return false;
            }
            var failure = (source.Offer as ProviderResult<ShopProductOffer>.Failure)?.Error;
            SetResult(result, failure?.Status ?? (source.Offer is null ? RetailerLookupStatus.Unavailable : RetailerLookupStatus.Exact),
                failure?.Code ?? (source.Offer is null ? "price_not_checked" : null));
            result.ShopProductId = mapping.Id; result.MatchType = MatchType.Exact; result.MatchConfidence = match.Confidence;
            result.CheckedDate = source.CheckedDate.UtcDateTime;
            return true;
        }, token);

    private void SetResult(ProductImportRetailerResult result, RetailerLookupStatus status, string? code)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        result.Status = status; result.ErrorCode = code?[..Math.Min(code.Length, 100)]; result.ErrorMessage = null;
        result.ShopProductId = null; result.MatchType = null; result.MatchConfidence = null; result.IsFromCache = false;
        result.CheckedDate = null; result.StartedDate ??= now; result.UpdatedDate = now;
        result.CompletedDate = status is RetailerLookupStatus.Pending or RetailerLookupStatus.Checking ? null : now;
    }

    private async Task<bool> WriteAsync(ProductImportClaim claim, long shopId,
        Func<ProductImportJob, ProductImportRetailerResult, Task<bool>> write, CancellationToken token)
    {
        if (db.ChangeTracker.Entries().Any()) throw new InvalidOperationException("Comparison persistence requires a clean context.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await ProductService.LockCatalogueAsync(db, token);
            var user = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {claim.UserAccountId} FOR UPDATE""").SingleOrDefaultAsync(token);
            var list = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {claim.ShoppingListId} FOR UPDATE""").SingleOrDefaultAsync(token);
            var job = await db.ProductImportJobs.FromSqlInterpolated($"""SELECT * FROM "ProductImportJobs" WHERE "Id" = {claim.JobId} FOR UPDATE""").SingleOrDefaultAsync(token);
            bool Eligible()
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return job is not null && job.UserAccountId == claim.UserAccountId && job.ShoppingListId == claim.ShoppingListId
                    && job.Status == ProductImportJobStatus.Processing && job.ClaimToken == claim.Token && job.LeaseExpiresDate > now
                    && job.ProductId != null && job.ShoppingListProductId != null && job.SourceShopId != shopId
                    && user is { IsActive: true } && (!user.IsTrial || user.ExpiresDate > now)
                    && list is { IsArchived: false } && list.UserAccountId == claim.UserAccountId
                    && (list.ExpiresDate == null || list.ExpiresDate > now);
            }
            if (!Eligible() || !await db.Shops.AnyAsync(s => s.Id == shopId && s.IsActive, token)) return false;
            var result = await db.ProductImportRetailerResults.SingleOrDefaultAsync(r => r.ProductImportJobId == claim.JobId && r.ShopId == shopId, token);
            if (result is not null && result.Status is not (RetailerLookupStatus.Pending or RetailerLookupStatus.Checking)) return false;
            if (result is null)
            {
                result = new() { ProductImportJobId = claim.JobId, ShopId = shopId, CreatedDate = clock.GetUtcNow().UtcDateTime };
                db.ProductImportRetailerResults.Add(result);
            }
            if (!await write(job!, result)) return false;
            job!.LastActivityDate = clock.GetUtcNow().UtcDateTime;
            job.ProgressStage = ProductImportProgressStage.CheckingRetailers;
            await db.SaveChangesAsync(token);
            if (!Eligible()) return false;
            await transaction.CommitAsync(token);
            return true;
        }
        finally { db.ChangeTracker.Clear(); }
    }
}
