using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public sealed record TrialSessionResult(TrialStartResponse? Response, string? Token = null, string? ErrorCode = null);

public sealed class TrialSessionService(MyShoppingListDbContext db, IOptions<AuthOptions> options, TimeProvider clock)
{
    public async Task<CurrentSessionResponse?> CurrentAsync(long accountId, long sessionId, CancellationToken token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.UserSessions.AsNoTracking().Where(s => s.Id == sessionId && s.UserAccountId == accountId
            && s.RevokedDate == null && s.ExpiresDate > now && s.UserAccount.IsActive
            && (!s.UserAccount.IsTrial || s.UserAccount.ExpiresDate > now))
            .Select(s => new CurrentSessionResponse(new AccountResponse(s.UserAccountId, s.UserAccount.DisplayName,
                s.UserAccount.IsTrial, s.UserAccount.ExpiresDate),
                db.ShoppingLists.Where(l => l.UserAccountId == accountId && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now))
                    .OrderBy(l => l.Id).Select(l => (long?)l.Id).FirstOrDefault(), s.ExpiresDate))
            .SingleOrDefaultAsync(token);
    }

    public async Task<TrialSessionResult> StartAsync(long? accountId, long? sessionId, CancellationToken token)
    {
        if (accountId.HasValue || sessionId.HasValue)
        {
            if (!accountId.HasValue || !sessionId.HasValue) return new(null, ErrorCode: "invalid_session");
            var current = await CurrentAsync(accountId.Value, sessionId.Value, token);
            if (current is null) return new(null, ErrorCode: "invalid_session");
            if (!current.Account.IsTrial) return new(null, ErrorCode: "registered_session");
            if (current.ShoppingListId is null) return new(null, ErrorCode: "trial_list_unavailable");
            return new(new(current.Account, current.ShoppingListId.Value, current.SessionExpiresDate, true));
        }
        var now = clock.GetUtcNow().UtcDateTime;
        now = new DateTime(now.Ticks / 10 * 10, DateTimeKind.Utc);
        var expires = now.AddHours(options.Value.TrialLifetimeHours);
        var raw = SessionToken.Create();
        var user = new UserAccount { DisplayName = "Trial shopper", IsTrial = true, IsActive = true,
            CreatedDate = now, UpdatedDate = now, ExpiresDate = expires };
        var list = new ShoppingList { UserAccount = user, Name = "My shopping list", CreatedDate = now, UpdatedDate = now, ExpiresDate = expires };
        var session = new UserSession { UserAccount = user, TokenHash = SessionToken.Hash(raw), CreatedDate = now, ExpiresDate = expires };
        db.AddRange(user, list, session);
        // One SaveChanges transaction commits all three rows or none of them.
        await db.SaveChangesAsync(token);
        return new(new(new(user.Id, user.DisplayName, true, expires), list.Id, expires, false), raw);
    }

    public Task<int> LogoutAsync(long accountId, long sessionId, CancellationToken token) =>
        db.UserSessions.Where(s => s.Id == sessionId && s.UserAccountId == accountId && s.RevokedDate == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedDate, clock.GetUtcNow().UtcDateTime), token);
}
