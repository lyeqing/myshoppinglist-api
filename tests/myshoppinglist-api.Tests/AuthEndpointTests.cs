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

    private sealed class AuthFactory(int limit = 10) : WebApplicationFactory<Program>
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
                services.PostConfigure<AuthOptions>(o => o.TrialRequestsPerWindow = limit);
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
