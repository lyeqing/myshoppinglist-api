using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public sealed record ProductImportSubmissionResult(ProductImportAcceptedResponse? Response, int StatusCode = 202,
    string? ErrorCode = null, string? Message = null);

public sealed class ProductImportSubmissionService(MyShoppingListDbContext db, ProductUrlValidator urls,
    RetailerProviderRegistry providers, TimeProvider clock, ILogger<ProductImportSubmissionService> logger)
{
    public async Task<ProductImportSubmissionResult> SubmitAsync(long accountId, long listId, ProductImportRequest request, CancellationToken token)
    {
        if (request.Quantity <= 0) return new(null, 400, "invalid_quantity", "Quantity must be a positive whole number.");
        var validated = urls.Validate(request.Url);
        if (!validated.IsValid) return new(null, 400, "invalid_url", "Enter a valid HTTPS product URL from a supported retailer.");
        if (providers.FindByCode(validated.Retailer!.Code)?.IsImplemented != true)
            return new(null, 400, "source_not_supported", "Source imports from this retailer are not implemented yet.");
        var normalised = validated.ProductUrl!.AbsoluteUri;
        // Submission never acquires catalogue locks. Account -> list is the same relative order as worker writes.
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            var user = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(token);
            var list = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {listId} AND "UserAccountId" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(token);
            var now = clock.GetUtcNow().UtcDateTime;
            var error = Access(user, list, now);
            if (error is not null) return error;
            // Serialising submitters on the list row also handles simultaneous HTTP requests in different instances.
            var existing = await db.ProductImportJobs.AsNoTracking().Where(j => j.ShoppingListId == listId
                && j.UserAccountId == accountId && j.NormalisedSourceUrl == normalised
                && (j.Status == ProductImportJobStatus.Queued || j.Status == ProductImportJobStatus.Processing))
                .OrderBy(j => j.CreatedDate).ThenBy(j => j.Id).FirstOrDefaultAsync(token);
            if (existing is not null)
            {
                error = Access(user, list, clock.GetUtcNow().UtcDateTime);
                if (error is not null) return error;
                await transaction.CommitAsync(token);
                return Accepted(existing, true);
            }
            var shop = await db.Shops.AsNoTracking().SingleOrDefaultAsync(s => s.Code == validated.Retailer.Code && s.IsActive, token);
            if (shop is null) return new(null, 400, "source_not_supported", "This retailer is currently unavailable for imports.");
            var job = new ProductImportJob
            {
                UserAccountId = accountId, ShoppingListId = listId, SourceUrl = normalised, NormalisedSourceUrl = normalised,
                SourceShopId = shop.Id, RequestedQuantity = request.Quantity, CreatedDate = now, LastActivityDate = now
            };
            db.ProductImportJobs.Add(job);
            var shopIds = await db.Shops.Where(s => s.IsActive).Select(s => s.Id).ToListAsync(token);
            foreach (var shopId in shopIds) db.ProductImportRetailerResults.Add(new()
            { ProductImportJob = job, ShopId = shopId, Status = RetailerLookupStatus.Pending, CreatedDate = now, UpdatedDate = now });
            await db.SaveChangesAsync(token);
            error = Access(user, list, clock.GetUtcNow().UtcDateTime);
            if (error is not null) return error;
            await transaction.CommitAsync(token);
            logger.LogInformation("Import job {ProductImportJobId} queued for list {ShoppingListId}, source {SourceShop}", job.Id, listId, shop.Code);
            return Accepted(job, false);
        }
        finally { db.ChangeTracker.Clear(); }
    }
    private static ProductImportSubmissionResult Accepted(ProductImportJob job, bool reused) =>
        new(new(job.Id, job.Status, job.RequestedQuantity, reused, $"/api/product-import-jobs/{job.Id}"));
    private static ProductImportSubmissionResult? Access(UserAccount? user, ShoppingList? list, DateTime now)
    {
        if (user is null || !user.IsActive || user.IsTrial && !(user.ExpiresDate > now))
            return new(null, 401, "account_unavailable", "A valid session is required.");
        if (list is null) return new(null, 404, "list_not_found", "The shopping list was not found.");
        if (list.IsArchived || list.ExpiresDate <= now) return new(null, 409, "list_unavailable", "This shopping list is no longer available for imports.");
        return null;
    }
}
