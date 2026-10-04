using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class PriceRefreshService(MyShoppingListDbContext db, TimeProvider clock,
    CatalogueFreshnessService freshness, IOptions<PriceRefreshOptions> options)
{
    public async Task<int?> RequestAsync(long accountId, long listId, CancellationToken token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.ShoppingLists.AnyAsync(l => l.Id == listId && l.UserAccountId == accountId
            && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now) && l.UserAccount.IsActive
            && (!l.UserAccount.IsTrial || l.UserAccount.ExpiresDate > now), token)) return null;
        return await ScanAsync(token, listId, accountId);
    }

    public async Task<int> ScanAsync(CancellationToken token, long? listId = null, long? accountId = null)
    {
        if (!options.Value.Enabled) return 0;
        long cursor = 0;
        var queued = 0;
        while (true)
        {
            // Serialize with extraction persistence and queue claims; recheck prices inside this lock.
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await ProductService.LockCatalogueAsync(db, token);
            var now = clock.GetUtcNow();
            var utc = now.UtcDateTime;
            var eligibleProducts = db.ShoppingListProducts.Where(i => !i.IsPurchased && !i.IsHidden
                && !i.Product.IsDeleted && !i.ShoppingList.IsArchived
                && (listId == null || i.ShoppingListId == listId && i.ShoppingList.UserAccountId == accountId)
                && (i.ShoppingList.ExpiresDate == null || i.ShoppingList.ExpiresDate > utc)
                && i.ShoppingList.UserAccount.IsActive
                && (!i.ShoppingList.UserAccount.IsTrial || i.ShoppingList.UserAccount.ExpiresDate > utc))
                .Select(i => i.ProductId);
            var mappings = await db.ShopProducts.AsNoTracking().Include(m => m.Shop)
                .Where(m => m.Id > cursor && m.IsActive && m.Shop.IsActive && m.MatchType == MatchType.Exact
                    && (m.Shop.Code == "coles" || m.Shop.Code == "woolworths") && eligibleProducts.Contains(m.ProductId))
                .OrderBy(m => m.Id).Take(200).ToArrayAsync(token);
            if (mappings.Length == 0) { await tx.CommitAsync(token); break; }
            var ids = mappings.Select(m => m.Id).ToArray();
            var prices = await db.ShopProductPrices.AsNoTracking()
                .Where(p => ids.Contains(p.ShopProductId) && p.ShopLocationId == null).ToArrayAsync(token);
            foreach (var mapping in mappings)
            {
                var price = prices.Where(p => p.ShopProductId == mapping.Id).OrderByDescending(p => p.CheckedDate)
                    .ThenByDescending(p => p.Id).FirstOrDefault();
                // A recent unavailable observation is still a completed check, not a reason to loop.
                var checkedAt = price?.CheckedDate ?? mapping.LastCheckedDate;
                if (freshness.IsFresh(checkedAt, now) && !(price?.SpecialEndDate <= utc)) continue;
                var url = ProductUrl(mapping);
                if (url is null) continue;
                var key = ColesExtensionTaskService.ProductKey(url);
                var task = await db.ColesExtensionTasks.SingleOrDefaultAsync(t => t.Key == key, token);
                var priority = listId.HasValue ? 2 : 0;
                if (task is { Status: "Waiting" or "Processing" })
                {
                    task.Priority = Math.Max(task.Priority, priority);
                    continue;
                }
                if (task?.RefreshNotBefore > utc) continue;
                if (task is { Status: "Failed" } && (task.CompletedAt ?? task.ClaimedAt)?.AddHours(options.Value.RetryCooldownHours) > utc) continue;
                if (task is null)
                {
                    task = new() { Key = key, Url = url.AbsoluteUri, CreatedAt = utc };
                    db.ColesExtensionTasks.Add(task);
                }
                else ColesExtensionTaskService.Reset(task);
                task.Priority = priority;
                task.RefreshNotBefore = utc.AddHours(options.Value.RetryCooldownHours);
                queued++;
                // Save each key so duplicate mappings share the same queued task.
                await db.SaveChangesAsync(token);
            }
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            db.ChangeTracker.Clear();
            cursor = mappings[^1].Id;
        }
        return queued;
    }

    internal static Uri? ProductUrl(ShopProduct mapping)
    {
        if (!Uri.TryCreate(mapping.ProductUrl, UriKind.Absolute, out var url) || url.Scheme != "https"
            || !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo)) return null;
        if (mapping.Shop.Code == "coles" && url.Host is "coles.com.au" or "www.coles.com.au"
            && ColesProductParser.ProductCode(url) is { } coles)
            return new("https://www.coles.com.au/product/" + coles);
        if (mapping.Shop.Code == "woolworths" && url.Host is "woolworths.com.au" or "www.woolworths.com.au"
            && WoolworthsProductParser.ProductCode(url) is { } woolworths)
            return new("https://www.woolworths.com.au/shop/productdetails/" + woolworths);
        return null;
    }
}
