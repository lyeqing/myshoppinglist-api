using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class PackDiscoveryService(MyShoppingListDbContext db, TimeProvider clock,
    IOptions<ProductImportOptions> options, IOptions<PriceRefreshOptions> refreshOptions)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    internal IQueryable<ShoppingListProduct> EligibleItems(long accountId, long listId) => db.ShoppingListProducts
        .Where(i => i.ShoppingListId == listId && i.ShoppingList.UserAccountId == accountId
            && i.ShoppingList.UserAccount.IsActive && (!i.ShoppingList.UserAccount.IsTrial || i.ShoppingList.UserAccount.ExpiresDate > Now)
            && !i.ShoppingList.IsArchived && (i.ShoppingList.ExpiresDate == null || i.ShoppingList.ExpiresDate > Now)
            && !i.IsPurchased && !i.IsHidden && !i.Product.IsDeleted);
    internal static bool Active(string status) => status is "Queued" or "Processing" or "WaitingForExtension";
    private bool Recent(PackDiscoveryJob job) => job.Status == "Completed" && job.CompletedDate >= new CatalogueFreshnessService().Cutoff(clock.GetUtcNow())
        && job.CompletedDate <= Now || job.Status == "Failed" && job.NextAttemptDate > Now;

    public async Task<PackDiscoveryView> ReadAsync(long accountId, long listId, long? itemId, CancellationToken token)
    {
        var now = Now;
        var account = await db.ShoppingLists.AsNoTracking().Where(l => l.Id == listId && l.UserAccountId == accountId
            && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now) && l.UserAccount.IsActive
            && (!l.UserAccount.IsTrial || l.UserAccount.ExpiresDate > now)).Select(l => l.UserAccount).SingleOrDefaultAsync(token);
        if (account is null) throw new UserImportException(404, "The shopping list was not found.");
        if (itemId.HasValue && !await db.ShoppingListProducts.AnyAsync(i => i.Id == itemId && i.ShoppingListId == listId, token))
            throw new UserImportException(404, "The shopping list item was not found.");
        var items = await EligibleItems(accountId, listId).AsNoTracking().Include(i => i.Product)
            .Where(i => itemId == null || i.Id == itemId).ToArrayAsync(token);
        var eligible = items.Where(i => Query(i.Product) is not null).Select(i => i.Id).ToArray();
        var shops = await ShoppingPreferencesService.AllowedShops(db, accountId).Where(s => s.Code == "coles" || s.Code == "woolworths").Select(s => s.Id).ToArrayAsync(token);
        var enabled = account.IncludeDifferentPackSizes && options.Value.Enabled;
        var jobs = enabled ? await db.PackDiscoveryJobs.AsNoTracking().Include(j => j.Shop).Include(j => j.ShoppingListProduct).ThenInclude(i => i.Product)
            .Where(j => j.UserAccountId == accountId && j.ShoppingListId == listId && eligible.Contains(j.ShoppingListProductId) && shops.Contains(j.ShopId))
            .OrderBy(j => j.ShoppingListProductId).ThenBy(j => j.Shop.Name).ToArrayAsync(token) : [];
        var canStart = enabled && eligible.Any(id => shops.Any(shop => !jobs.Any(j => j.ShoppingListProductId == id && j.ShopId == shop && (Active(j.Status) || Recent(j)))));
        var reason = !account.IncludeDifferentPackSizes ? "Enable different package sizes in My Stores to search."
            : !options.Value.Enabled ? "Retailer discovery is temporarily unavailable."
            : shops.Length == 0 ? "Choose a supported store in My Stores."
            : eligible.Length == 0 ? "No remaining products with reliable package information are available to search."
            : !canStart && jobs.Any(j => Active(j.Status)) ? "Other-size searches are already in progress."
            : !canStart && jobs.Any(j => j.Status == "Failed") ? "Some checks are waiting for their retry time. See dates below."
            : !canStart ? "Already checked since the latest Wednesday catalogue update. Saved prices keep their check dates." : null;
        return new(accountId, listId, itemId, enabled, canStart, reason, jobs.Select(j => new PackDiscoveryProgress(j.Id,
            j.ShoppingListProductId, j.ShoppingListProduct.Product.Name, j.Shop.Name, j.Status, j.FoundCount, j.ErrorCode, j.UpdatedDate,
            j.Status == "Failed" ? j.NextAttemptDate : null)).ToArray());
    }

    // The family parser removes verified package measurements, retaining all variant wording.
    internal static string? Query(Product product)
    {
        var identity = ProductMatchingService.Identity(product);
        if (string.IsNullOrWhiteSpace(identity.Brand)) return null;
        var family = new PackComparisonService().Read(identity)?.FamilyName;
        if (family is null) return null;
        var query = Providers.ProductSearchQueryBuilder.Build(new() { Name = family, Brand = identity.Brand, Variant = identity.Variant });
        return query is { Length: > 0 and <= 160 } ? query : null;
    }

    public async Task<PackDiscoveryView> RequestAsync(long accountId, long listId, long? itemId, PackDiscoveryRequest request, CancellationToken token)
    {
        if (request.ExpectedAccountId != accountId) throw new UserImportException(409, "Your account changed. Reload your list before searching.");
        var queued = 0; var running = 0; var recent = 0;
        await using (var tx = await db.Database.BeginTransactionAsync(token))
        {
            // Catalogue -> account -> list: same order as catalogue persistence.
            await ProductService.LockCatalogueAsync(db, token);
            await db.UserAccounts.FromSqlInterpolated($"SELECT * FROM \"UserAccounts\" WHERE \"Id\" = {accountId} FOR UPDATE").SingleOrDefaultAsync(token);
            await db.ShoppingLists.FromSqlInterpolated($"SELECT * FROM \"ShoppingLists\" WHERE \"Id\" = {listId} AND \"UserAccountId\" = {accountId} FOR UPDATE").SingleOrDefaultAsync(token);
            var view = await ReadAsync(accountId, listId, itemId, token);
            if (!view.Enabled) throw new UserImportException(409, view.Reason!);
            var items = await EligibleItems(accountId, listId).Include(i => i.Product).Where(i => itemId == null || i.Id == itemId).ToArrayAsync(token);
            var shops = await ShoppingPreferencesService.AllowedShops(db, accountId).Where(s => s.Code == "coles" || s.Code == "woolworths").Select(s => s.Id).ToArrayAsync(token);
            foreach (var item in items.Where(i => Query(i.Product) is not null)) foreach (var shop in shops)
            {
                var job = await db.PackDiscoveryJobs.SingleOrDefaultAsync(j => j.UserAccountId == accountId && j.ShoppingListProductId == item.Id && j.ShopId == shop, token);
                if (job is not null && Active(job.Status)) { running++; continue; }
                if (job is not null && Recent(job)) { recent++; continue; }
                if (job?.FailedTaskId is { } failed)
                {
                    var shared = await db.ColesExtensionTasks.SingleOrDefaultAsync(t => t.Id == failed && t.Status == "Failed", token);
                    if (shared is not null && (shared.RefreshNotBefore == null || shared.RefreshNotBefore <= Now))
                        ColesExtensionTaskService.Reset(shared);
                }
                if (job is null)
                {
                    job = new() { UserAccountId = accountId, ShoppingListId = listId, ShoppingListProductId = item.Id, ProductId = item.ProductId, ShopId = shop };
                    db.PackDiscoveryJobs.Add(job);
                }
                job.Status = "Queued"; job.Attempts = 0; job.FoundCount = 0; job.ErrorCode = null; job.FailedTaskId = null;
                job.CreatedDate = job.UpdatedDate = Now; job.NextAttemptDate = job.CompletedDate = null; job.ClaimToken = null; job.LeaseExpiresDate = null;
                queued++;
            }
            await db.SaveChangesAsync(token); await tx.CommitAsync(token); db.ChangeTracker.Clear();
        }
        return (await ReadAsync(accountId, listId, itemId, token)) with { Queued = queued, AlreadyRunning = running, Recent = recent };
    }

    internal async Task<bool> EligibleAsync(PackDiscoveryJob job, CancellationToken token) =>
        await EligibleItems(job.UserAccountId, job.ShoppingListId).AnyAsync(i => i.Id == job.ShoppingListProductId && i.ProductId == job.ProductId
            && i.ShoppingList.UserAccount.IncludeDifferentPackSizes, token)
        && await ShoppingPreferencesService.AllowsAsync(db, job.UserAccountId, job.ShopId, token);

    public async Task<PackDiscoveryClaim?> ClaimAsync(CancellationToken token)
    {
        var now = Now;
        await db.PackDiscoveryJobs.Where(j => j.Status == "Processing" && j.LeaseExpiresDate <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "Queued").SetProperty(j => j.ClaimToken, (Guid?)null)
                .SetProperty(j => j.LeaseExpiresDate, (DateTime?)null).SetProperty(j => j.NextAttemptDate, (DateTime?)now), token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var job = await db.PackDiscoveryJobs.FromSqlInterpolated($"SELECT * FROM \"PackDiscoveryJobs\" WHERE \"Status\" IN ('Queued','WaitingForExtension') AND (\"NextAttemptDate\" IS NULL OR \"NextAttemptDate\" <= {now}) ORDER BY \"UpdatedDate\", \"Id\" LIMIT 1 FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(token);
        if (job is null) return null;
        if (!await EligibleAsync(job, token)) { job.Status = "Cancelled"; job.UpdatedDate = now; }
        else { job.Status = "Processing"; job.ClaimToken = Guid.NewGuid(); job.LeaseExpiresDate = now.AddMinutes(3); job.UpdatedDate = now; }
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        var claim = job.ClaimToken is { } value ? new PackDiscoveryClaim(job.Id, value) : null;
        db.ChangeTracker.Clear(); return claim;
    }

    internal async Task<PackDiscoveryJob?> ReadClaimAsync(PackDiscoveryClaim claim, CancellationToken token) =>
        await db.PackDiscoveryJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == claim.Id && j.Status == "Processing"
            && j.ClaimToken == claim.Token && j.LeaseExpiresDate > Now, token);

    public async Task FinishAsync(PackDiscoveryClaim claim, string status, int found, string? error, CancellationToken token,
        TimeSpan? retry = null, long? failedTaskId = null)
    {
        var job = await ReadClaimAsync(claim, token);
        if (job is null) return;
        if (!await EligibleAsync(job, token)) { status = "Cancelled"; error = "request_no_longer_eligible"; }
        var attempts = job.Attempts + (status == "Failed" ? 1 : 0);
        if (status == "Failed" && failedTaskId == null && retry.HasValue && attempts < options.Value.MaxAttempts) status = "Queued";
        var now = Now;
        var next = status is "Queued" or "WaitingForExtension" ? now.Add(retry ?? TimeSpan.FromSeconds(30))
            : status == "Failed" ? now.Add(retry ?? TimeSpan.FromHours(refreshOptions.Value.RetryCooldownHours)) : (DateTime?)null;
        await db.PackDiscoveryJobs.Where(j => j.Id == claim.Id && j.Status == "Processing" && j.ClaimToken == claim.Token && j.LeaseExpiresDate > now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status).SetProperty(j => j.Attempts, attempts).SetProperty(j => j.FoundCount, found)
                .SetProperty(j => j.ErrorCode, error).SetProperty(j => j.FailedTaskId, failedTaskId).SetProperty(j => j.UpdatedDate, now)
                .SetProperty(j => j.NextAttemptDate, next).SetProperty(j => j.CompletedDate, Active(status) ? (DateTime?)null : now)
                .SetProperty(j => j.ClaimToken, (Guid?)null).SetProperty(j => j.LeaseExpiresDate, (DateTime?)null), token);
    }
}
