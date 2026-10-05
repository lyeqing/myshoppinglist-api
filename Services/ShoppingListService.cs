using System.Data;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed record ShoppingListUpdateResult(ShoppingListItemResponse? Item, int StatusCode, string? Message = null);
public sealed record ShoppingListDeleteResult(int StatusCode, string? Message = null);

public sealed class ShoppingListService(MyShoppingListDbContext db, TimeProvider clock)
{
    public async Task<ShoppingListPlan?> PlanAsync(long accountId, long listId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var list = await db.ShoppingLists.AsNoTracking().Include(l => l.UserAccount)
            .SingleOrDefaultAsync(l => l.Id == listId && l.UserAccountId == accountId, token);
        if (!Accessible(list?.UserAccount, list, accountId)) return null;

        // Planning includes every saved item; purchased/hidden are in-store preferences only.
        var items = await db.ShoppingListProducts.AsNoTracking().Include(i => i.Product)
            .Where(i => i.ShoppingListId == listId && !i.Product.IsDeleted).OrderByDescending(i => i.Id).ToArrayAsync(token);
        var ids = items.Select(i => i.ProductId).Distinct().ToArray();
        var mappings = await db.ShopProducts.AsNoTracking().Include(m => m.Shop)
            .Where(m => ids.Contains(m.ProductId) && m.IsActive && m.Shop.IsActive).ToArrayAsync(token);
        var mappingIds = mappings.Select(m => m.Id).ToArray();
        var prices = await db.ShopProductPrices.AsNoTracking()
            .Where(p => mappingIds.Contains(p.ShopProductId) && p.ShopLocationId == null).ToArrayAsync(token);
        var shopIds = mappings.Select(m => m.ShopId).Distinct().ToArray();
        var shops = await db.Shops.AsNoTracking()
            .Where(s => s.IsActive && (s.Code == "coles" || s.Code == "woolworths" || shopIds.Contains(s.Id)))
            .OrderBy(s => s.Name).Select(s => new { s.Id, s.Name }).ToArrayAsync(token);
        var now = clock.GetUtcNow();
        var freshness = new CatalogueFreshnessService();
        var refreshKeys = mappings.Select(PriceRefreshService.ProductUrl).Where(u => u is not null)
            .Select(u => ColesExtensionTaskService.ProductKey(u!)).Distinct().ToArray();
        var refreshTasks = await db.ColesExtensionTasks.AsNoTracking().Where(t => refreshKeys.Contains(t.Key))
            .Select(t => new { t.Key, t.Status })
            .ToDictionaryAsync(t => t.Key, token);
        var rows = items.Select(item => new ShoppingListPlanningItem(Response(item),
            mappings.Where(m => m.ProductId == item.ProductId).GroupBy(m => m.ShopId).Select(group =>
            {
                // Prefer a verified identity, then the most recent observation for that identity.
                var candidates = group.OrderByDescending(m => m.MatchType == MatchType.Exact).ThenByDescending(m => m.LastFoundDate).ToArray();
                var exact = candidates.Where(m => m.MatchType == MatchType.Exact).ToArray();
                var eligibleIds = (exact.Length > 0 ? exact : candidates).Select(m => m.Id).ToArray();
                var price = prices.Where(p => eligibleIds.Contains(p.ShopProductId)).OrderByDescending(p => p.CheckedDate).ThenByDescending(p => p.Id).FirstOrDefault();
                var mapping = price is null ? candidates[0] : candidates.Single(m => m.Id == price.ShopProductId);
                var status = mapping.MatchType != MatchType.Exact ? "Match not verified" : InStoreShoppingService.PriceStatus(price, now, freshness);
                string? refreshStatus = null;
                if (!item.IsPurchased && !item.IsHidden && status != "Fresh" && PriceRefreshService.ProductUrl(mapping) is { } url
                    && refreshTasks.TryGetValue(ColesExtensionTaskService.ProductKey(url), out var task))
                    refreshStatus = task.Status switch { "Waiting" => "Waiting", "Processing" => "Updating", "Failed" => "RetryLater", _ => null };
                return new ShoppingListPrice(mapping.ShopId, mapping.Shop.Name,
                    price is { Currency: "AUD", Price: >= 0 } ? price.Price : null, status == "Fresh", status,
                    mapping.ProductUrl, price?.CheckedDate, price?.SpecialDescription, refreshStatus,
                    status == "Fresh" ? InStoreShoppingService.SinglePriceMultibuy(price) : null);
            }).OrderBy(p => p.ShopName).ToArray())).ToArray();
        if (!Accessible(list?.UserAccount, list, accountId)) return null;
        var plan = SummarizePlan(list!.Id, list.Name, rows, shops.Select(s => (s.Id, s.Name)).ToArray());
        await transaction.CommitAsync(token);
        return plan;
    }

