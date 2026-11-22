using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using OpenIddict.EntityFrameworkCore.Models;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class AccountProfileTests
{
    private const string Password = "Original-password9!";
    private static async Task<CurrentSessionResponse> Register(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Original name"));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
    }
    private static async Task<AccountProfileView> Profile(HttpClient client) => (await client.GetFromJsonAsync<AccountProfileView>("/api/account/profile"))!;
    private static async Task<string> Link(AccountSecurityTests.SecurityFactory app)
    {
        using var scope = app.Services.CreateScope(); var outbox = scope.ServiceProvider.GetRequiredService<EmailOutboxService>();
        while (await outbox.DeliverOneAsync(default)) { }
        var body = app.Transport.Messages.Last(m => m.Email.Subject == "Verify your new MyShoppingList email address").Email.Body;
        var link = new Uri(body.Split('\n').Single(line => line.StartsWith("https://frontend.example/", StringComparison.Ordinal)));
        return Uri.UnescapeDataString(link.Fragment[7..]);
    }
    private static Task<HttpResponseMessage> Request(HttpClient client, long id, string address = "new@example.test", string? password = Password,
        string? old = "owner@example.test") => client.PostAsJsonAsync("/api/account/email/request", new RequestEmailChange(id, old, address, password));

    [PostgreSqlFact]
    public async Task Profile_edits_are_optional_versioned_owned_and_do_not_revoke_sessions()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new AccountSecurityTests.SecurityFactory();
        using var owner = app.Client(); var account = await Register(owner, "owner@example.test");
        var before = await Profile(owner);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest(account.Account.Id + 1, before.UpdatedDate, "Wrong account"))).StatusCode);
        var saved = await owner.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest(account.Account.Id, before.UpdatedDate, "  Taylor  "));
        saved.EnsureSuccessStatusCode(); var after = (await saved.Content.ReadFromJsonAsync<AccountProfileView>())!;
        Assert.Equal("Taylor", after.DisplayName); Assert.True(after.UpdatedDate > before.UpdatedDate);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest(account.Account.Id, before.UpdatedDate, "Stale"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest(account.Account.Id, after.UpdatedDate, new string('x', 201)))).StatusCode);
        (await owner.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest(account.Account.Id, after.UpdatedDate, " \t "))).EnsureSuccessStatusCode();
        var session = (await owner.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!;
        Assert.Equal("", session.Account.DisplayName); Assert.Equal("owner@example.test", session.Account.Email);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.All(await db.UserSessions.ToArrayAsync(), s => Assert.Null(s.RevokedDate));
        Assert.True(await db.ShoppingLists.AnyAsync(l => l.Id == account.ShoppingListId));
    }

    [PostgreSqlFact]
    public async Task Verified_email_change_preserves_data_revokes_access_and_invalidates_old_recovery_proofs()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new AccountSecurityTests.SecurityFactory();
        using var owner = app.Client(); using var second = app.Client(); using var other = app.Client();
        var account = await Register(owner, "owner@example.test"); await Register(other, "other@example.test");
        (await second.PostAsJsonAsync("/api/auth/login", new SignInRequest("owner@example.test", Password))).EnsureSuccessStatusCode();
        string reset;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>(); var user = await db.UserAccounts.SingleAsync(u => u.Id == account.Account.Id);
            reset = scope.ServiceProvider.GetRequiredService<RecoveryTokenService>().Create(user, app.Clock.Now.UtcDateTime.AddMinutes(15));
            var authorization = new OpenIddictEntityFrameworkCoreAuthorization { Id = Guid.NewGuid().ToString(), Subject = user.Id.ToString(), Status = "valid", Type = "permanent" };
            db.Add(authorization); db.Add(new OpenIddictEntityFrameworkCoreToken { Id = Guid.NewGuid().ToString(), Subject = user.Id.ToString(), Status = "valid", Authorization = authorization, Type = "refresh_token" });
            db.AiConnections.Add(new() { UserAccountId = user.Id, AuthorizationId = authorization.Id, ClientId = "test", DisplayName = "Test", GrantedScopes = "lists.read", CreatedDate = app.Clock.Now.UtcDateTime });
            var login = await db.UserSessions.FirstAsync(s => s.UserAccountId == user.Id);
            db.ExtensionDevices.Add(new() { UserAccountId = user.Id, UserSessionId = login.Id, InstallationId = Guid.NewGuid(), TokenHash = SessionToken.Hash(SessionToken.Create()), ExtensionId = new string('a', 32), ExtensionVersion = "1", LinkedAt = app.Clock.Now.UtcDateTime });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Accepted, (await Request(owner, account.Account.Id)).StatusCode);
        var pending = await Profile(owner); Assert.Equal("owner@example.test", pending.Email); Assert.Equal("new@example.test", pending.PendingEmail);
        var token = await Link(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await second.PostAsJsonAsync("/api/account/email/complete", new CompleteEmailChange(account.Account.Id, token))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/api/account/email/complete", new CompleteEmailChange(account.Account.Id, token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/api/auth/login", new SignInRequest("owner@example.test", Password))).StatusCode);
        (await owner.PostAsJsonAsync("/api/auth/login", new SignInRequest("new@example.test", Password))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/account/email/complete", new CompleteEmailChange(account.Account.Id, token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(reset, "Replacement-password8!"))).StatusCode);
        using var verify = app.Services.CreateScope(); var state = verify.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var updated = await state.UserAccounts.SingleAsync(u => u.Id == account.Account.Id);
        Assert.Equal("new@example.test", updated.Email); Assert.NotNull(updated.EmailVerifiedAt); Assert.Equal("Original name", updated.DisplayName);
        Assert.True(await state.ShoppingLists.AnyAsync(l => l.Id == account.ShoppingListId && l.UserAccountId == updated.Id));
        Assert.All(await state.AiConnections.ToArrayAsync(), c => Assert.NotNull(c.RevokedDate));
        Assert.All(await state.ExtensionDevices.ToArrayAsync(), d => Assert.NotNull(d.RevokedAt));
        Assert.All(await state.Set<OpenIddictEntityFrameworkCoreToken>().ToArrayAsync(), t => Assert.Equal("revoked", t.Status));
        while (await verify.ServiceProvider.GetRequiredService<EmailOutboxService>().DeliverOneAsync(default)) { }
        Assert.Contains(app.Transport.Messages, m => m.Email.Recipients.Contains("owner@example.test") && m.Email.Subject == "Your MyShoppingList email address was changed");
    }

    [PostgreSqlFact]
    public async Task Email_requests_require_proof_and_support_cancellation_expiry_and_newer_links()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new AccountSecurityTests.SecurityFactory();
        using var owner = app.Client(); var account = await Register(owner, "owner@example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(owner, account.Account.Id, password: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(owner, account.Account.Id, password: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Request(owner, account.Account.Id)).StatusCode);
        var first = await Profile(owner); var firstToken = await Link(app);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Request(owner, account.Account.Id)).StatusCode);
        (await owner.PostAsJsonAsync("/api/account/email/cancel", new CancelEmailChange(account.Account.Id, first.PendingEmailId!.Value))).EnsureSuccessStatusCode();
        Assert.Null((await Profile(owner)).PendingEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/account/email/complete", new CompleteEmailChange(account.Account.Id, firstToken))).StatusCode);
        app.Clock.Now = app.Clock.Now.AddMinutes(3);
        // A separate client address avoids exhausting the endpoint's shared five-request security budget.
        using var scope = app.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<AccountProfileService>();
        var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>(); var sessionId = await db.UserSessions.Where(s => s.UserAccountId == account.Account.Id).Select(s => s.Id).SingleAsync();
        await service.RequestEmailAsync(account.Account.Id, sessionId, new(account.Account.Id, "owner@example.test", "latest@example.test", Password), default);
        db.ChangeTracker.Clear();
        var latest = await Profile(owner); var latestToken = await Link(app);
        await service.CancelAsync(account.Account.Id, sessionId, new(account.Account.Id, first.PendingEmailId.Value), default);
        Assert.Equal(latest.PendingEmailId, (await Profile(owner)).PendingEmailId);
        app.Clock.Now = app.Clock.Now.AddMinutes(16);
        var expired = await Assert.ThrowsAsync<UserImportException>(() => service.CompleteAsync(account.Account.Id, sessionId, new(account.Account.Id, latestToken), default));
        Assert.Equal(400, expired.Status); Assert.Equal("owner@example.test", (await Profile(owner)).Email);
    }

    [PostgreSqlFact]
    public async Task Conflicting_email_is_rechecked_at_confirmation_and_requests_are_csrf_protected()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new AccountSecurityTests.SecurityFactory();
        using var owner = app.Client(); using var other = app.Client(); var account = await Register(owner, "owner@example.test");
        owner.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(owner, account.Account.Id)).StatusCode);
        owner.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        (await Request(owner, account.Account.Id)).EnsureSuccessStatusCode(); var token = await Link(app);
        await Register(other, "new@example.test");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/account/email/complete", new CompleteEmailChange(account.Account.Id, token))).StatusCode);
        Assert.Equal("owner@example.test", (await Profile(owner)).Email);
    }

    [PostgreSqlFact]
    public async Task Concurrent_confirmation_is_single_use_and_social_only_accounts_require_recent_provider_proof()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new AccountSecurityTests.SecurityFactory();
        using var owner = app.Client(); var account = await Register(owner, "owner@example.test");
        using var setup = app.Services.CreateScope(); var db = setup.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var user = await db.UserAccounts.SingleAsync(u => u.Id == account.Account.Id);
        db.ExternalIdentities.Add(new() { UserAccountId = user.Id, Provider = "google", Subject = "fixture", CreatedAt = app.Clock.Now.UtcDateTime });
        user.PasswordHash = null; user.PasswordSalt = null; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await Request(owner, user.Id, password: null)).StatusCode);
        var session = await db.UserSessions.SingleAsync(s => s.UserAccountId == user.Id); session.ReauthenticatedAt = app.Clock.Now.UtcDateTime; await db.SaveChangesAsync();
        (await Request(owner, user.Id, password: null)).EnsureSuccessStatusCode(); var token = await Link(app);
        async Task<bool> Confirm()
        {
            using var scope = app.Services.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<AccountProfileService>().CompleteAsync(user.Id, session.Id, new(user.Id, token), default); return true; }
            catch (UserImportException) { return false; }
        }
        var results = await Task.WhenAll(Confirm(), Confirm()).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, success => success);
        db.ChangeTracker.Clear(); Assert.Equal("new@example.test", (await db.UserAccounts.SingleAsync(u => u.Id == user.Id)).Email);
        Assert.Equal("fixture", (await db.ExternalIdentities.SingleAsync()).Subject);
    }
}
