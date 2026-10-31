using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class CurrentListService(MyShoppingListDbContext db, TimeProvider clock)
{
    private async Task AccountAsync(long accountId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.UserAccounts.AnyAsync(a => a.Id == accountId && a.IsActive && (!a.IsTrial || a.ExpiresDate > now), ct))
            throw new UserImportException(401, "Your session is unavailable.");
    }

    public async Task<CurrentListResponse> ReadAsync(long accountId, CancellationToken ct)
    {
        await AccountAsync(accountId, ct);
        var selection = await db.UserCurrentLists.AsNoTracking().SingleOrDefaultAsync(c => c.UserAccountId == accountId, ct);
        var revision = await db.UserCurrentListRevisions.AsNoTracking().Where(r => r.UserAccountId == accountId).Select(r => (long?)r.Version).SingleOrDefaultAsync(ct) ?? 0;
        if (selection is null) return new(revision, null, revision == 0 ? "selection_required" : "selection_unavailable");
        var now = clock.GetUtcNow().UtcDateTime;
        var list = await db.ShoppingLists.AsNoTracking().Where(l => l.Id == selection.ShoppingListId
            && l.UserAccountId == accountId && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now))
            .Select(l => new ShoppingListSummary(l.Id, l.Name, l.CreatedDate, l.UpdatedDate)).SingleOrDefaultAsync(ct);
        return new(Math.Max(selection.Version, revision), list, list is null ? "selection_unavailable" : "selected");
    }

    public async Task<CurrentListResponse> SetAsync(long accountId, CurrentListUpdateRequest request, CancellationToken ct)
    {
        if (accountId != request.ExpectedAccountId) throw new UserImportException(409, "Your account changed. Reload your lists.");
        if (request.ShoppingListId <= 0 || request.ExpectedSelectionVersion < 0) throw new UserImportException(400, "Select a valid shopping list and version.");
        await using var tx = await WriteTransactionScope.BeginAsync(db, ct);
        try
        {
            await db.UserAccounts.FromSqlInterpolated($"SELECT * FROM \"UserAccounts\" WHERE \"Id\" = {accountId} FOR UPDATE").SingleOrDefaultAsync(ct);
            await AccountAsync(accountId, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            if (!await db.ShoppingLists.AnyAsync(l => l.Id == request.ShoppingListId && l.UserAccountId == accountId
                && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now), ct))
                throw new UserImportException(404, "The shopping list was not found.");
            var current = await db.UserCurrentLists.SingleOrDefaultAsync(c => c.UserAccountId == accountId, ct);
            var revision = await db.UserCurrentListRevisions.SingleOrDefaultAsync(r => r.UserAccountId == accountId, ct);
            var version = Math.Max(current?.Version ?? 0, revision?.Version ?? 0);
            if (version != request.ExpectedSelectionVersion)
                throw new UserImportException(409, "Your current list changed. Refresh before selecting again.");
            var changed = current is null || current.ShoppingListId != request.ShoppingListId;
            if (changed) version = checked(version + 1);
            if (current is null) db.UserCurrentLists.Add(new() { UserAccountId = accountId, ShoppingListId = request.ShoppingListId, Version = version, UpdatedDate = now });
            else if (changed || current.Version != version)
            {
                current.ShoppingListId = request.ShoppingListId;
                current.Version = version;
                current.UpdatedDate = now;
            }
            if (revision is null) db.UserCurrentListRevisions.Add(new() { UserAccountId = accountId, Version = version, UpdatedDate = now });
            else if (changed || revision.Version != version) { revision.Version = version; revision.UpdatedDate = now; }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        finally { db.ChangeTracker.Clear(); }
        return await ReadAsync(accountId, ct);
    }
}