    public static ShoppingListPlan SummarizePlan(long id, string name, ShoppingListPlanningItem[] items,
        (long Id, string Name)[] shops)
    {
        ShoppingListBasket Basket(long? shopId, string title)
        {
            decimal subtotal = 0;
            var count = 0;
            var missing = new List<ShoppingListMissingItem>();
            foreach (var row in items)
            {
                var available = row.Prices.Where(p => shopId is null || p.ShopId == shopId).ToArray();
                var best = available.Where(p => p.IncludedInTotal && p.Price.HasValue).OrderBy(p => p.Price).FirstOrDefault();
                if (best is not null)
                {
                    subtotal += best.Price!.Value * row.Item.Quantity;
                    count++;
                }
                else missing.Add(new(row.Item.Id, row.Item.Product.Name,
                    shopId is null ? "No current comparable price" : available.FirstOrDefault()?.Status ?? "No price available"));
            }
            return new(shopId, title, subtotal, count, missing.ToArray());
        }
        return new(id, name, items, Basket(null, "Lowest total across shops"), shops.Select(s => Basket(s.Id, s.Name)).ToArray());
    }

    public async Task<ShoppingListDeleteResult> DeleteAsync(long accountId, long listId, long itemId,
        ShoppingListItemDeleteRequest request, CancellationToken token)
    {
        if (request.ExpectedUpdatedDate == default || request.ExpectedUpdatedDate.Kind != DateTimeKind.Utc)
            return new(400, "ExpectedUpdatedDate must be the UTC timestamp returned with the item.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            // Match the extension's advisory lock before taking account/list row locks.
            var jobIds = await db.ProductImportJobs.AsNoTracking().Where(j => j.UserAccountId == accountId
                && j.ShoppingListId == listId && j.ShoppingListProductId == itemId).OrderBy(j => j.Id).Select(j => j.Id).ToArrayAsync(token);
            foreach (var id in jobIds)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"user-extension-import:" + id}, 0))", token);
            var user = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(token);
            var list = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {listId} AND "UserAccountId" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(token);
            if (!Accessible(user, list, accountId)) return new(404, "The shopping list or item was not found.");
            var item = await db.ShoppingListProducts.SingleOrDefaultAsync(i => i.Id == itemId && i.ShoppingListId == listId, token);
            if (item is null) return new(404, "The shopping list or item was not found.");
            if (item.UpdatedDate != request.ExpectedUpdatedDate) return new(409, "This item changed. Refresh your list before deleting it.");
            var now = clock.GetUtcNow().UtcDateTime;
            var jobs = await db.ProductImportJobs.FromSqlInterpolated($"""SELECT * FROM "ProductImportJobs" WHERE "ShoppingListId" = {listId} AND "ShoppingListProductId" = {itemId} ORDER BY "Id" FOR UPDATE""").ToArrayAsync(token);
            foreach (var job in jobs)
            {
                job.ShoppingListProductId = null;
                if (job.Status is ProductImportJobStatus.Queued or ProductImportJobStatus.Processing)
                {
                    job.Status = ProductImportJobStatus.Cancelled;
                    job.ClaimToken = null;
                    job.LeaseExpiresDate = null;
                    job.NextAttemptDate = null;
                    job.CompletedDate = now;
                    job.ErrorCode = "list_item_removed";
                }
            }
            var linkedIds = jobs.Select(j => j.Id).ToArray();
            await db.UserExtensionImportTasks.Where(t => linkedIds.Contains(t.ProductImportJobId) && t.Status == "Waiting")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Failed").SetProperty(t => t.ErrorCode, "list_item_removed")
                    .SetProperty(t => t.UpdatedDate, now), token);
            // Detach first: the existing FK cascades deletes from list entries to import history.
            await db.SaveChangesAsync(token);
            db.ShoppingListProducts.Remove(item);
            list!.UpdatedDate = now > list.UpdatedDate ? now : list.UpdatedDate.AddTicks(10);
            await db.SaveChangesAsync(token);
            if (!Accessible(user, list, accountId)) return new(404, "The shopping list or item was not found.");
            await transaction.CommitAsync(token);
            return new(204);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<long?> EnsureDefaultAsync(long accountId, CancellationToken token, string? timezone = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            var user = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(token);
            var now = clock.GetUtcNow().UtcDateTime;
            if (user is null || !user.IsActive || user.IsTrial && !(user.ExpiresDate > now)) return null;
            var existing = await db.ShoppingLists.Where(l => l.UserAccountId == accountId && !l.IsArchived
                && (l.ExpiresDate == null || l.ExpiresDate > now)).OrderBy(l => l.Id).Select(l => (long?)l.Id).FirstOrDefaultAsync(token);
            if (existing.HasValue) { await transaction.CommitAsync(token); return existing; }
            var list = new ShoppingList
            {
                UserAccountId = accountId,
                Name = await ShoppingListNameService.NextAsync(db, accountId, now, timezone, token),
                CreatedDate = now,
                UpdatedDate = now,
                ExpiresDate = user.IsTrial ? user.ExpiresDate : null
            };
            db.ShoppingLists.Add(list);
            await db.SaveChangesAsync(token);
            if (user.IsTrial && !(user.ExpiresDate > clock.GetUtcNow().UtcDateTime)) return null;
            await transaction.CommitAsync(token);
            return list.Id;
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<ShoppingListItemPage?> ReadAsync(long accountId, long listId, long? beforeId, int pageSize,
        bool includeHidden, bool includePurchased, CancellationToken token)
    {
        if (pageSize is < 1 or > 50 || beforeId <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var list = await db.ShoppingLists.AsNoTracking().Include(l => l.UserAccount)
            .SingleOrDefaultAsync(l => l.Id == listId && l.UserAccountId == accountId, token);
        if (!Accessible(list?.UserAccount, list, accountId)) return null;
        var items = await db.ShoppingListProducts.AsNoTracking().Include(i => i.Product)
            .Where(i => i.ShoppingListId == listId && !i.Product.IsDeleted && (beforeId == null || i.Id < beforeId)
                && (includeHidden || !i.IsHidden) && (includePurchased || !i.IsPurchased))
            .OrderByDescending(i => i.Id).Take(pageSize + 1).ToListAsync(token);
        if (!Accessible(list?.UserAccount, list, accountId)) return null;
        var page = items.Take(pageSize).Select(Response).ToArray();
        await transaction.CommitAsync(token);
        return new(page, items.Count > pageSize ? page[^1].Id : null);
    }

    public async Task<ShoppingListUpdateResult> UpdateAsync(long accountId, long listId, long itemId,
        ShoppingListItemUpdateRequest request, CancellationToken token)
    {
        if (request.Quantity <= 0) return new(null, 400, "Quantity must be a positive whole number.");
        if (request.Notes?.Length > 4000) return new(null, 400, "Notes must contain at most 4000 characters.");
        if (request.ExpectedUpdatedDate == default || request.ExpectedUpdatedDate.Kind != DateTimeKind.Utc)
            return new(null, 400, "ExpectedUpdatedDate must be the UTC timestamp returned with the item.");
        if (db.ChangeTracker.Entries().Any()) throw new InvalidOperationException("List editing requires a clean context.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            // Shared relative lock order with source persistence; never acquire the catalogue lock afterwards.
            var user = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(token);
            var list = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {listId} AND "UserAccountId" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(token);
            if (!Accessible(user, list, accountId)) return Missing();
            var item = await db.ShoppingListProducts.FromSqlInterpolated($"""SELECT * FROM "ShoppingListProducts" WHERE "Id" = {itemId} AND "ShoppingListId" = {listId} FOR UPDATE""").SingleOrDefaultAsync(token);
            if (item is null) return Missing();
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == item.ProductId && !p.IsDeleted, token);
            if (product is null) return Missing();
            if (item.UpdatedDate != request.ExpectedUpdatedDate)
                return new(null, 409, "This item changed. Reload it before saving your edits.");
            if (item.Quantity != request.Quantity || item.Notes != request.Notes || item.IsPurchased != request.IsPurchased || item.IsHidden != request.IsHidden)
            {
                var now = new DateTime(clock.GetUtcNow().UtcTicks / 10 * 10, DateTimeKind.Utc);
                if (item.IsPurchased != request.IsPurchased) item.PurchasedDate = request.IsPurchased ? now : null;
                item.Quantity = request.Quantity; item.Notes = request.Notes;
                item.IsPurchased = request.IsPurchased; item.IsHidden = request.IsHidden;
                // PostgreSQL microsecond precision; successive edits must have distinct versions even with a frozen clock.
                item.UpdatedDate = now > item.UpdatedDate ? now : item.UpdatedDate.AddTicks(10);
                if (item.UpdatedDate > list!.UpdatedDate) list.UpdatedDate = item.UpdatedDate;
                await db.SaveChangesAsync(token);
            }
            if (!Accessible(user, list, accountId)) return Missing();
            var response = Response(item, product);
            await transaction.CommitAsync(token);
            return new(response, 200);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private bool Accessible(UserAccount? user, ShoppingList? list, long accountId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return user is { IsActive: true } && (!user.IsTrial || user.ExpiresDate > now)
            && list is { IsArchived: false } && list.UserAccountId == accountId
            && (list.ExpiresDate == null || list.ExpiresDate > now);
    }
    private static ShoppingListUpdateResult Missing() => new(null, 404, "The shopping list or item was not found.");
    private static ShoppingListItemResponse Response(ShoppingListProduct item) => Response(item, item.Product);
    private static ShoppingListItemResponse Response(ShoppingListProduct item, Product product) => new(item.Id, item.ShoppingListId,
        new(product.Id, product.Name, product.Brand, product.Variant, product.PackQuantity, product.PackSize, product.PackUnit, product.ImageUrl),
        item.Quantity, item.Notes, item.IsPurchased, item.PurchasedDate, item.IsHidden, item.PreferredShopId, item.AddedDate, item.UpdatedDate);
}
