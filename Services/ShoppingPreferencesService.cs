using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;

namespace myshoppinglist_api.Services;

public sealed class ShoppingPreferencesService(MyShoppingListDbContext db, TimeProvider clock, RetailerProviderRegistry registry)
{
    // Keep this as an IQueryable so callers filter before loading prices or starting retailer work.
    public static IQueryable<Shop> AllowedShops(MyShoppingListDbContext db, long accountId) =>
        db.Shops.Where(s => s.IsActive && db.UserAccounts.Any(a => a.Id == accountId && a.IsActive
            && (!a.CompareOnlyPreferredShops || db.UserPreferredShops.Any(p => p.UserAccountId == accountId && p.ShopId == s.Id && p.IsEnabled))));

    public static Task<bool> AllowsAsync(MyShoppingListDbContext db, long accountId, long shopId, CancellationToken ct) =>
        AllowedShops(db, accountId).AnyAsync(s => s.Id == shopId, ct);

    public static async Task<ComparisonStoreContext> ContextAsync(MyShoppingListDbContext db, long accountId, CancellationToken ct) =>
        new(await db.UserAccounts.Where(a => a.Id == accountId).Select(a => a.CompareOnlyPreferredShops).SingleAsync(ct),
            await AllowedShops(db, accountId).OrderBy(s => s.Name).Select(s => s.Name).ToArrayAsync(ct));

    private async Task<UserAccount> AccountAsync(long accountId, CancellationToken ct) =>
        await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.IsActive
            && (!a.IsTrial || a.ExpiresDate > clock.GetUtcNow().UtcDateTime), ct)
        ?? throw new UserImportException(401, "Your session is unavailable.");

    public async Task<ShoppingPreferencesResponse> ReadAsync(long accountId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var account = await AccountAsync(accountId, ct);
        var shops = await db.Shops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Id).ToArrayAsync(ct);
        var preferences = await db.UserPreferredShops.AsNoTracking().Where(p => p.UserAccountId == accountId && p.Shop.IsActive)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.ShopId).Select(p => new PreferredShopSelection(p.ShopId, p.IsEnabled)).ToArrayAsync(ct);
        var result = new ShoppingPreferencesResponse(account.Id, account.CompareOnlyPreferredShops, account.IncludeDifferentPackSizes,
            account.IncludeDifferentBrands, preferences, shops.Select(s => new ShoppingPreferenceShop(s.Id, s.Name, s.Code,
                registry.FindByCode(s.Code)?.IsImplemented == true)).ToArray(), account.UpdatedDate);
        await AccountAsync(accountId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<ShoppingPreferencesResponse> SaveAsync(long accountId, ShoppingPreferencesUpdate request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != accountId) throw new UserImportException(409, "Your account changed. Reload settings before saving again.");
        if (request.Shops is null || request.Shops.Length > 100 || request.Shops.Any(s => s is null || s.ShopId <= 0)
            || request.Shops.Select(s => s.ShopId).Distinct().Count() != request.Shops.Length
            || request.ExpectedUpdatedDate == default || request.ExpectedUpdatedDate.Kind != DateTimeKind.Utc)
            throw new UserImportException(400, "Send valid, unique stores and the current settings timestamp.");
        if (request.CompareOnlyPreferredShops && !request.Shops.Any(s => s.IsEnabled))
            throw new UserImportException(400, "Choose at least one enabled store, or compare all stores.");
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Use the same lock order as imports and refreshes before changing their eligibility.
            await ProductService.LockCatalogueAsync(db, ct);
            var account = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            if (account is null || !account.IsActive || account.IsTrial && !(account.ExpiresDate > now))
                throw new UserImportException(401, "Your session is unavailable.");
            if (account.UpdatedDate != request.ExpectedUpdatedDate) throw new UserImportException(409, "Your settings changed. Reload settings before saving again.");
            var ids = request.Shops.Select(s => s.ShopId).ToArray();
            if (await db.Shops.CountAsync(s => s.IsActive && ids.Contains(s.Id), ct) != ids.Length)
                throw new UserImportException(400, "One or more stores are unavailable. Reload settings and try again.");
            await db.UserPreferredShops.Where(p => p.UserAccountId == accountId).ExecuteDeleteAsync(ct);
            db.UserPreferredShops.AddRange(request.Shops.Select((s, index) => new UserPreferredShop
            { UserAccountId = accountId, ShopId = s.ShopId, IsEnabled = s.IsEnabled, DisplayOrder = index }));
            account.CompareOnlyPreferredShops = request.CompareOnlyPreferredShops;
            account.IncludeDifferentPackSizes = request.IncludeDifferentPackSizes;
            account.IncludeDifferentBrands = request.IncludeDifferentBrands;
            account.UpdatedDate = new DateTime(Math.Max(now.Ticks / 10 * 10, account.UpdatedDate.Ticks + 10), DateTimeKind.Utc);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        return await ReadAsync(accountId, ct);
    }
}
