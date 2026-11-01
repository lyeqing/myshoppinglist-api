using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using OpenIddict.EntityFrameworkCore.Models;

namespace myshoppinglist_api.Services;

public sealed class AccountCredentialService(MyShoppingListDbContext db, IOptions<AuthOptions> auth,
    IOptions<AccountRecoveryOptions> recovery, RecoveryTokenService tokens, EmailOutboxService emails, TimeProvider clock)
{
    public async Task ChangeAsync(long account, long session, ChangePasswordRequest request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != account) throw new UserImportException(409, "Your account changed. Review this request again.");
        Validate(request.NewPassword);
        var candidate = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(u => u.Id == account && u.IsActive && !u.IsTrial, ct);
        if (candidate is null) throw new UserImportException(403, "A registered account is required.");
        if (!PasswordHasher.Verify(request.CurrentPassword, candidate.PasswordHash, candidate.PasswordSalt))
            throw new UserImportException(400, "The current password is incorrect.");
        var credential = PasswordHasher.Hash(request.NewPassword!);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var user = await LockedAsync(account, ct);
            if (user is null || !user.IsActive || user.IsTrial || user.PasswordHash != candidate.PasswordHash || user.PasswordSalt != candidate.PasswordSalt
                || !await db.UserSessions.AnyAsync(s => s.Id == session && s.UserAccountId == account && s.RevokedDate == null && s.ExpiresDate > clock.GetUtcNow().UtcDateTime, ct))
                throw new UserImportException(401, "Sign in again before changing your password.");
            await ApplyAsync(user, credential, ct);
            await tx.CommitAsync(ct);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<long> ResetAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        if (!recovery.Value.Enabled) throw new UserImportException(503, "Password recovery is not available. Contact support for help.");
        var ticket = tokens.Read(request.Token) ?? throw InvalidLink();
        Validate(request.NewPassword);
        // Reject stale/invalid proofs before expensive hashing, then recheck under the account lock.
        var candidate = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ticket.AccountId && u.IsActive && !u.IsTrial, ct);
        if (candidate is null || RecoveryTokenService.Fingerprint(candidate) != ticket.CredentialFingerprint) throw InvalidLink();
        var credential = PasswordHasher.Hash(request.NewPassword!);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var user = await LockedAsync(ticket.AccountId, ct);
            if (user is null || !user.IsActive || user.IsTrial || ticket.ExpiresAt <= clock.GetUtcNow().UtcDateTime
                || RecoveryTokenService.Fingerprint(user) != ticket.CredentialFingerprint) throw InvalidLink();
            await ApplyAsync(user, credential, ct);
            await tx.CommitAsync(ct);
            return user.Id;
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private Task<UserAccount?> LockedAsync(long account, CancellationToken ct) => db.UserAccounts
        .FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id"={account} FOR UPDATE""").SingleOrDefaultAsync(ct);

    private void Validate(string? password)
    {
        if (PasswordPolicy.Error(password, auth.Value.MinimumPasswordLength) is { } error) throw new UserImportException(400, error);
    }
    private static UserImportException InvalidLink() => new(400, "This reset link is invalid, expired or already used. Request a new link.");

    private async Task ApplyAsync(UserAccount user, (string Hash, string Salt) credential, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime; var account = user.Id;
        user.PasswordHash = credential.Hash; user.PasswordSalt = credential.Salt; user.UpdatedDate = now;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountCredentialChanges" ("UserAccountId", "ChangedAt") VALUES ({account}, {now})
            ON CONFLICT ("UserAccountId") DO UPDATE SET "ChangedAt"=EXCLUDED."ChangedAt"
            """, ct);
        await db.UserSessions.Where(s => s.UserAccountId == account && s.RevokedDate == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedDate, now), ct);
        await db.ExtensionDevices.Where(d => d.UserAccountId == account && d.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        await db.AiConnections.Where(c => c.UserAccountId == account && c.RevokedDate == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedDate, now), ct);
        var subject = account.ToString(CultureInfo.InvariantCulture);
        await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Where(a => a.Subject == subject && a.Status == "valid")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "revoked"), ct);
        await db.Set<OpenIddictEntityFrameworkCoreToken>().Where(t => t.Subject == subject && t.Status != "revoked")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "revoked"), ct);
        await db.ExtensionTasks.Where(t => t.UserAccountId == account && (t.Status == "queued" || t.Status == "awaiting_user" || t.Status == "acknowledged" || t.Status == "running"))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "cancelled").SetProperty(t => t.CompletedAt, now).SetProperty(t => t.ErrorCode, "origin_revoked"), ct);
        var hash = EmailOutboxService.AddressHash(user.Email!);
        await db.EmailOutboxMessages.Where(e => e.Kind == "account_recovery" && e.RecipientHash == hash && (e.Status == "queued" || e.Status == "sending"))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, "cancelled").SetProperty(e => e.CompletedAt, now)
                .SetProperty(e => e.ProtectedPayload, (string?)null).SetProperty(e => e.ClaimToken, (Guid?)null).SetProperty(e => e.LeaseExpiresAt, (DateTime?)null), ct);
        emails.AddPasswordNotice(user.Email!);
        await db.SaveChangesAsync(ct);
    }
}
