using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;

namespace myshoppinglist_api.Services;

public sealed class TrialSessionService(MyShoppingListDbContext db, TimeProvider clock)
{
    public async Task<CurrentSessionResponse?> CurrentAsync(long accountId, long sessionId, CancellationToken token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.UserSessions.Where(s => s.Id == sessionId && s.UserAccountId == accountId
            && s.RevokedDate == null && s.ExpiresDate > now && s.UserAccount.IsActive
            && !s.UserAccount.IsTrial)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenDate, now), token);
        return await db.UserSessions.AsNoTracking().Where(s => s.Id == sessionId && s.UserAccountId == accountId
            && s.RevokedDate == null && s.ExpiresDate > now && s.UserAccount.IsActive
            && !s.UserAccount.IsTrial)
            .Select(s => new CurrentSessionResponse(new AccountResponse(s.UserAccountId, s.UserAccount.DisplayName,
                s.UserAccount.IsTrial, s.UserAccount.ExpiresDate, s.UserAccount.IsPaid, s.UserAccount.Email),
                db.ShoppingLists.Where(l => l.UserAccountId == accountId && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now))
                    .OrderByDescending(l => db.UserCurrentLists.Any(c => c.UserAccountId == accountId && c.ShoppingListId == l.Id))
                    .ThenBy(l => l.Id).Select(l => (long?)l.Id).FirstOrDefault(), s.ExpiresDate,
                db.UserCurrentLists.Where(c => c.UserAccountId == accountId && !c.ShoppingList.IsArchived
                    && (c.ShoppingList.ExpiresDate == null || c.ShoppingList.ExpiresDate > now))
                    .Select(c => (long?)c.ShoppingListId).FirstOrDefault()))
            .SingleOrDefaultAsync(token);
    }

    public Task<int> LogoutAsync(long accountId, long sessionId, CancellationToken token) =>
        db.UserSessions.Where(s => s.Id == sessionId && s.UserAccountId == accountId && s.RevokedDate == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedDate, clock.GetUtcNow().UtcDateTime), token);
}
