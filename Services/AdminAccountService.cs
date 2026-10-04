using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class AdminAccountService(MyShoppingListDbContext db, IOptions<AdminOptions> options, TimeProvider clock)
{
    public async Task UpdateAsync(long administrator, long id, AdminAccountUpdate change, CancellationToken ct)
    {
        if (!options.Value.AccountIds.Contains(administrator)) throw new UserImportException(403, "Administrator access required.");
        if (string.IsNullOrWhiteSpace(change.Reason) || change.Reason.Length > 1000)
            throw new UserImportException(400, "Enter a review reason of at most 1000 characters.");
        if (options.Value.AccountIds.Contains(id) && (!change.IsActive || change.ContributionBlocked))
            throw new UserImportException(400, "Administrator accounts cannot be restricted here.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ProductService.LockCatalogueAsync(db, ct);
        var account = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {id} FOR UPDATE""").SingleOrDefaultAsync(ct)
            ?? throw new UserImportException(404, "Account not found.");
        if (account.UpdatedDate != change.ExpectedUpdatedDate) throw new UserImportException(409, "Account changed. Refresh before saving.");
        static string Snapshot(UserAccount a) => JsonSerializer.Serialize(new { a.IsPaid, a.IsActive, a.ContributionBlocked });
        var before = Snapshot(account);
        account.IsPaid = change.IsPaid; account.IsActive = change.IsActive;
        account.ContributionBlocked = change.ContributionBlocked; account.RestrictionReason = change.Reason.Trim();
        account.UpdatedDate = clock.GetUtcNow().UtcDateTime;
        if (!account.IsActive)
            await db.UserSessions.Where(s => s.UserAccountId == id && s.RevokedDate == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedDate, (DateTime?)account.UpdatedDate), ct);
        if (!account.IsActive || account.ContributionBlocked)
            await db.ColesExtensionTasks.Where(t => t.ContributorAccountId == id && t.Status == "Processing")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Waiting").SetProperty(t => t.ClaimToken, (Guid?)null)
                    .SetProperty(t => t.WorkerId, (string?)null).SetProperty(t => t.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(t => t.ContributorAccountId, (long?)null).SetProperty(t => t.Attempts, 0), ct);
        db.AccountAdminAudits.Add(new() { AdministratorId = administrator, AccountId = id, BeforeJson = before,
            AfterJson = Snapshot(account), Reason = change.Reason.Trim(), CreatedAt = account.UpdatedDate });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
