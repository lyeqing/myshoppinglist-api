using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using Npgsql;

namespace myshoppinglist_api.Services;

public sealed record AccountAuthResult(CurrentSessionResponse? Response, string? Token = null, int StatusCode = 200, string? Message = null);

public sealed class AccountAuthService(MyShoppingListDbContext db, IOptions<AuthOptions> options, TimeProvider clock)
{
    private static readonly (string Hash, string Salt) Dummy = PasswordHasher.Hash("Unknown account credential");
    private DateTime Now => new(clock.GetUtcNow().UtcTicks / 10 * 10, DateTimeKind.Utc);

    public async Task<AccountAuthResult> RegisterAsync(RegisterRequest request, long? accountId, long? sessionId, CancellationToken token)
    {
        var email = NormaliseEmail(request.Email);
        if (email is null) return Fail(400, "Enter a valid email address.");
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 200)
            return Fail(400, "Enter a display name of at most 200 characters.");
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < options.Value.MinimumPasswordLength || request.Password.Length > 1024)
            return Fail(400, $"Use a password between {options.Value.MinimumPasswordLength} and 1024 characters.");
        if (accountId.HasValue != sessionId.HasValue) return Fail(401, "A valid session is required.");
        CleanContext();
        // Expensive hashing is outside database locks. Eligibility is checked again after acquiring them.
        var credential = PasswordHasher.Hash(request.Password);
        token.ThrowIfCancellationRequested();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            // A namespaced email lock serialises duplicate registrations across application instances.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"account-register:" + email}, 0))", token);
            if (await db.UserAccounts.AnyAsync(u => u.Email == email, token)) return Fail(409, "Registration could not be completed with this email.");
            UserAccount user;
            ShoppingList list;
            UserSession? trialSession = null;
            DateTime? trialExpiry = null;
            List<ShoppingList> convertedLists = [];
            List<DateTime?> originalListExpiries = [];
            var now = Now;
            if (accountId is { } id)
            {
                var existing = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {id} FOR UPDATE""").SingleOrDefaultAsync(token);
                if (existing is null || !existing.IsActive || existing.IsTrial && !(existing.ExpiresDate > Now))
                    return Fail(401, "The trial session has expired or is unavailable.");
                if (!existing.IsTrial) return Fail(409, "You are already signed in to a registered account.");
                user = existing; trialExpiry = user.ExpiresDate;
                var lists = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "UserAccountId" = {id} ORDER BY "Id" FOR UPDATE""").ToListAsync(token);
                trialSession = await db.UserSessions.FromSqlInterpolated($"""SELECT * FROM "UserSessions" WHERE "Id" = {sessionId} AND "UserAccountId" = {id} FOR UPDATE""").SingleOrDefaultAsync(token);
                if (trialSession is null || trialSession.RevokedDate is not null || !(trialSession.ExpiresDate > Now))
                    return Fail(401, "A valid trial session is required.");
                convertedLists = lists.Where(l => !l.IsArchived && (l.ExpiresDate is null || l.ExpiresDate > Now)).ToList();
                if (convertedLists.Count == 0) return Fail(409, "The trial shopping list is unavailable.");
                originalListExpiries = convertedLists.Select(l => l.ExpiresDate).ToList();
                list = convertedLists[0];
                foreach (var activeList in convertedLists) { activeList.ExpiresDate = null; activeList.UpdatedDate = now; }
                // Tokens issued while this was a trial must not become registered-account credentials.
                await db.UserSessions.Where(s => s.UserAccountId == id && s.RevokedDate == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedDate, now), token);
            }
            else
            {
                user = new() { CreatedDate = now, IsActive = true };
                list = new() { UserAccount = user, Name = "My shopping list", CreatedDate = now, UpdatedDate = now };
                db.AddRange(user, list);
            }
            user.Email = email; user.DisplayName = request.DisplayName.Trim();
            user.PasswordHash = credential.Hash; user.PasswordSalt = credential.Salt;
            user.IsTrial = false; user.ExpiresDate = null; user.UpdatedDate = now;
            var (session, raw) = NewSession(user, now);
            db.UserSessions.Add(session);
            await db.SaveChangesAsync(token);
            now = Now;
            // Compare original expiries because the tracked trial/list values were just cleared.
            if (trialSession is not null && (!(trialExpiry > now) || !(trialSession.ExpiresDate > now)
                || originalListExpiries.Any(expiry => expiry <= now)))
                return Fail(401, "The trial expired before registration finished.");
            if (!(session.ExpiresDate > now)) return Fail(401, "The new session expired before registration finished.");
            await transaction.CommitAsync(token);
            return new(Response(user, list.Id, session), raw, 201);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { return Fail(409, "Registration could not be completed with this email."); }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<AccountAuthResult> SignInAsync(SignInRequest request, CancellationToken token)
    {
        CleanContext();
        var email = NormaliseEmail(request.Email);
        if (request.Password is null || request.Password.Length is < 1 or > 1024) return InvalidCredentials();
        var candidate = email is null ? null : await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email && !u.IsTrial, token);
        var valid = PasswordHasher.Verify(request.Password, candidate?.PasswordHash ?? Dummy.Hash, candidate?.PasswordSalt ?? Dummy.Salt);
        if (!valid || candidate is null || !candidate.IsActive) return InvalidCredentials();
        token.ThrowIfCancellationRequested();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            var user = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {candidate.Id} FOR UPDATE""").SingleOrDefaultAsync(token);
            if (user is null || !user.IsActive || user.IsTrial || user.PasswordHash != candidate.PasswordHash || user.PasswordSalt != candidate.PasswordSalt)
                return InvalidCredentials();
            var now = Now;
            var listId = await db.ShoppingLists.Where(l => l.UserAccountId == user.Id && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now))
                .OrderBy(l => l.Id).Select(l => (long?)l.Id).FirstOrDefaultAsync(token);
            var (session, raw) = NewSession(user, now);
            db.UserSessions.Add(session); await db.SaveChangesAsync(token);
            if (!(session.ExpiresDate > Now)) return InvalidCredentials();
            await transaction.CommitAsync(token);
            return new(Response(user, listId, session), raw);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private (UserSession Session, string Raw) NewSession(UserAccount user, DateTime now)
    {
        var raw = SessionToken.Create();
        return (new() { UserAccount = user, TokenHash = SessionToken.Hash(raw), CreatedDate = now,
            ExpiresDate = now.AddDays(options.Value.RegisteredSessionDays) }, raw);
    }
    private void CleanContext()
    { if (db.ChangeTracker.Entries().Any()) throw new InvalidOperationException("Account authentication requires a clean context."); }
    internal static string? NormaliseEmail(string? value)
    {
        var email = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || email.Length > 320 || email.Any(char.IsWhiteSpace)
            || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || parsed.DisplayName.Length != 0
            || !parsed.Host.Contains('.') || parsed.Host.StartsWith('[')) return null;
        return email;
    }
    private static CurrentSessionResponse Response(UserAccount user, long? listId, UserSession session) =>
        new(new(user.Id, user.DisplayName, user.IsTrial, user.ExpiresDate), listId, session.ExpiresDate);
    private static AccountAuthResult Fail(int code, string message) => new(null, StatusCode: code, Message: message);
    private static AccountAuthResult InvalidCredentials() => Fail(401, "The email or password is incorrect.");
}
