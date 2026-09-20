using System.Data;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed record ShoppingListUpdateResult(ShoppingListItemResponse? Item, int StatusCode, string? Message = null);

public sealed class ShoppingListService(MyShoppingListDbContext db, TimeProvider clock)
{
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
