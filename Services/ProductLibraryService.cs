using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class ProductLibraryService(MyShoppingListDbContext db, TimeProvider clock)
{
    private async Task RegisteredAsync(long accountId, CancellationToken ct)
    {
        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.IsActive, ct);
        if (account is null) throw new UserImportException(401, "Sign in to use your saved products.");
        if (account.IsTrial) throw new UserImportException(403, "Create an account to save Favorites and Essentials and keep product history.");
    }

    private IQueryable<Product> Known(long accountId) => db.Products.Where(p => !p.IsDeleted &&
        (db.UserProductActivities.Any(a => a.UserAccountId == accountId && a.ProductId == p.Id)
         || db.UserProductPreferences.Any(a => a.UserAccountId == accountId && a.ProductId == p.Id)
         || db.ShoppingListProducts.Any(i => i.ProductId == p.Id && i.ShoppingList.UserAccountId == accountId)));

    public async Task<ProductPreferenceResponse[]> PreferencesAsync(long accountId, long[] ids, CancellationToken ct)
    {
        await RegisteredAsync(accountId, ct);
        if (ids.Length is < 1 or > 50 || ids.Any(id => id <= 0)) throw new UserImportException(400, "Send between 1 and 50 valid product IDs.");
        return await Known(accountId).Where(p => ids.Contains(p.Id)).Select(p => new ProductPreferenceResponse(p.Id,
            db.UserProductPreferences.Any(f => f.UserAccountId == accountId && f.ProductId == p.Id && f.IsFavorite),
            db.UserProductPreferences.Any(f => f.UserAccountId == accountId && f.ProductId == p.Id && f.IsEssential))).ToArrayAsync(ct);
    }

    public async Task<ProductPreferenceResponse> SavePreferenceAsync(long accountId, long productId, ProductPreferenceUpdate request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != accountId) throw new UserImportException(409, "Your account changed. Reload before saving.");
        if (request.Kind is not ("favorite" or "essential")) throw new UserImportException(400, "Choose Favorite or Essential.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(ct);
            await RegisteredAsync(accountId, ct);
            if (!await Known(accountId).AnyAsync(p => p.Id == productId, ct)) throw new UserImportException(404, "This product was not found in your products.");
            var preference = await db.UserProductPreferences.SingleOrDefaultAsync(p => p.UserAccountId == accountId && p.ProductId == productId, ct);
            if (preference is null)
            {
                preference = new() { UserAccountId = accountId, ProductId = productId };
                db.UserProductPreferences.Add(preference);
            }
            if (request.Kind == "favorite") preference.IsFavorite = request.Selected;
            else preference.IsEssential = request.Selected;
            preference.UpdatedDate = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(productId, preference.IsFavorite, preference.IsEssential);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<ProductLibraryPage> ReadAsync(long accountId, string view, string filter, string? search, int offset, int pageSize, CancellationToken ct)
    {
        await RegisteredAsync(accountId, ct);
        if (view is not ("history" or "favorites" or "essentials") || filter is not ("all" or "purchased" or "unpurchased")
            || offset < 0 || offset > 100000 || pageSize is < 1 or > 50 || search?.Length > 100)
            throw new UserImportException(400, "Invalid product filters. Use a search of at most 100 characters.");
        var query = Known(accountId);
        if (view == "history") query = query.Where(p => db.UserProductActivities.Any(a => a.UserAccountId == accountId && a.ProductId == p.Id));
        if (view == "favorites") query = query.Where(p => db.UserProductPreferences.Any(f => f.UserAccountId == accountId && f.ProductId == p.Id && f.IsFavorite));
        if (view == "essentials") query = query.Where(p => db.UserProductPreferences.Any(f => f.UserAccountId == accountId && f.ProductId == p.Id && f.IsEssential));
        if (filter == "purchased") query = query.Where(p => db.UserProductActivities.Any(a => a.UserAccountId == accountId && a.ProductId == p.Id && a.PurchasedDate != null));
        if (filter == "unpurchased") query = query.Where(p => !db.UserProductActivities.Any(a => a.UserAccountId == accountId && a.ProductId == p.Id && a.PurchasedDate != null));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern, "\\") || p.Brand != null && EF.Functions.ILike(p.Brand, pattern, "\\"));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(p => db.UserProductActivities.Where(a => a.UserAccountId == accountId && a.ProductId == p.Id).Max(a => (DateTime?)a.AddedDate))
            .ThenByDescending(p => p.Id).Skip(offset).Take(pageSize).Select(p => new ProductLibraryItem(
                new(p.Id, p.Name, p.Brand, p.Variant, p.PackQuantity, p.PackSize, p.PackUnit, p.ImageUrl),
                db.UserProductPreferences.Any(f => f.UserAccountId == accountId && f.ProductId == p.Id && f.IsFavorite),
                db.UserProductPreferences.Any(f => f.UserAccountId == accountId && f.ProductId == p.Id && f.IsEssential),
                db.UserProductActivities.Where(a => a.UserAccountId == accountId && a.ProductId == p.Id).Max(a => (DateTime?)a.AddedDate),
                db.UserProductActivities.Where(a => a.UserAccountId == accountId && a.ProductId == p.Id).Max(a => a.PurchasedDate),
                db.UserProductActivities.Count(a => a.UserAccountId == accountId && a.ProductId == p.Id))).ToArrayAsync(ct);
        return new(accountId, rows, total, offset + rows.Length < total ? offset + rows.Length : null);
    }

    public async Task<ProductLibraryAddResponse> AddAsync(long accountId, long productId, long listId, ProductLibraryAddRequest request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != accountId) throw new UserImportException(409, "Your account changed. Reload before adding products.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(ct);
            await RegisteredAsync(accountId, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var list = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {listId} AND "UserAccountId" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(ct);
            if (list is null || list.IsArchived || list.ExpiresDate <= now) throw new UserImportException(404, "Choose an active shopping list.");
            if (!await Known(accountId).AnyAsync(p => p.Id == productId, ct)) throw new UserImportException(404, "This product was not found in your products.");
            var item = await db.ShoppingListProducts.SingleOrDefaultAsync(i => i.ShoppingListId == listId && i.ProductId == productId, ct);
            var exists = item is not null;
            if (item is null)
            {
                item = new() { ShoppingListId = listId, ProductId = productId, Quantity = 1, AddedDate = now, UpdatedDate = now };
                db.ShoppingListProducts.Add(item);
                list.UpdatedDate = now > list.UpdatedDate ? now : list.UpdatedDate.AddTicks(10);
                await TrackActivityAsync(db, accountId, item, ct);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return new(item.Id, exists);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    // Call inside the caller's account-locked transaction, before SaveChanges.
    public static async Task TrackActivityAsync(MyShoppingListDbContext db, long accountId, ShoppingListProduct item, CancellationToken ct)
    {
        var activity = item.Id > 0 ? await db.UserProductActivities.SingleOrDefaultAsync(a => a.ShoppingListProductId == item.Id, ct) : null;
        if (activity is null)
        {
            activity = new() { UserAccountId = accountId, ProductId = item.ProductId, ShoppingListProduct = item, AddedDate = item.AddedDate };
            db.UserProductActivities.Add(activity);
        }
        activity.Quantity = item.Quantity;
        activity.PurchasedDate = item.IsPurchased ? item.PurchasedDate : null;
    }

    public static async Task SeedEssentialsAsync(MyShoppingListDbContext db, ShoppingList list, CancellationToken ct)
    {
        var ids = await db.UserProductPreferences.Where(p => p.UserAccountId == list.UserAccountId && p.IsEssential && !p.Product.IsDeleted).Select(p => p.ProductId).ToArrayAsync(ct);
        foreach (var id in ids)
        {
            var item = new ShoppingListProduct { ShoppingList = list, ProductId = id, Quantity = 1, AddedDate = list.CreatedDate, UpdatedDate = list.CreatedDate };
            db.ShoppingListProducts.Add(item);
            await TrackActivityAsync(db, list.UserAccountId, item, ct);
        }
    }
}
