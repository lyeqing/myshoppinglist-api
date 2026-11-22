using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using Npgsql;

namespace myshoppinglist_api.Tests;

internal sealed class PostgreSqlTheoryAttribute : TheoryAttribute
{
    public PostgreSqlTheoryAttribute()
    { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION"))) Skip = "A disposable PostgreSQL test connection is required."; }
}

[Collection("Import worker database")]
public sealed class SocialAuthenticationTests
{
    private const string Password = "Original-passphrase1!";

    [PostgreSqlTheory]
    [InlineData(null)]
    [InlineData(" \t ")]
    public async Task Missing_provider_names_stay_blank_without_using_email(string? name)
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory(); using var browser = app.Client();
        app.Name = name;
        await Authenticate(app, browser, "google"); await Finish(browser);
        var session = (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!;
        Assert.Equal(string.Empty, session.Account.DisplayName);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.Equal(string.Empty, (await db.UserAccounts.SingleAsync(a => a.Id == session.Account.Id)).DisplayName);
    }

    [PostgreSqlFact]
    public async Task Returning_social_login_does_not_replace_an_existing_name()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory(); using var browser = app.Client();
        await Authenticate(app, browser, "google"); await Finish(browser);
        var original = (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!;
        Assert.Equal("Social shopper", original.Account.DisplayName);
        (await browser.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        app.Name = "A changed provider name";
        await Authenticate(app, browser, "google"); await Finish(browser);
        var returned = (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!;
        Assert.Equal(original.Account.Id, returned.Account.Id); Assert.Equal(original.Account.DisplayName, returned.Account.DisplayName);
    }

    [PostgreSqlTheory]
    [InlineData("google", false)]
    [InlineData("facebook", false)]
    [InlineData("microsoft", false)]
    [InlineData("google", true)]
    [InlineData("facebook", true)]
    [InlineData("microsoft", true)]
    public async Task Real_provider_handlers_create_and_reopen_the_same_account_without_retaining_tokens(string provider, bool missingEmail)
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory();
        using var browser = app.Client();
        browser.DefaultRequestHeaders.Add("X-Client-Timezone", "Pacific/Honolulu");
        if (missingEmail) app.Email = null;
        await Authenticate(app, browser, provider);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
        var result = await Finish(browser);
        Assert.Equal("registered", result.GetProperty("outcome").GetString());
        var session = await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me");
        var account = session!.Account.Id;
        Assert.False(session.Account.IsTrial);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            var user = await db.UserAccounts.SingleAsync(x => x.Id == account);
            Assert.Null(user.Email); Assert.Null(user.PasswordHash);
            Assert.Equal(provider, (await db.ExternalIdentities.SingleAsync()).Provider);
            var firstList = Assert.Single(await db.ShoppingLists.Where(x => x.UserAccountId == account).ToArrayAsync());
            Assert.Equal(ShoppingListNameService.First(app.Clock.Now.UtcDateTime, "Pacific/Honolulu"), firstList.Name);
            // A social-only contact address must never make the dummy unknown-user hash usable.
            user.Email = "social-contact@example.test"; await db.SaveChangesAsync();
        }
        (await browser.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/api/auth/login", new SignInRequest("social-contact@example.test", "Unknown account credential"))).StatusCode);
        app.Subject = "identity-1"; app.Email = "changed@example.test";
        await Authenticate(app, browser, provider);
        Assert.Equal("signed_in", (await Finish(browser)).GetProperty("outcome").GetString());
        Assert.Equal(account, (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!.Account.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/api/auth/social/complete", new { })).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Linking_requires_existing_method_proof_and_preserves_lists_paid_status_and_password()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory(); using var browser = app.Client();
        var owner = await Register(browser, "owner@example.test");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().UserAccounts.Where(x => x.Id == owner.Account.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPaid, true));
        var denied = await browser.PostAsJsonAsync("/api/auth/social/start", new SocialStartRequest("google", "link", owner.Account.Id));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        (await browser.PostAsJsonAsync("/api/auth/social/reauthenticate", new SocialPasswordProof(owner.Account.Id, Password))).EnsureSuccessStatusCode();
        await Authenticate(app, browser, "google", "link", owner.Account.Id);
        Assert.Equal("connected", (await Finish(browser)).GetProperty("outcome").GetString());
        var methods = await browser.GetFromJsonAsync<SocialMethodsView>("/api/auth/social/methods");
        Assert.True(methods!.HasPassword); Assert.True(methods.Providers.Single(x => x.Provider == "google").Connected);
        Assert.False(methods.RecentlyAuthenticated);
        (await browser.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        await Authenticate(app, browser, "google"); await Finish(browser);
        var reopened = (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!;
        Assert.Equal(owner.Account.Id, reopened.Account.Id); Assert.Equal(owner.ShoppingListId, reopened.ShoppingListId); Assert.True(reopened.Account.IsPaid);
        (await browser.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        (await browser.PostAsJsonAsync("/api/auth/login", new SignInRequest("owner@example.test", Password))).EnsureSuccessStatusCode();
    }

    [PostgreSqlFact]
    public async Task Matching_email_never_merges_accounts_and_a_provider_cannot_be_linked_twice()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory();
        using var first = app.Client(); var owner = await Register(first, "existing@example.test");
        using var outsider = app.Client(); app.Email = "existing@example.test";
        await Authenticate(app, outsider, "facebook");
        Assert.Equal(HttpStatusCode.Conflict, (await outsider.PostAsJsonAsync("/api/auth/social/complete", new { })).StatusCode);
        using (var scope = app.Services.CreateScope()) Assert.Single(await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().UserAccounts.ToArrayAsync());
        (await first.PostAsJsonAsync("/api/auth/social/reauthenticate", new SocialPasswordProof(owner.Account.Id, Password))).EnsureSuccessStatusCode();
        await Authenticate(app, first, "facebook", "link", owner.Account.Id); await Finish(first);
        using var second = app.Client(); var other = await Register(second, "other@example.test");
        (await second.PostAsJsonAsync("/api/auth/social/reauthenticate", new SocialPasswordProof(other.Account.Id, Password))).EnsureSuccessStatusCode();
        await Authenticate(app, second, "facebook", "link", other.Account.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/auth/social/complete", new { })).StatusCode);
        using (var scope = app.Services.CreateScope()) Assert.Equal(owner.Account.Id, (await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().ExternalIdentities.SingleAsync()).UserAccountId);
    }

    [PostgreSqlFact]
    public async Task Social_registration_creates_a_free_registered_account_and_persistent_list()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory(); using var browser = app.Client();
        await Authenticate(app, browser, "microsoft"); await Finish(browser);
        var session = (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!;
        Assert.False(session.Account.IsTrial); Assert.False(session.Account.IsPaid); Assert.Null(session.Account.ExpiresDate);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var list = await db.ShoppingLists.SingleAsync(l => l.Id == session.ShoppingListId);
        Assert.Equal(session.Account.Id, list.UserAccountId); Assert.Null(list.ExpiresDate);
        Assert.Equal(HttpStatusCode.Gone, (await browser.PostAsync("/api/auth/trial", null)).StatusCode);
        Assert.Single(await db.UserAccounts.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task Last_method_cannot_be_removed_and_verified_provider_proof_can_link_another_method()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory(); using var browser = app.Client();
        app.Email = null;
        await Authenticate(app, browser, "google"); await Finish(browser);
        var account = (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!.Account.Id;
        await Authenticate(app, browser, "google", "reauthenticate", account); await Finish(browser);
        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsJsonAsync("/api/auth/social/disconnect", new SocialDisconnectRequest(account, "google"))).StatusCode);
        await Authenticate(app, browser, "microsoft", "link", account); await Finish(browser);
        await Authenticate(app, browser, "microsoft", "reauthenticate", account); await Finish(browser);
        (await browser.PostAsJsonAsync("/api/auth/social/disconnect", new SocialDisconnectRequest(account, "google"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
        await Authenticate(app, browser, "microsoft"); await Finish(browser);
        Assert.Equal(account, (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!.Account.Id);
    }

    [PostgreSqlFact]
    public async Task Callback_and_completion_reject_csrf_replays_wrong_browser_and_account_switches()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory(); using var browser = app.Client();
        using var noHeader = app.Client(); noHeader.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await noHeader.PostAsJsonAsync("/api/auth/social/start", new SocialStartRequest("google", "signin"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/api/auth/social/complete", new { })).StatusCode);
        var start = await browser.PostAsJsonAsync("/api/auth/social/start", new SocialStartRequest("google", "signin"));
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query); var callback = query["redirect_uri"] + "?code=code&state=" + Uri.EscapeDataString(query["state"]!);
        using var wrongBrowser = app.Client(); var rejected = await wrongBrowser.GetAsync(callback);
        Assert.Contains("error=failed", rejected.Headers.Location!.ToString());
        var corrupt = await browser.GetAsync(query["redirect_uri"] + "?code=code&state=changed");
        Assert.Contains("error=failed", corrupt.Headers.Location!.ToString());
        await Authenticate(app, browser, "google");
        await Register(browser, "switched@example.test");
        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsJsonAsync("/api/auth/social/complete", new { })).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Cancellation_and_provider_failure_do_not_create_an_account_or_expose_errors()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory(); using var browser = app.Client();
        var start = await browser.PostAsJsonAsync("/api/auth/social/start", new SocialStartRequest("google", "signin"));
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        var response = await browser.GetAsync(query["redirect_uri"] + "?error=access_denied&error_description=PRIVATE_PROVIDER_DETAIL&state=" + Uri.EscapeDataString(query["state"]!));
        Assert.Contains("error=cancelled", response.Headers.Location!.ToString());
        Assert.DoesNotContain("PRIVATE", response.Headers.Location.ToString());
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().UserAccounts.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Social_only_account_adds_password_only_after_email_proof_and_invalidates_the_link()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory(); using var browser = app.Client();
        await Authenticate(app, browser, "google"); await Finish(browser);
        var account = (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!.Account.Id;
        await Authenticate(app, browser, "google", "reauthenticate", account); await Finish(browser);
        (await browser.PostAsJsonAsync("/api/auth/social/password/start", new SocialEmailRequest(account, "verified@example.test"))).EnsureSuccessStatusCode();
        string token;
        using (var scope = app.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<EmailOutboxService>();
            Assert.True(await outbox.DeliverOneAsync(default));
            var message = app.Transport.Messages.Single(); token = message.Body.Split("#verify=")[1].Split('\n')[0];
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            Assert.Null((await db.UserAccounts.SingleAsync()).PasswordHash);
        }
        (await browser.PostAsJsonAsync("/api/auth/social/password/complete", new SocialPasswordRequest(account, token, "New-password2!"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
        (await browser.PostAsJsonAsync("/api/auth/login", new SignInRequest("verified@example.test", "New-password2!"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsJsonAsync("/api/auth/social/password/complete", new SocialPasswordRequest(account, token, "New-password3!"))).StatusCode);
        using var final = app.Services.CreateScope(); Assert.NotNull((await final.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().UserAccounts.SingleAsync()).EmailVerifiedAt);
    }

    [PostgreSqlFact]
    public async Task Concurrent_linking_has_one_owner()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory();
        using var a = app.Client(); using var b = app.Client();
        var first = await Register(a, "first@example.test"); var second = await Register(b, "second@example.test");
        foreach (var pair in new[] { (a, first), (b, second) })
        {
            (await pair.Item1.PostAsJsonAsync("/api/auth/social/reauthenticate", new SocialPasswordProof(pair.Item2.Account.Id, Password))).EnsureSuccessStatusCode();
            await Authenticate(app, pair.Item1, "google", "link", pair.Item2.Account.Id);
        }
        var responses = await Task.WhenAll(a.PostAsJsonAsync("/api/auth/social/complete", new { }), b.PostAsJsonAsync("/api/auth/social/complete", new { })).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(responses, x => x.IsSuccessStatusCode); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        using var scope = app.Services.CreateScope(); Assert.Single(await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().ExternalIdentities.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Expired_flows_and_old_or_wrong_identity_proofs_cannot_change_methods()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SocialFactory(); using var browser = app.Client();
        await Authenticate(app, browser, "google"); app.Clock.Now += TimeSpan.FromMinutes(11);
        var expired = await browser.PostAsJsonAsync("/api/auth/social/complete", new { });
        // Either the protected temporary cookie or the database flow may expire first.
        Assert.Contains(expired.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict });
        using (var scope = app.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().UserAccounts.ToArrayAsync());
        var account = await Register(browser, "proof@example.test");
        (await browser.PostAsJsonAsync("/api/auth/social/reauthenticate", new SocialPasswordProof(account.Account.Id, Password))).EnsureSuccessStatusCode();
        app.Clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsJsonAsync("/api/auth/social/start", new SocialStartRequest("google", "link", account.Account.Id))).StatusCode);
        (await browser.PostAsJsonAsync("/api/auth/social/reauthenticate", new SocialPasswordProof(account.Account.Id, Password))).EnsureSuccessStatusCode();
        await Authenticate(app, browser, "google", "link", account.Account.Id); await Finish(browser);
        app.Subject = "different-provider-account";
        await Authenticate(app, browser, "google", "reauthenticate", account.Account.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsJsonAsync("/api/auth/social/complete", new { })).StatusCode);
    }

    [Fact]
    public void Enabled_providers_require_exact_trusted_origins_and_server_credentials()
    {
        var settings = new myshoppinglist_api.Configuration.SocialAuthenticationOptions();
        SocialAuthenticationSetup.Validate(settings, false);
        settings.Enabled = true; settings.WebsiteOrigin = "http://attacker.example";
        Assert.Throws<InvalidOperationException>(() => SocialAuthenticationSetup.Validate(settings, true));
        settings.WebsiteOrigin = "https://example.test/path";
        Assert.Throws<InvalidOperationException>(() => SocialAuthenticationSetup.Validate(settings, false));
        settings.WebsiteOrigin = "https://example.test"; settings.Google.Enabled = true;
        Assert.Throws<InvalidOperationException>(() => SocialAuthenticationSetup.Validate(settings, false));
        settings.Google.ClientId = "fixture"; settings.Google.ClientSecret = "fixture";
        SocialAuthenticationSetup.Validate(settings, false);
    }

    [Fact]
    public void Production_social_sign_in_requires_persistent_protected_keys_even_when_email_is_off()
    {
        var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = Environments.Production });
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["SocialAuthentication:Enabled"] = "true", ["SocialAuthentication:WebsiteOrigin"] = "https://website.example" }).Build();
        Assert.Throws<InvalidOperationException>(() => host.Services.AddSocialAuthentication(config, host.Environment));
    }

    private static async Task<CurrentSessionResponse> Register(HttpClient browser, string email)
    { var response = await browser.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Shopper")); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<CurrentSessionResponse>())!; }
    private static async Task<JsonElement> Finish(HttpClient browser, bool separate = false)
    { var response = await browser.PostAsJsonAsync("/api/auth/social/complete", new SocialCompleteRequest(separate)); response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<JsonElement>(); }
    private static async Task Authenticate(SocialFactory app, HttpClient browser, string provider, string purpose = "signin", long? account = null)
    {
        var challenge = await browser.PostAsJsonAsync("/api/auth/social/start", new SocialStartRequest(provider, purpose, account));
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        Assert.Equal("https://localhost/api/auth/social/callback/" + provider, query["redirect_uri"].ToString());
        if (provider != "facebook") Assert.Equal("S256", query["code_challenge_method"].ToString());
        var callback = await browser.GetAsync(query["redirect_uri"] + "?code=fixture-code&state=" + Uri.EscapeDataString(query["state"]!));
        Assert.Equal("https://localhost/account/social", callback.Headers.Location!.ToString());
    }

    internal sealed class SocialFactory(string websiteOrigin = "https://localhost") : WebApplicationFactory<Program>
    {
        public string Subject { get; set; } = "identity-1";
        public string? Email { get; set; } = "social@example.test";
        public string? Name { get; set; } = "Social shopper";
        public CaptureMail Transport { get; } = new();
        public PersistenceClock Clock { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AiIntegration:Enabled", "false");
            builder.UseSetting("SocialAuthentication:Enabled", "true"); builder.UseSetting("SocialAuthentication:WebsiteOrigin", websiteOrigin);
            foreach (var name in new[] { "Google", "Facebook", "Microsoft" })
            { builder.UseSetting($"SocialAuthentication:{name}:Enabled", "true"); builder.UseSetting($"SocialAuthentication:{name}:ClientId", "fixture-client"); builder.UseSetting($"SocialAuthentication:{name}:ClientSecret", "fixture-secret"); }
            builder.UseSetting("EmailDelivery:Enabled", "true"); builder.UseSetting("EmailDelivery:Host", "127.0.0.1"); builder.UseSetting("EmailDelivery:Port", "2525");
            builder.UseSetting("EmailDelivery:FromAddress", "alerts@example.test"); builder.UseSetting("EmailDelivery:AllowLoopbackWithoutTls", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>(); services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.RemoveAll<IEmailTransport>(); services.AddSingleton<IEmailTransport>(Transport);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>(); services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
                services.PostConfigure<GoogleOptions>(SocialAuthenticationSetup.Scheme("google"), o => o.Backchannel = new(new Backchannel(this)));
                services.PostConfigure<FacebookOptions>(SocialAuthenticationSetup.Scheme("facebook"), o => o.Backchannel = new(new Backchannel(this)));
                services.PostConfigure<MicrosoftAccountOptions>(SocialAuthenticationSetup.Scheme("microsoft"), o => o.Backchannel = new(new Backchannel(this)));
            });
        }
        public HttpClient Client() { var c = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); c.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1"); return c; }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); await Serilog.Log.CloseAndFlushAsync(); }
    }
    private sealed class Backchannel(SocialFactory source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object payload = request.Method == HttpMethod.Post ? new { access_token = "fixture-access-token", token_type = "Bearer", expires_in = 3600 }
                : new { id = source.Subject, sub = source.Subject, email = source.Email, mail = source.Email, userPrincipalName = source.Email, name = source.Name, displayName = source.Name };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        }
    }
    internal sealed class CaptureMail : IEmailTransport
    {
        public List<OutboundEmail> Messages { get; } = [];
        public Task SendAsync(Guid messageId, OutboundEmail message, CancellationToken ct) { Messages.Add(message); return Task.CompletedTask; }
    }
}
