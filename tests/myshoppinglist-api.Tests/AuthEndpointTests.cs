using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class AuthEndpointTests
{
    [PostgreSqlFact]
    public async Task Browser_can_start_reuse_read_and_logout_without_exposing_token_in_json()
    {
        await using var app = new AuthFactory();
        using var client = app.Client();
        var response = await client.PostAsync("/api/auth/trial", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var first = (await response.Content.ReadFromJsonAsync<TrialStartResponse>())!;
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        var raw = cookie.Split(';')[0].Split('=')[1];
        Assert.DoesNotContain(raw, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        app.Clock.Now += TimeSpan.FromMinutes(20);
        var reuse = await client.PostAsync("/api/auth/trial", null);
        Assert.Equal(HttpStatusCode.OK, reuse.StatusCode);
        var second = (await reuse.Content.ReadFromJsonAsync<TrialStartResponse>())!;
        Assert.True(second.Reused); Assert.Equal(first.SessionExpiresDate, second.SessionExpiresDate);
        Assert.False(reuse.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var replay = app.Client(cookies: false);
        replay.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Missing_header_cross_origin_and_cross_site_requests_are_rejected()
    {
        await using var app = new AuthFactory();
        using var client = app.Client();
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/trial", null)).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        client.DefaultRequestHeaders.Add("Origin", "https://other.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/trial", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/auth/trial", null)).StatusCode);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Session_expiry_account_expiry_and_inactive_account_are_enforced()
    {
        await using var app = new AuthFactory();
        using var client = app.Client();
        var response = await client.PostAsync("/api/auth/trial", null);
        var trial = (await response.Content.ReadFromJsonAsync<TrialStartResponse>())!;
        await using var db = PersistenceScope.Context();
        await db.UserAccounts.Where(u => u.Id == trial.Account.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        await db.UserAccounts.Where(u => u.Id == trial.Account.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.IsActive, true).SetProperty(u => u.ExpiresDate, app.Clock.Now.UtcDateTime));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        await db.UserAccounts.Where(u => u.Id == trial.Account.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.ExpiresDate, app.Clock.Now.UtcDateTime.AddHours(5)));
        app.Clock.Now = new DateTimeOffset(trial.SessionExpiresDate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Explicit_invalid_bearer_does_not_fall_back_to_browser_cookie()
    {
        await using var app = new AuthFactory();
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/auth/trial", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/trial", null)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Trial_rate_limit_returns_429_with_retry_after_without_creating_another_account()
    {
        await using var app = new AuthFactory(limit: 1);
        using var client = app.Client(cookies: false);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/auth/trial", null)).StatusCode);
        var denied = await client.PostAsync("/api/auth/trial", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.NotNull(denied.Headers.RetryAfter);
        Assert.Single(app.CreatedAccounts);
    }

    [PostgreSqlFact]
    public async Task Bearer_logout_needs_no_browser_header_and_cannot_revoke_another_session()
    {
        await using var app = new AuthFactory();
        using var first = app.Client(); using var second = app.Client();
        var created = await first.PostAsync("/api/auth/trial", null);
        var raw = created.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=')[1];
        await second.PostAsync("/api/auth/trial", null);
        using var native = app.Client(cookies: false);
        native.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        native.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        Assert.Equal(HttpStatusCode.NoContent, (await native.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Registration_login_logout_and_registered_session_expiry_work_without_json_secrets()
    {
        await using var app = new AuthFactory();
        using var client = app.Client();
        var request = new RegisterRequest(Guid.NewGuid() + "@example.test", "A long test passphrase 123!", "Shopper");
        var created = await client.PostAsJsonAsync("/api/auth/register", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = (await created.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
        Assert.False(account.Account.IsTrial); Assert.Null(account.Account.ExpiresDate);
        var cookie = created.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cookie.Split(';')[0].Split('=')[1], await created.Content.ReadAsStringAsync());
        Assert.DoesNotContain(request.Password!, await created.Content.ReadAsStringAsync());
        Assert.True(created.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new SignInRequest(request.Email, request.Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(account.Account.Id, (await login.Content.ReadFromJsonAsync<CurrentSessionResponse>())!.Account.Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        app.Clock.Now += TimeSpan.FromDays(31);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Conversion_keeps_account_and_list_and_invalidates_old_trial_token()
    {
        await using var app = new AuthFactory();
        using var client = app.Client();
        var started = await client.PostAsync("/api/auth/trial", null);
        var trial = (await started.Content.ReadFromJsonAsync<TrialStartResponse>())!;
        var raw = started.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=')[1];
        var registered = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(Guid.NewGuid() + "@example.test", "A long test passphrase 123!", "Shopper"));
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var account = (await registered.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
        Assert.Equal(trial.Account.Id, account.Account.Id); Assert.Equal(trial.ShoppingListId, account.ShoppingListId);
        using var replay = app.Client(false);
        replay.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/auth/me")).StatusCode);
        app.Clock.Now += TimeSpan.FromHours(4);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Expired_trial_cannot_register_but_can_login_without_merging_its_list()
    {
        await using var app = new AuthFactory();
        using var owner = app.Client(); using var trialClient = app.Client();
        var request = new RegisterRequest(Guid.NewGuid() + "@example.test", "A long test passphrase 123!", "Shopper");
        var registered = (await (await owner.PostAsJsonAsync("/api/auth/register", request)).Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
        var trialResponse = await trialClient.PostAsync("/api/auth/trial", null);
        var trial = (await trialResponse.Content.ReadFromJsonAsync<TrialStartResponse>())!;
        app.Clock.Now += TimeSpan.FromHours(4);
        Assert.Equal(HttpStatusCode.Unauthorized, (await trialClient.PostAsJsonAsync("/api/auth/register", request with { Email = Guid.NewGuid() + "@example.test" })).StatusCode);
        var login = await trialClient.PostAsJsonAsync("/api/auth/login", new SignInRequest(request.Email, request.Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(registered.ShoppingListId, (await login.Content.ReadFromJsonAsync<CurrentSessionResponse>())!.ShoppingListId);
        await using var db = PersistenceScope.Context();
        Assert.True((await db.UserAccounts.SingleAsync(u => u.Id == trial.Account.Id)).IsTrial);
        Assert.Equal(trial.Account.Id, (await db.ShoppingLists.SingleAsync(l => l.Id == trial.ShoppingListId)).UserAccountId);
    }

    [PostgreSqlFact]
    public async Task Account_endpoints_enforce_browser_protection_validation_and_invalid_bearer()
    {
        await using var app = new AuthFactory(); using var client = app.Client();
        foreach (var route in new[] { "register", "login" })
        {
            client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/" + route, new { })).StatusCode);
            client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
            using var malformed = new StringContent("{", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/" + route, malformed)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/" + route, new { })).StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
        }
        Assert.Empty(app.CreatedAccounts);
    }

    [PostgreSqlFact]
    public async Task Registration_and_login_have_independent_request_limits()
    {
        await using var app = new AuthFactory(accountLimit: 1); using var client = app.Client(false);
        foreach (var route in new[] { "register", "login" })
        {
            var first = await client.PostAsJsonAsync("/api/auth/" + route, new { });
            Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
            var denied = await client.PostAsJsonAsync("/api/auth/" + route, new { });
            Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
            Assert.NotNull(denied.Headers.RetryAfter); Assert.True(denied.Headers.CacheControl!.NoStore);
        }
        Assert.Empty(app.CreatedAccounts);
    }

    [PostgreSqlFact]
    public async Task Registration_api_rejects_missing_password_requirements()
    {
        await using var app = new AuthFactory(); using var client = app.Client(false);
        var request = new RegisterRequest(Guid.NewGuid() + "@example.test", "Abcdef1!", "Shopper");
        foreach (var password in new[] { "Aa1!abc", "Abcdefg!", "ABCDEFG1!", "abcdefg1!", "Abcdefg1", "Abcdef1 " })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", request with { Password = password })).StatusCode);
        Assert.Empty(app.CreatedAccounts);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", request)).StatusCode);
    }

    private sealed class AuthFactory(int limit = 10, int accountLimit = 20) : WebApplicationFactory<Program>
    {
        public PersistenceClock Clock { get; } = new();
        public ConcurrentBag<long> CreatedAccounts { get; } = [];
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
                services.PostConfigure<AuthOptions>(o => { o.TrialRequestsPerWindow = limit; o.RegistrationRequestsPerWindow = accountLimit; o.SignInRequestsPerWindow = accountLimit; });
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o
                    .UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!)
                    .AddInterceptors(new AccountCapture(CreatedAccounts)));
            });
        }
        public HttpClient Client(bool cookies = true)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), HandleCookies = cookies, AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
            return client;
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (CreatedAccounts.IsEmpty) return;
            await using var db = PersistenceScope.Context();
            var ids = CreatedAccounts.ToArray();
            await db.UserAccounts.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync();
        }
    }
    private sealed class AccountCapture(ConcurrentBag<long> accounts) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken cancellationToken = default)
        {
            foreach (var account in data.Context!.ChangeTracker.Entries<UserAccount>()) accounts.Add(account.Entity.Id);
            return ValueTask.FromResult(result);
        }
    }
}
