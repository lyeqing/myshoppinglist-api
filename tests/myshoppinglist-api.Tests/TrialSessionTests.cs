using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public class TrialSessionTests
{
    private static TrialSessionService Service(PersistenceScope scope) => new(scope.Db, Options.Create(new AuthOptions()), scope.Clock);

    [PostgreSqlFact]
    public async Task Creates_matching_account_list_session_and_stores_only_hash()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var result = await Service(scope).StartAsync(null, null, default);
        Assert.NotNull(result.Response); Assert.NotNull(result.Token);
        var account = await scope.Db.UserAccounts.SingleAsync(u => u.Id == result.Response.Account.Id);
        var list = await scope.Db.ShoppingLists.SingleAsync(l => l.Id == result.Response.ShoppingListId);
        var session = await scope.Db.UserSessions.SingleAsync(s => s.UserAccountId == account.Id);
        Assert.True(account.IsTrial); Assert.Null(account.Email); Assert.Null(account.PasswordHash); Assert.Null(account.PasswordSalt);
        Assert.Equal(account.Id, list.UserAccountId);
        Assert.Equal(account.CreatedDate.AddHours(3), account.ExpiresDate);
        Assert.Equal(account.ExpiresDate, list.ExpiresDate); Assert.Equal(account.ExpiresDate, session.ExpiresDate);
        Assert.Equal(SessionToken.Hash(result.Token), session.TokenHash); Assert.NotEqual(result.Token, session.TokenHash);
    }

    [PostgreSqlFact]
    public async Task Reuse_keeps_original_expiry_and_does_not_create_more_rows()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var service = Service(scope);
        var first = await service.StartAsync(null, null, default);
        var accountId = first.Response!.Account.Id;
        var session = await scope.Db.UserSessions.SingleAsync(s => s.UserAccountId == accountId);
        scope.Clock.Now += TimeSpan.FromHours(1);
        var second = await service.StartAsync(accountId, session.Id, default);
        Assert.True(second.Response!.Reused); Assert.Null(second.Token);
        Assert.Equal(first.Response.ShoppingListId, second.Response.ShoppingListId);
        Assert.Equal(first.Response.SessionExpiresDate, second.Response.SessionExpiresDate);
        Assert.Equal(1, await scope.Db.UserSessions.CountAsync(s => s.UserAccountId == accountId));
        Assert.Equal(1, await scope.Db.ShoppingLists.CountAsync(l => l.UserAccountId == accountId));
    }

    [PostgreSqlFact]
    public async Task Expiry_at_boundary_and_revocation_reject_current_session()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var service = Service(scope);
        var first = await service.StartAsync(null, null, default);
        var accountId = first.Response!.Account.Id;
        var session = await scope.Db.UserSessions.SingleAsync(s => s.UserAccountId == accountId);
        Assert.Equal(0, await service.LogoutAsync(scope.UserId, session.Id, default));
        Assert.NotNull(await service.CurrentAsync(accountId, session.Id, default));
        scope.Clock.Now = new DateTimeOffset(session.ExpiresDate);
        Assert.Null(await service.CurrentAsync(accountId, session.Id, default));
        Assert.Equal("invalid_session", (await service.StartAsync(accountId, session.Id, default)).ErrorCode);
        scope.Clock.Now -= TimeSpan.FromHours(1);
        Assert.Equal(1, await service.LogoutAsync(accountId, session.Id, default));
        Assert.Null(await service.CurrentAsync(accountId, session.Id, default));
        Assert.Equal(0, await service.LogoutAsync(accountId, session.Id, default));
    }

    [PostgreSqlFact]
    public async Task Archived_trial_list_is_not_silently_replaced()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var service = Service(scope);
        var first = await service.StartAsync(null, null, default);
        var session = await scope.Db.UserSessions.SingleAsync(s => s.UserAccountId == first.Response!.Account.Id);
        await scope.Db.ShoppingLists.Where(l => l.Id == first.Response!.ShoppingListId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        var second = await service.StartAsync(session.UserAccountId, session.Id, default);
        Assert.Equal("trial_list_unavailable", second.ErrorCode); Assert.Null(second.Token);
    }
}
