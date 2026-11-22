using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public class TrialSessionTests
{
    private static TrialSessionService Service(PersistenceScope scope) => new(scope.Db, scope.Clock);
    private static async Task<(CurrentSessionResponse Response, UserSession Session)> RegisterAsync(PersistenceScope scope)
    {
        scope.Db.ChangeTracker.Clear();
        var result = await new AccountAuthService(scope.Db, Options.Create(new AuthOptions()), scope.Clock)
            .RegisterAsync(new RegisterRequest(Guid.NewGuid() + "@example.test", "Valid-Password9!", "Registered shopper"), null, null, default);
        Assert.NotNull(result.Response); Assert.False(result.Response.Account.IsTrial); Assert.False(result.Response.Account.IsPaid);
        var session = await scope.Db.UserSessions.SingleAsync(s => s.UserAccountId == result.Response.Account.Id);
        Assert.Equal(SessionToken.Hash(result.Token!), session.TokenHash);
        return (result.Response, session);
    }

    [PostgreSqlFact]
    public async Task Registered_session_restores_existing_list_without_creating_rows()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var (response, session) = await RegisterAsync(scope);
        var service = Service(scope);
        var first = await service.CurrentAsync(response.Account.Id, session.Id, default);
        scope.Clock.Now += TimeSpan.FromHours(4);
        var second = await service.CurrentAsync(response.Account.Id, session.Id, default);
        Assert.NotNull(first); Assert.NotNull(second); Assert.Equal(first.ShoppingListId, second.ShoppingListId);
        Assert.Equal(first.SessionExpiresDate, second.SessionExpiresDate);
        Assert.Equal(1, await scope.Db.UserSessions.CountAsync(s => s.UserAccountId == response.Account.Id));
        Assert.Equal(1, await scope.Db.ShoppingLists.CountAsync(l => l.UserAccountId == response.Account.Id));
    }

    [PostgreSqlFact]
    public async Task Legacy_trial_session_is_rejected_without_changing_its_data()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var now = scope.Clock.Now.UtcDateTime;
        var session = new UserSession { UserAccountId = scope.UserId, TokenHash = SessionToken.Hash(SessionToken.Create()), CreatedDate = now, ExpiresDate = now.AddHours(3) };
        scope.Db.Add(session); await scope.Db.SaveChangesAsync();
        Assert.Null(await Service(scope).CurrentAsync(scope.UserId, session.Id, default));
        Assert.Null((await scope.Db.UserSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id)).LastSeenDate);
        Assert.True((await scope.Db.UserAccounts.SingleAsync(u => u.Id == scope.UserId)).IsTrial);
    }

    [PostgreSqlFact]
    public async Task Expiry_at_boundary_and_revocation_reject_registered_session()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var (response, session) = await RegisterAsync(scope); var service = Service(scope);
        Assert.Equal(0, await service.LogoutAsync(scope.UserId, session.Id, default));
        Assert.NotNull(await service.CurrentAsync(response.Account.Id, session.Id, default));
        scope.Clock.Now = new DateTimeOffset(session.ExpiresDate);
        Assert.Null(await service.CurrentAsync(response.Account.Id, session.Id, default));
        scope.Clock.Now -= TimeSpan.FromHours(1);
        Assert.Equal(1, await service.LogoutAsync(response.Account.Id, session.Id, default));
        Assert.Null(await service.CurrentAsync(response.Account.Id, session.Id, default));
        Assert.Equal(0, await service.LogoutAsync(response.Account.Id, session.Id, default));
    }

    [PostgreSqlFact]
    public async Task Archived_registered_list_is_not_silently_replaced()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var (response, session) = await RegisterAsync(scope);
        await scope.Db.ShoppingLists.Where(l => l.Id == response.ShoppingListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        var restored = await Service(scope).CurrentAsync(response.Account.Id, session.Id, default);
        Assert.NotNull(restored); Assert.Null(restored.ShoppingListId);
        Assert.Equal(1, await scope.Db.ShoppingLists.CountAsync(l => l.UserAccountId == response.Account.Id));
    }
}
