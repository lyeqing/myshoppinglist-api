using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public sealed class AccountProfileService(MyShoppingListDbContext db, AccountCredentialService credentials,
    SocialAuthenticationService social, EmailOutboxService emails, IOptions<EmailDeliveryOptions> delivery,
    IOptions<AccountRecoveryOptions> recovery, IHostEnvironment environment, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private bool Available => delivery.Value.Enabled && EmailDeliverySetup.ValidOrigin(recovery.Value.WebsiteOrigin, environment.IsDevelopment());
    private static UserImportException Changed() => new(409, "Your account changed. Reload Account settings and try again.");
    private static UserImportException InvalidLink() => new(400, "This email verification link is invalid, expired or already used. Request a new link from Account settings.");

    private async Task<UserAccount> Active(long account, long session, bool locked, CancellationToken ct)
    {
        var query = locked ? db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id"={account} FOR UPDATE""")
            : db.UserAccounts.AsNoTracking().Where(u => u.Id == account);
        var user = await query.SingleOrDefaultAsync(ct);
        if (user is null || !user.IsActive || user.IsTrial || !await db.UserSessions.AnyAsync(s => s.Id == session
            && s.UserAccountId == account && s.RevokedDate == null && s.ExpiresDate > Now, ct))
            throw new UserImportException(401, "Sign in again to manage your account.");
        return user;
    }

    private async Task<AccountProfileView> View(UserAccount user, CancellationToken ct)
    {
        var pending = await db.ExternalAuthFlows.AsNoTracking().Where(f => f.UserAccountId == user.Id
            && f.Purpose == "email_change" && f.ConsumedAt == null && f.ExpiresAt > Now)
            .OrderByDescending(f => f.CreatedAt).FirstOrDefaultAsync(ct);
        return new(user.Id, user.DisplayName, user.Email, user.UpdatedDate, user.PasswordHash is not null,
            Available, pending?.Id, pending?.Email, pending?.ExpiresAt);
    }

    public async Task<AccountProfileView> GetAsync(long account, long session, CancellationToken ct) =>
        await View(await Active(account, session, false, ct), ct);

    public async Task<AccountProfileView> UpdateAsync(long account, long session, UpdateProfileRequest request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != account) throw Changed();
        var name = request.DisplayName?.Trim() ?? "";
        if (name.Length > 200 || name.Any(char.IsControl)) throw new UserImportException(400, "Use a display name of at most 200 characters without control characters, or leave it blank.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await Active(account, session, true, ct);
        if (request.ExpectedUpdatedDate != user.UpdatedDate) throw Changed();
        if (name != user.DisplayName)
        {
            user.DisplayName = name;
            // PostgreSQL timestamp precision is microseconds; preserve a monotonic optimistic revision.
            user.UpdatedDate = new DateTime(Math.Max(Now.Ticks / 10 * 10, user.UpdatedDate.Ticks + 10), DateTimeKind.Utc);
            await db.SaveChangesAsync(ct);
        }
        var result = await View(user, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<AccountProfileView> RequestEmailAsync(long account, long session, RequestEmailChange request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != account) throw Changed();
        if (!Available) throw new UserImportException(503, "Email verification is unavailable. Your email address has not changed.");
        var address = AccountAuthService.NormaliseEmail(request.Email) ?? throw new UserImportException(400, "Enter a valid new email address.");
        // Reuse the existing password proof and five-minute provider reauthentication window.
        if (request.CurrentPassword is not null) await social.Reauthenticate(account, session, request.CurrentPassword, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await Active(account, session, true, ct);
        if (request.ExpectedEmail != user.Email) throw Changed();
        if (address == user.Email) throw new UserImportException(400, "Enter a different email address.");
        if (!await db.UserSessions.AnyAsync(s => s.Id == session && s.ReauthenticatedAt > Now.AddMinutes(-5), ct))
            throw new UserImportException(403, "Verify your current password or a connected sign-in provider before changing your email.");
        await CheckAddress(account, address, ct);
        if (await db.ExternalAuthFlows.AnyAsync(f => f.UserAccountId == account && f.Purpose == "email_change" && f.CreatedAt > Now.AddMinutes(-2), ct))
            throw new UserImportException(429, "A verification link was requested recently. Check your inbox or wait two minutes before requesting another.");
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('recovery-queue-budget'))", ct);
        if (await db.EmailOutboxMessages.CountAsync(e => e.Status == "queued" || e.Status == "sending", ct) >= 10000)
            throw new UserImportException(503, "Email verification is busy. Try again later; your email has not changed.");
        await CancelPending(account, null, ct);
        var raw = SessionToken.Create();
        var flow = new ExternalAuthFlow { UserAccountId = account, UserSessionId = session, Provider = "local", Purpose = "email_change",
            Email = address, TokenHash = SessionToken.Hash(raw), CreatedAt = Now, ExpiresAt = Now.AddMinutes(15) };
        db.ExternalAuthFlows.Add(flow);
        var link = recovery.Value.WebsiteOrigin.TrimEnd('/') + "/account/security#email=" + flow.Id + "." + raw;
        emails.Add("signin_email", new([address], "Verify your new MyShoppingList email address",
            "You requested to change your MyShoppingList email address. Open this link in the same browser while still signed in, then confirm the change:\n\n" + link
            + "\n\nThe link expires in 15 minutes and can be used once. Your current email stays active until you confirm. If you did not request this, ignore this email."), flow.ExpiresAt, flow.Id);
        await db.SaveChangesAsync(ct); var result = await View(user, ct); await tx.CommitAsync(ct); return result;
    }

    private async Task CheckAddress(long account, string address, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"account-register:" + address}, 0))", ct);
        if (await db.UserAccounts.AnyAsync(u => u.Id != account && u.Email == address, ct)
            || await db.ExternalIdentities.AnyAsync(i => i.UserAccountId != account && i.ProviderEmail == address, ct))
            throw new UserImportException(409, "That email address cannot be used. Choose another address or sign in to its existing account.");
    }

    private async Task CancelPending(long account, Guid? id, CancellationToken ct)
    {
        var pending = await db.ExternalAuthFlows.Where(f => f.UserAccountId == account && f.Purpose == "email_change"
            && f.ConsumedAt == null && (id == null || f.Id == id)).Select(f => f.Id).ToArrayAsync(ct);
        await db.ExternalAuthFlows.Where(f => pending.Contains(f.Id)).ExecuteUpdateAsync(s => s.SetProperty(f => f.ConsumedAt, Now), ct);
        await db.EmailOutboxMessages.Where(e => pending.Contains(e.Id) && (e.Status == "queued" || e.Status == "sending"))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, "cancelled").SetProperty(e => e.CompletedAt, Now)
                .SetProperty(e => e.ProtectedPayload, (string?)null).SetProperty(e => e.ClaimToken, (Guid?)null).SetProperty(e => e.LeaseExpiresAt, (DateTime?)null), ct);
    }

    public async Task<AccountProfileView> CancelAsync(long account, long session, CancelEmailChange request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != account) throw Changed();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await Active(account, session, true, ct);
        // A stale cancellation cannot cancel a newer request.
        await CancelPending(account, request.PendingEmailId, ct);
        var result = await View(user, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task CompleteAsync(long account, long session, CompleteEmailChange request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != account) throw Changed();
        if (!Available) throw new UserImportException(503, "Email verification is unavailable. Your email address has not changed.");
        if (request.Token?.Length is not 101 || !Guid.TryParse(request.Token[..36], out var id)
            || request.Token[36] != '.' || !SessionToken.IsValidFormat(request.Token[37..])) throw InvalidLink();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await Active(account, session, true, ct);
        var flow = await db.ExternalAuthFlows.FromSqlInterpolated($"""SELECT * FROM "ExternalAuthFlows" WHERE "Id"={id} FOR UPDATE""").SingleOrDefaultAsync(ct);
        if (flow is null || flow.Purpose != "email_change" || flow.UserAccountId != account || flow.UserSessionId != session
            || flow.ConsumedAt != null || flow.ExpiresAt <= Now || flow.TokenHash != SessionToken.Hash(request.Token[37..])
            || AccountAuthService.NormaliseEmail(flow.Email) is not { } address) throw InvalidLink();
        await CheckAddress(account, address, ct);
        var previous = user.Email;
        // Revoke while the old email is still present, cancelling its recovery messages/proofs.
        await CancelPending(account, null, ct);
        await credentials.RevokeAccessAsync(user, ct);
        user.Email = address; user.EmailVerifiedAt = Now; user.UpdatedDate = Now;
        emails.Add("security_alert", new([address], "Your MyShoppingList email address was changed",
            "Your verified email address is now used for MyShoppingList. Existing sessions and AI/extension connections were disconnected. Your shopping lists were kept."), Now.AddDays(1));
        if (previous is not null) emails.Add("security_alert", new([previous], "Your MyShoppingList email address was changed",
            "Your MyShoppingList email address was changed. Existing sessions and AI/extension connections were disconnected. If you did not make this change, contact support."), Now.AddDays(1));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
