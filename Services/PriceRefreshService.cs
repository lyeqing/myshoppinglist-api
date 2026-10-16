using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class PriceRefreshService(MyShoppingListDbContext db, TimeProvider clock,
    CatalogueFreshnessService freshness, IOptions<PriceRefreshOptions> options)
{
    // Retained for existing callers; interactive endpoints use the detailed response.
    public async Task<int?> RequestAsync(long accountId, long listId, CancellationToken token) =>
        (await RequestRefreshAsync(accountId, listId, null, null, token))?.Queued;

    public async Task<PriceRefreshResult?> RequestRefreshAsync(long accountId, long listId, long? itemId,
        string? timezone, CancellationToken token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.ShoppingLists.AnyAsync(l => l.Id == listId && l.UserAccountId == accountId
            && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now) && l.UserAccount.IsActive
            && (!l.UserAccount.IsTrial || l.UserAccount.ExpiresDate > now), token)) return null;
        if (itemId.HasValue && !await db.ShoppingListProducts.AnyAsync(i => i.Id == itemId
            && i.ShoppingListId == listId && !i.Product.IsDeleted, token)) return null;
        return await ScanCoreAsync(token, listId, accountId, itemId, LocalZone(timezone));
    }

    private static TimeZoneInfo LocalZone(string? timezone)
    {
        if (timezone is { Length: > 0 and <= 100 })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timezone); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    internal static bool CheckedToday(DateTime? checkedAt, DateTimeOffset now, TimeZoneInfo zone) =>
        checkedAt is { } value && value <= now.UtcDateTime
        && TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), zone).Date
            == TimeZoneInfo.ConvertTime(now, zone).Date;

    public async Task<int> ScanAsync(CancellationToken token, long? listId = null, long? accountId = null) =>
        (await ScanCoreAsync(token, listId, accountId, null, null)).Queued;

    private async Task<PriceRefreshResult> ScanCoreAsync(CancellationToken token, long? listId,
        long? accountId, long? itemId, TimeZoneInfo? manualZone)
    {
        if (!options.Value.Enabled && manualZone is null) return new(0, 0, 0, 0);
        var running = 0;
        var current = 0;
        var deferred = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
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
                && (itemId == null || i.Id == itemId)
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
                var url = ProductUrl(mapping);
                if (url is null) continue;
                var key = ColesExtensionTaskService.ProductKey(url);
                if (!seen.Add(key)) continue;
                var task = await db.ColesExtensionTasks.SingleOrDefaultAsync(t => t.Key == key, token);
                var priority = listId.HasValue ? 2 : 0;
                if (task is { Status: "Waiting" or "Processing" })
                {
                    task.Priority = Math.Max(task.Priority, priority);
                    running++;
                    continue;
                }
                // An unavailable check is a completed observation too; don't repeatedly queue it.
                var checkedAt = price?.CheckedDate ?? mapping.LastCheckedDate;
                var recent = manualZone is null ? freshness.IsFresh(checkedAt, now)
                    : CheckedToday(checkedAt, now, manualZone);
                if (recent && !(price?.SpecialEndDate <= utc)) { current++; continue; }
                if (!options.Value.Enabled || task?.RefreshNotBefore > utc
                    || task is { Status: "Failed" } && (task.CompletedAt ?? task.ClaimedAt)?.AddHours(options.Value.RetryCooldownHours) > utc)
                { deferred++; continue; }
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
        return new(queued, running, current, deferred);
    }

    internal static async Task<Dictionary<long, string>> ReadStatusesAsync(MyShoppingListDbContext db,
        IEnumerable<ShopProduct> mappings, DateTime utc, CancellationToken token)
    {
        var entries = mappings.Select(m => (Mapping: m, Url: ProductUrl(m))).Where(e => e.Url is not null).ToArray();
        var keys = entries.Select(e => ColesExtensionTaskService.ProductKey(e.Url!)).Distinct().ToArray();
        var tasks = await db.ColesExtensionTasks.AsNoTracking().Where(t => keys.Contains(t.Key))
            .Select(t => new { t.Key, t.Status, t.CompletedAt, t.ClaimedAt }).ToDictionaryAsync(t => t.Key, token);
        var paused = await db.RetailerWorkloadStates.AsNoTracking().Where(s => s.PausedUntil > utc).Select(s => s.Retailer).ToArrayAsync(token);
        var result = new Dictionary<long, string>();
        foreach (var entry in entries)
        {
            if (!tasks.TryGetValue(ColesExtensionTaskService.ProductKey(entry.Url!), out var task)) continue;
            var status = task.Status switch
            {
                "Waiting" when paused.Contains(entry.Mapping.Shop.Code) => "Paused",
                "Waiting" => "Waiting",
                "Processing" => "Updating",
                "Failed" when entry.Mapping.LastCheckedDate is null
                    || (task.CompletedAt ?? task.ClaimedAt) >= entry.Mapping.LastCheckedDate => "RetryLater",
                _ => null
            };
            if (status is not null) result[entry.Mapping.Id] = status;
        }
        return result;
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
