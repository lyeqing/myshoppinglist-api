using System.Data;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class ProductImportStatusService(MyShoppingListDbContext db, TimeProvider clock)
{
    public async Task<ProductImportPage?> ListAsync(long accountId, long listId, long? beforeId, int pageSize, CancellationToken token)
    {
        if (pageSize is < 1 or > 50 || beforeId <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var now = clock.GetUtcNow().UtcDateTime;
        var access = await db.ShoppingLists.AsNoTracking().Where(l => l.Id == listId && l.UserAccountId == accountId
            && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now) && l.UserAccount.IsActive
            && (!l.UserAccount.IsTrial || l.UserAccount.ExpiresDate > now))
            .Select(l => new { ListExpiry = l.ExpiresDate, AccountExpiry = l.UserAccount.IsTrial ? l.UserAccount.ExpiresDate : null })
            .SingleOrDefaultAsync(token);
        if (access is null) return null;
        var jobs = await db.ProductImportJobs.AsNoTracking().Where(j => j.ShoppingListId == listId && j.UserAccountId == accountId
            && (beforeId == null || j.Id < beforeId)).OrderByDescending(j => j.Id).Take(pageSize + 1)
            .Select(j => new ProductImportSummary(j.Id, j.Status, j.CreatedDate)).ToListAsync(token);
        now = clock.GetUtcNow().UtcDateTime;
        if (access.ListExpiry <= now || access.AccountExpiry <= now) return null;
        var items = jobs.Take(pageSize).ToArray();
        await transaction.CommitAsync(token);
        return new(items, jobs.Count > pageSize ? items[^1].JobId : null);
    }

    public async Task<ProductImportStatusResponse?> ReadAsync(long accountId, long jobId, CancellationToken token)
    {
        // All projections share a snapshot so a worker commit cannot mix old progress with new retailer results.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var now = clock.GetUtcNow().UtcDateTime;
        var job = await db.ProductImportJobs.AsNoTracking().Where(j => j.Id == jobId && j.UserAccountId == accountId
            && j.UserAccount.IsActive && (!j.UserAccount.IsTrial || j.UserAccount.ExpiresDate > now)
            && j.ShoppingList.UserAccountId == accountId && !j.ShoppingList.IsArchived
            && (j.ShoppingList.ExpiresDate == null || j.ShoppingList.ExpiresDate > now))
            .Select(j => new { Job = j, AccountExpiry = j.UserAccount.IsTrial ? j.UserAccount.ExpiresDate : null, ListExpiry = j.ShoppingList.ExpiresDate })
            .SingleOrDefaultAsync(token);
        if (job is null) return null;
        var stored = job.Job;
        var product = await db.Products.AsNoTracking().Where(p => p.Id == stored.ProductId && !p.IsDeleted)
            .Select(p => new ImportProductResponse(p.Id, p.Name, p.Brand, p.Variant, p.PackQuantity, p.PackSize, p.PackUnit, p.ImageUrl))
            .SingleOrDefaultAsync(token);
        var results = await db.ProductImportRetailerResults.AsNoTracking().Where(r => r.ProductImportJobId == jobId)
            .OrderBy(r => r.ShopId).Select(r => new { Result = r, ShopName = r.Shop.Name }).ToListAsync(token);
        var mappingIds = results.Where(r => r.Result.Status is RetailerLookupStatus.Exact or RetailerLookupStatus.Likely or RetailerLookupStatus.Possible)
            .Select(r => r.Result.ShopProductId).OfType<long>().ToArray();
        var prices = await db.ShopProductPrices.AsNoTracking().Where(p => mappingIds.Contains(p.ShopProductId)
            && p.ShopProduct.IsActive && p.ShopProduct.ProductId == stored.ProductId && !p.ShopProduct.Product.IsDeleted)
            .OrderBy(p => p.Id).Select(p => new { p.ShopProductId, p.ShopProduct.ShopId, Price = new ImportPriceResponse(
                p.Price, p.NormalPrice, p.UnitPrice, p.Currency, p.ShopLocationId, p.PriceScope, p.SourceType, p.SourceUrl,
                p.CheckedDate, p.InStock, p.SpecialType, p.SpecialDescription, p.SpecialStartDate, p.SpecialEndDate) }).ToListAsync(token);
        var retailers = results.Select(r => new ImportRetailerResponse(r.Result.ShopId, r.ShopName, r.Result.Status,
            r.Result.MatchType, r.Result.MatchConfidence, r.Result.IsFromCache, r.Result.CheckedDate, r.Result.ErrorCode,
            prices.Where(p => p.ShopProductId == r.Result.ShopProductId && p.ShopId == r.Result.ShopId).Select(p => p.Price).ToArray())).ToArray();
        now = clock.GetUtcNow().UtcDateTime;
        if (job.AccountExpiry <= now || job.ListExpiry <= now) return null;
        await transaction.CommitAsync(token);
        return new(stored.Id, stored.ShoppingListId, stored.Status, stored.ProgressStage, stored.RequestedQuantity,
            stored.ShoppingListProductId, product, stored.CreatedDate, stored.LastActivityDate, stored.CompletedDate,
            stored.NextAttemptDate, stored.ErrorCode, retailers);
    }
}
