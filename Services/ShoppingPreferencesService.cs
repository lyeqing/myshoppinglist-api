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

    // Use the same publication requirements as the customer branch directory. Saving a preference
    // never turns a directory record into a source of branch-specific prices.
    private static bool Available(ShopLocation branch, DateTime now) =>
        branch.IsActive && branch.Shop.IsActive && branch.VerifiedAt is not null && branch.VerifiedAt <= now
        && branch.VerifiedByAccountId is not null && branch.SourceReference is not null && branch.DirectoryKey is not null;

    private static PreferredShopLocationResponse BranchResponse(ShopLocation branch, DateTime now)
    {
        var available = Available(branch, now);
        return new(new(branch.Id, branch.ShopId, branch.Shop.Name, branch.StoreCode, branch.Name,
            branch.Address1, branch.Address2, branch.Suburb, branch.State, branch.Postcode, branch.VerifiedAt),
            available, available ? null : "This branch is no longer available. Remove it or choose another branch.");
    }

    public async Task<ShoppingPreferencesResponse> ReadAsync(long accountId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var account = await AccountAsync(accountId, ct);
        var shops = await db.Shops.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Id).ToArrayAsync(ct);
        var preferences = await db.UserPreferredShops.AsNoTracking().Where(p => p.UserAccountId == accountId && p.Shop.IsActive)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.ShopId).Select(p => new PreferredShopSelection(p.ShopId, p.IsEnabled)).ToArrayAsync(ct);
        var branches = await db.UserPreferredShopLocations.AsNoTracking().Where(p => p.UserAccountId == accountId)
            .Include(p => p.ShopLocation).ThenInclude(l => l.Shop)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.ShopLocationId).ToArrayAsync(ct);
        var result = new ShoppingPreferencesResponse(account.Id, account.CompareOnlyPreferredShops, account.IncludeDifferentPackSizes,
            account.IncludeDifferentBrands, preferences, shops.Select(s =>
            {
                var capability = registry.FindByCode(s.Code);
                return new ShoppingPreferenceShop(s.Id, s.Name, s.Code, capability?.IsImplemented == true,
                    capability?.CapabilityStatus ?? "NotImplemented", capability?.CapabilityMessage ?? "Comparison not available yet.");
            }).ToArray(), account.UpdatedDate, branches.Select(p => BranchResponse(p.ShopLocation, clock.GetUtcNow().UtcDateTime)).ToArray());
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
        if (request.Branches is not null && (request.Branches.Length > 100
            || request.Branches.Any(b => b is null || b.ShopId <= 0 || b.ShopLocationId <= 0)
            || request.Branches.Select(b => b.ShopLocationId).Distinct().Count() != request.Branches.Length))
            throw new UserImportException(400, "Send at most 100 unique branch selections with valid retailer and branch IDs.");
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
            // Omitted/null branches preserve selections made by newer clients. An explicit empty
            // array clears them. Retired selections may be retained, but cannot be newly added.
            if (request.Branches is not null)
            {
                var saved = await db.UserPreferredShopLocations.Where(p => p.UserAccountId == accountId).ToArrayAsync(ct);
                var selectedIds = request.Branches.Select(b => b.ShopLocationId).ToArray();
                var locations = await db.ShopLocations.AsNoTracking().Include(l => l.Shop)
                    .Where(l => selectedIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
                foreach (var selection in request.Branches)
                {
                    if (!locations.TryGetValue(selection.ShopLocationId, out var branch) || branch.ShopId != selection.ShopId)
                        throw new UserImportException(400, "One or more branches do not belong to the selected retailer. Reload settings and try again.");
                    if (!saved.Any(p => p.ShopLocationId == branch.Id)
                        && (!ids.Contains(branch.ShopId) || !Available(branch, now)))
                        throw new UserImportException(400, "Choose an active, verified branch from one of your saved supermarkets.");
                }
                db.UserPreferredShopLocations.RemoveRange(saved.Where(p => !selectedIds.Contains(p.ShopLocationId)));
                for (var index = 0; index < request.Branches.Length; index++)
                {
                    var id = request.Branches[index].ShopLocationId;
                    var preference = saved.SingleOrDefault(p => p.ShopLocationId == id);
                    if (preference is null)
                        db.UserPreferredShopLocations.Add(new() { UserAccountId = accountId, ShopLocationId = id, DisplayOrder = index });
                    else preference.DisplayOrder = index;
                }
            }
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
