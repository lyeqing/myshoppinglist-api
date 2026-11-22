using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

// Provider facts reach this service only after a maintained server handler validates its response.
public sealed record ExternalIdentityProof(string Provider, string Subject, string? Email, string? Name);
public sealed record ExternalCompletion(AccountAuthResult? Authentication, string Outcome);

public sealed class SocialAuthenticationService(MyShoppingListDbContext db, AccountAuthService authentication,
    AccountCredentialService credentials, EmailOutboxService emails, IOptions<SocialAuthenticationOptions> options,
    IOptions<AuthOptions> authOptions, IOptions<EmailDeliveryOptions> delivery, TimeProvider clock,
    ILogger<SocialAuthenticationService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static UserImportException Restart() => new(409, "This sign-in request expired or your account changed. Start again.");
    private static UserImportException LinkRequired() => new(409, "Sign in with an existing method, then connect this provider in account security. Accounts are never merged automatically.");
    private Task<UserAccount?> LockAccount(long id, CancellationToken ct) => db.UserAccounts
        .FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id"={id} FOR UPDATE""").SingleOrDefaultAsync(ct);

    private async Task<UserAccount> ActiveSession(long account, long session, bool fresh, CancellationToken ct)
    {
        var user = await LockAccount(account, ct);
        var login = await db.UserSessions.SingleOrDefaultAsync(s => s.Id == session && s.UserAccountId == account, ct);
        if (user is null || !user.IsActive || login is null || login.RevokedDate is not null || login.ExpiresDate <= Now
            || user.IsTrial && !(user.ExpiresDate > Now)) throw Restart();
        if (fresh && (user.IsTrial || !(login.ReauthenticatedAt > Now.AddMinutes(-5))))
            throw new UserImportException(403, "Verify an existing sign-in method before changing account security.");
        return user;
    }

    public async Task<SocialMethodsView> Methods(long account, long session, CancellationToken ct)
    {
        var user = await db.UserAccounts.AsNoTracking().SingleAsync(u => u.Id == account, ct);
        if (user.IsTrial) throw new UserImportException(403, "Register to manage sign-in methods.");
        var connected = await db.ExternalIdentities.Where(x => x.UserAccountId == account).Select(x => x.Provider).ToArrayAsync(ct);
        var recent = await db.UserSessions.AnyAsync(s => s.Id == session && s.UserAccountId == account
            && s.RevokedDate == null && s.ExpiresDate > Now && s.ReauthenticatedAt > Now.AddMinutes(-5), ct);
        return new(user.PasswordHash is not null, user.PasswordHash is null, delivery.Value.Enabled, recent,
            SocialAuthenticationOptions.Names.Select(p => new SocialProviderView(p, options.Value.Available(p), connected.Contains(p))).ToArray());
    }

    public async Task<Guid> Start(SocialStartRequest request, long? account, long? session, CancellationToken ct)
    {
        if (!options.Value.Available(request.Provider)) throw new UserImportException(503, "This sign-in provider is not available yet.");
        if (request.Purpose is not ("signin" or "link" or "reauthenticate")) throw new UserImportException(400, "Choose a valid sign-in action.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (account is { } id && session is { } sid)
        {
            var user = await ActiveSession(id, sid, request.Purpose == "link", ct);
            if (request.ExpectedAccountId != id) throw Restart();
            if (request.Purpose == "signin" && !user.IsTrial) throw new UserImportException(409, "You are already signed in. Manage sign-in methods in account security.");
            if (request.Purpose != "signin" && user.IsTrial) throw new UserImportException(403, "Register before connecting additional sign-in methods.");
            if (request.Purpose == "reauthenticate" && !await db.ExternalIdentities.AnyAsync(x => x.UserAccountId == id && x.Provider == request.Provider, ct))
                throw new UserImportException(400, "Verify a provider that is already connected to your account.");
        }
        else if (request.Purpose != "signin" || account.HasValue != session.HasValue || request.ExpectedAccountId is not null) throw Restart();
        var now = Now;
        await db.ExternalAuthFlows.Where(x => x.ExpiresAt < now.AddDays(-1)).OrderBy(x => x.ExpiresAt).Take(100).ExecuteDeleteAsync(ct);
        if (await db.ExternalAuthFlows.CountAsync(x => x.ConsumedAt == null && x.ExpiresAt > now && x.UserAccountId == account, ct) >= (account is null ? 1000 : 10))
            throw new UserImportException(429, "Too many sign-in requests. Wait a few minutes and try again.");
        var flow = new ExternalAuthFlow { UserAccountId = account, UserSessionId = session, Provider = request.Provider,
            Purpose = request.Purpose, CreatedAt = now, ExpiresAt = now.AddMinutes(10) };
        db.Add(flow); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return flow.Id;
    }

    public async Task Reauthenticate(long account, long session, string? password, CancellationToken ct)
    {
        var user = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == account, ct);
        if (user is null || user.IsTrial || password?.Length > 1024 || !PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt))
            throw new UserImportException(400, "The current password is incorrect. You can also verify a connected provider.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await ActiveSession(account, session, false, ct);
        if (locked.PasswordHash != user.PasswordHash || locked.PasswordSalt != user.PasswordSalt) throw Restart();
        await db.UserSessions.Where(x => x.Id == session).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReauthenticatedAt, Now), ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ExternalCompletion> Complete(Guid flowId, ExternalIdentityProof proof, long? account, long? session,
        bool leaveTrialSeparate, CancellationToken ct, string? timezone = null)
    {
        if (!options.Value.Available(proof.Provider) || string.IsNullOrWhiteSpace(proof.Subject) || proof.Subject.Length > 512) throw Restart();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Subject locks precede account locks in all linking/completion paths.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"external:" + proof.Provider + ":" + proof.Subject}, 0))", ct);
        UserAccount? source = null;
        if (account is { } owner && session is { } sid) source = await ActiveSession(owner, sid, false, ct);
        var flow = await db.ExternalAuthFlows.FromSqlInterpolated($"""SELECT * FROM "ExternalAuthFlows" WHERE "Id"={flowId} FOR UPDATE""").SingleOrDefaultAsync(ct);
        if (flow is null || flow.ConsumedAt is not null || flow.ExpiresAt <= Now || flow.Provider != proof.Provider
            || flow.UserAccountId != account || flow.UserSessionId != session || flow.Purpose == "email") throw Restart();
        if (flow.Purpose == "link" && account is { } linking && session is { } linkingSession)
            await ActiveSession(linking, linkingSession, true, ct);
        var identity = await db.ExternalIdentities.SingleOrDefaultAsync(x => x.Provider == proof.Provider && x.Subject == proof.Subject, ct);
        var email = AccountAuthService.NormaliseEmail(proof.Email);
        if (flow.Purpose is "link" or "reauthenticate")
        {
            if (source is null || source.IsTrial) throw Restart();
            if (identity is not null && identity.UserAccountId != account) throw new UserImportException(409, "This provider account is connected elsewhere. Sign in with another account or provider.");
            if (flow.Purpose == "reauthenticate")
            {
                if (identity is null) throw new UserImportException(403, "Use the provider account already connected to this account.");
                await db.UserSessions.Where(x => x.Id == session).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReauthenticatedAt, Now), ct);
            }
            else
            {
                if (identity is null)
                {
                    if (await db.ExternalIdentities.AnyAsync(x => x.UserAccountId == account && x.Provider == proof.Provider, ct))
                        throw new UserImportException(409, "Another account from this provider is already connected. Disconnect it before connecting a replacement.");
                    db.ExternalIdentities.Add(new() { UserAccountId = source.Id, Provider = proof.Provider, Subject = proof.Subject, ProviderEmail = email, CreatedAt = Now });
                    SecurityNotice(source, "A sign-in provider was connected to your MyShoppingList account.");
                }
                await db.UserSessions.Where(x => x.Id == session).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReauthenticatedAt, (DateTime?)null), ct);
            }
            flow.ConsumedAt = Now; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            logger.LogInformation("Social authentication outcome {Outcome}", flow.Purpose == "link" ? "provider_connected" : "reauthenticated");
            return new(null, flow.Purpose == "link" ? "connected" : "verified");
        }
        if (identity is not null)
        {
            if (source?.IsTrial == true && !leaveTrialSeparate) return new(null, "confirm_trial_switch");
            var user = await LockAccount(identity.UserAccountId, ct);
            if (user is null || !user.IsActive || user.IsTrial) throw new UserImportException(403, "This account is unavailable.");
            flow.ConsumedAt = Now;
            var (login, raw) = authentication.NewSession(user, Now);
            db.UserSessions.Add(login);
            var list = await db.ShoppingLists.Where(l => l.UserAccountId == user.Id && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > Now))
                .OrderByDescending(l => db.UserCurrentLists.Any(c => c.UserAccountId == user.Id && c.ShoppingListId == l.Id))
                .ThenBy(l => l.Id).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            logger.LogInformation("Social authentication outcome {Outcome}", "signed_in");
            return new(new(new(new(user.Id, user.DisplayName, false, user.ExpiresDate, user.IsPaid), list, login.ExpiresDate), raw), "signed_in");
        }
        if (email is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"account-register:" + email}, 0))", ct);
            if (await db.UserAccounts.AnyAsync(x => x.Email == email, ct) || await db.ExternalIdentities.AnyAsync(x => x.ProviderEmail == email, ct)) throw LinkRequired();
        }
        flow.ConsumedAt = Now; await db.SaveChangesAsync(ct);
        var displayName = proof.Name?.Trim();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 200 || displayName.Any(char.IsControl)) displayName = string.Empty;
        var result = await authentication.RegisterExternalAsync(displayName, new() { Provider = proof.Provider, Subject = proof.Subject,
            ProviderEmail = email, CreatedAt = Now }, account, session, ct, timezone);
        if (result.Response is null) throw new UserImportException(result.StatusCode, result.Message!);
        await tx.CommitAsync(ct);
        logger.LogInformation("Social authentication outcome {Outcome}", account is null ? "registered" : "trial_converted");
        return new(result, account is null ? "registered" : "trial_saved");
    }

    public async Task Disconnect(long account, long session, string provider, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await ActiveSession(account, session, true, ct);
        var identity = await db.ExternalIdentities.SingleOrDefaultAsync(x => x.UserAccountId == account && x.Provider == provider, ct)
            ?? throw new UserImportException(404, "This provider is not connected.");
        // Disabled provider configurations cannot be the sole remaining usable method.
        var other = await db.ExternalIdentities.Where(x => x.UserAccountId == account && x.Id != identity.Id).Select(x => x.Provider).ToArrayAsync(ct);
        if (user.PasswordHash is null && !other.Any(options.Value.Available))
            throw new UserImportException(409, "Connect another available sign-in method or add a verified email and password before disconnecting this provider.");
        db.Remove(identity);
        SecurityNotice(user, "A sign-in provider was disconnected. Existing sessions and AI/extension connections were revoked.");
        await credentials.RevokeAccessAsync(user, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        logger.LogInformation("Social authentication outcome {Outcome}", "provider_disconnected");
    }

    public async Task BeginPassword(long account, long session, string? value, CancellationToken ct)
    {
        if (!delivery.Value.Enabled) throw new UserImportException(503, "Email verification is not available. Keep a connected sign-in provider and try again later.");
        var email = AccountAuthService.NormaliseEmail(value) ?? throw new UserImportException(400, "Enter a valid email address.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await ActiveSession(account, session, true, ct);
        if (user.PasswordHash is not null) throw new UserImportException(409, "Use the existing change-password form.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"account-register:" + email}, 0))", ct);
        if (await db.UserAccounts.AnyAsync(x => x.Email == email && x.Id != account, ct)) throw LinkRequired();
        if (await db.ExternalAuthFlows.AnyAsync(x => x.UserAccountId == account && x.Purpose == "email" && x.CreatedAt > Now.AddMinutes(-2), ct))
            throw new UserImportException(429, "A verification link was requested recently. Check your inbox or wait two minutes.");
        var raw = SessionToken.Create();
        var flow = new ExternalAuthFlow { UserAccountId = account, UserSessionId = session, Provider = "local", Purpose = "email",
            Email = email, TokenHash = SessionToken.Hash(raw), CreatedAt = Now, ExpiresAt = Now.AddMinutes(15) };
        db.ExternalAuthFlows.Add(flow);
        var link = options.Value.WebsiteOrigin.TrimEnd('/') + "/account/social#verify=" + flow.Id + "." + raw;
        emails.Add("signin_email", new([email], "Verify your MyShoppingList sign-in email",
            "You requested an email/password sign-in method. Open this link in the browser where you made the request, while signed in:\n\n" + link
            + "\n\nIt expires in 15 minutes. Ignore this email if you did not request it."), flow.ExpiresAt);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task FinishPassword(long account, long session, SocialPasswordRequest request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != account || request.Token?.Length is not 101 || !Guid.TryParse(request.Token[..36], out var id)
            || request.Token[36] != '.' || !SessionToken.IsValidFormat(request.Token[37..])) throw Restart();
        if (PasswordPolicy.Error(request.NewPassword, authOptions.Value.MinimumPasswordLength) is { } error) throw new UserImportException(400, error);
        var credential = PasswordHasher.Hash(request.NewPassword!);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await ActiveSession(account, session, false, ct);
        var flow = await db.ExternalAuthFlows.FromSqlInterpolated($"""SELECT * FROM "ExternalAuthFlows" WHERE "Id"={id} FOR UPDATE""").SingleOrDefaultAsync(ct);
        if (user.PasswordHash is not null || flow is null || flow.Purpose != "email" || flow.UserAccountId != account || flow.UserSessionId != session
            || flow.ConsumedAt is not null || flow.ExpiresAt <= Now || flow.TokenHash != SessionToken.Hash(request.Token[37..])) throw Restart();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"account-register:" + flow.Email}, 0))", ct);
        if (await db.UserAccounts.AnyAsync(x => x.Email == flow.Email && x.Id != account, ct)) throw LinkRequired();
        user.Email = flow.Email; user.EmailVerifiedAt = Now; flow.ConsumedAt = Now;
        await credentials.ApplyAsync(user, credential, ct); await tx.CommitAsync(ct);
        logger.LogInformation("Social authentication outcome {Outcome}", "email_password_added");
    }

    private void SecurityNotice(UserAccount user, string message)
    {
        if (delivery.Value.Enabled && user.Email is { } email)
            emails.Add("security_alert", new([email], "MyShoppingList sign-in methods changed", message + " If this was not you, contact support."), Now.AddDays(1));
    }
}
