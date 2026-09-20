using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class AccountAuthServiceTests
{
    [PostgreSqlFact]
    public async Task New_registration_normalises_email_and_login_uses_independent_sessions()
    {
        await using var f = new AccountFixture();
        var registered = await f.Service.RegisterAsync(f.Request with { Email = " " + f.Email.ToUpperInvariant() + " ", DisplayName = " Shopper " }, null, null, default);
        Assert.Equal(201, registered.StatusCode); Assert.False(registered.Response!.Account.IsTrial);
        Assert.Null(registered.Response.Account.ExpiresDate); Assert.Equal("Shopper", registered.Response.Account.DisplayName);
        var user = await f.Db.UserAccounts.AsNoTracking().SingleAsync(u => u.Id == registered.Response.Account.Id);
        Assert.Equal(f.Email, user.Email); Assert.NotEqual(AccountFixture.Password, user.PasswordHash);
        Assert.True(PasswordHasher.Verify(AccountFixture.Password, user.PasswordHash, user.PasswordSalt));
        var list = await f.Db.ShoppingLists.AsNoTracking().SingleAsync(l => l.Id == registered.Response.ShoppingListId);
        Assert.Null(list.ExpiresDate);
        var login = await f.Service.SignInAsync(new(f.Email.ToUpperInvariant(), AccountFixture.Password), default);
        Assert.Equal(200, login.StatusCode); Assert.Equal(registered.Response.ShoppingListId, login.Response!.ShoppingListId);
        Assert.NotEqual(registered.Token, login.Token);
        Assert.Equal(f.Clock.Now.UtcDateTime.AddDays(30).Ticks / 10, login.Response.SessionExpiresDate.Ticks / 10);
        Assert.Equal(2, await f.Db.UserSessions.CountAsync(s => s.UserAccountId == user.Id && s.RevokedDate == null));
        Assert.True(await f.Db.UserSessions.AnyAsync(s => s.TokenHash == SessionToken.Hash(login.Token!)));
    }

    [PostgreSqlFact]
    public async Task Invalid_and_duplicate_registration_do_not_create_extra_accounts()
    {
        await using var f = new AccountFixture();
        foreach (var request in new[] { f.Request with { Email = "not email" }, f.Request with { DisplayName = " " },
            f.Request with { Password = "short" }, f.Request with { Password = new string('x',1025) } })
            Assert.Equal(400, (await f.Service.RegisterAsync(request, null, null, default)).StatusCode);
        async Task<AccountAuthResult> Register()
        {
            await using var db = PersistenceScope.Context();
            return await new AccountAuthService(db, Options.Create(new AuthOptions()), f.Clock).RegisterAsync(f.Request, null, null, default);
        }
        var results = await Task.WhenAll(Register(), Register());
        Assert.Single(results, r => r.StatusCode == 201); Assert.Single(results, r => r.StatusCode == 409);
        Assert.Equal(1, await f.Db.UserAccounts.CountAsync(u => u.Email == f.Email));
    }

    [PostgreSqlFact]
    public async Task Unknown_wrong_password_and_inactive_account_share_generic_failure()
    {
        await using var f = new AccountFixture();
        var registered = await f.Service.RegisterAsync(f.Request, null, null, default);
        var wrong = await f.Service.SignInAsync(new(f.Email, "An incorrect password"), default);
        var unknown = await f.Service.SignInAsync(new("unknown-" + f.Email, AccountFixture.Password), default);
        await f.Db.UserAccounts.Where(u => u.Id == registered.Response!.Account.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var inactive = await f.Service.SignInAsync(new(f.Email, AccountFixture.Password), default);
        Assert.Equal(wrong, unknown); Assert.Equal(wrong, inactive); Assert.Equal(401, wrong.StatusCode);
        Assert.Equal(1, await f.Db.UserSessions.CountAsync(s => s.UserAccountId == registered.Response!.Account.Id));
    }

    [PostgreSqlFact]
    public async Task Trial_conversion_preserves_items_jobs_and_revokes_all_trial_sessions()
    {
        await using var f = await ListFixture.CreateAsync();
        var original = await f.ItemAsync();
        var edited = (await f.UpdateAsync(new(8, "Keep this", true, true, original.UpdatedDate))).Item!;
        var session = await AddTrialSession(f);
        var second = await AddTrialSession(f);
        var service = new AccountAuthService(f.Scope.Db, Options.Create(new AuthOptions()), f.Scope.Clock);
        var result = await service.RegisterAsync(new(f.Scope.Code + "@example.test", AccountFixture.Password, "Permanent shopper"), f.Scope.UserId, session, default);
        Assert.Equal(201, result.StatusCode); Assert.Equal(f.Scope.UserId, result.Response!.Account.Id);
        Assert.Equal(f.Scope.ListId, result.Response.ShoppingListId); Assert.False(result.Response.Account.IsTrial);
        Assert.Equal(edited, await f.ItemAsync());
        Assert.Null((await f.Scope.Db.ShoppingLists.AsNoTracking().SingleAsync(l => l.Id == f.Scope.ListId)).ExpiresDate);
        Assert.Equal(f.ItemId, (await f.Scope.Db.ProductImportJobs.AsNoTracking().SingleAsync(j => j.Id == f.Scope.Job.Id)).ShoppingListProductId);
        Assert.All(await f.Scope.Db.UserSessions.AsNoTracking().Where(s => s.Id == session || s.Id == second).ToListAsync(), s => Assert.NotNull(s.RevokedDate));
        Assert.Equal(1, await f.Scope.Db.UserSessions.CountAsync(s => s.UserAccountId == f.Scope.UserId && s.RevokedDate == null));
        f.Scope.Clock.Now += TimeSpan.FromHours(4);
        Assert.Equal(edited, await f.ItemAsync());
    }

    [PostgreSqlFact]
    public async Task Conversion_rechecks_expiry_before_commit_and_rolls_back_credentials_and_revocation()
    {
        await using var f = await ListFixture.CreateAsync();
        var session = await AddTrialSession(f);
        var clock = new ExpiringClock(f.Scope.Clock.Now);
        var service = new AccountAuthService(f.Scope.Db, Options.Create(new AuthOptions()), clock);
        var result = await service.RegisterAsync(new(f.Scope.Code + "@example.test", AccountFixture.Password, "Shopper"), f.Scope.UserId, session, default);
        Assert.Equal(401, result.StatusCode); Assert.True(clock.Calls >= 5);
        var user = await f.Scope.Db.UserAccounts.AsNoTracking().SingleAsync(u => u.Id == f.Scope.UserId);
        Assert.True(user.IsTrial); Assert.Null(user.Email); Assert.Null(user.PasswordHash);
        Assert.Null((await f.Scope.Db.UserSessions.AsNoTracking().SingleAsync(s => s.Id == session)).RevokedDate);
        Assert.NotNull((await f.Scope.Db.ShoppingLists.AsNoTracking().SingleAsync(l => l.Id == f.Scope.ListId)).ExpiresDate);
    }

    [PostgreSqlFact]
    public async Task Expired_or_revoked_trial_cannot_be_converted()
    {
        foreach (var revoked in new[] { false, true })
        {
            await using var f = await ListFixture.CreateAsync();
            var session = await AddTrialSession(f);
            if (revoked) await f.Scope.Db.UserSessions.Where(s => s.Id == session).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedDate, f.Scope.Clock.Now.UtcDateTime));
            else f.Scope.Clock.Now += TimeSpan.FromHours(4);
            var service = new AccountAuthService(f.Scope.Db, Options.Create(new AuthOptions()), f.Scope.Clock);
            Assert.Equal(401, (await service.RegisterAsync(new(f.Scope.Code + "@example.test", AccountFixture.Password, "Shopper"), f.Scope.UserId, session, default)).StatusCode);
            Assert.True((await f.Scope.Db.UserAccounts.AsNoTracking().SingleAsync(u => u.Id == f.Scope.UserId)).IsTrial);
        }
    }

    [PostgreSqlFact]
    public async Task Simultaneous_conversions_have_one_winner_and_keep_one_account()
    {
        await using var f = await ListFixture.CreateAsync();
        var session = await AddTrialSession(f);
        async Task<AccountAuthResult> Convert(string suffix)
        {
            await using var db = PersistenceScope.Context();
            return await new AccountAuthService(db, Options.Create(new AuthOptions()), f.Scope.Clock).RegisterAsync(
                new(f.Scope.Code + suffix + "@example.test", AccountFixture.Password, "Shopper"), f.Scope.UserId, session, default);
        }
        var results = await Task.WhenAll(Convert("a"), Convert("b"));
        Assert.Single(results, r => r.StatusCode == 201); Assert.Single(results, r => r.StatusCode == 409);
        Assert.Equal(1, await f.Scope.Db.UserSessions.CountAsync(s => s.UserAccountId == f.Scope.UserId && s.RevokedDate == null));
    }

    internal static async Task<long> AddTrialSession(ListFixture f)
    {
        var now = f.Scope.Clock.Now.UtcDateTime;
        var session = new myshoppinglist_api.Models.UserSession { UserAccountId = f.Scope.UserId, CreatedDate = now,
            ExpiresDate = now.AddHours(3), TokenHash = SessionToken.Hash(SessionToken.Create()) };
        f.Scope.Db.UserSessions.Add(session); await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
        await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresDate, now.AddHours(3)));
        return session.Id;
    }
    private sealed class ExpiringClock(DateTimeOffset start) : TimeProvider
    {
        public int Calls { get; private set; }
        public override DateTimeOffset GetUtcNow() => ++Calls >= 5 ? start.AddHours(4) : start;
    }
    private sealed class AccountFixture : IAsyncDisposable
    {
        internal const string Password = "A long test passphrase 123";
        public string Email { get; } = "account-test-" + Guid.NewGuid().ToString("N") + "@example.test";
        public MyShoppingListDbContext Db { get; } = PersistenceScope.Context();
        public PersistenceClock Clock { get; } = new();
        public RegisterRequest Request => new(Email, Password, "Shopper");
        public AccountAuthService Service => new(Db, Options.Create(new AuthOptions()), Clock);
        public async ValueTask DisposeAsync()
        { try { Db.ChangeTracker.Clear(); await Db.UserAccounts.Where(u => u.Email == Email).ExecuteDeleteAsync(); } finally { await Db.DisposeAsync(); } }
    }
}
