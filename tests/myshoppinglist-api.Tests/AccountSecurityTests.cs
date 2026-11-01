using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using OpenIddict.EntityFrameworkCore.Models;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class AccountSecurityTests
{
    private const string Password = "Original-password9!", Changed = "Replacement-password8!";

    [PostgreSqlFact]
    public async Task Recovery_acknowledgements_do_not_reveal_accounts_and_the_queue_encrypts_addresses_and_groups_repeats()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SecurityFactory();
        using var owner = app.Client(); await Register(owner, "owner@example.test"); using var anonymous = app.Client();
        var known = await anonymous.PostAsJsonAsync("/api/auth/recovery/request", new RecoveryRequest("owner@example.test"));
        var unknown = await anonymous.PostAsJsonAsync("/api/auth/recovery/request", new RecoveryRequest("unknown@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode); Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.True(known.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/auth/recovery/request", new RecoveryRequest("OWNER@example.test"))).StatusCode);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.Equal(2, await db.EmailOutboxMessages.CountAsync());
        foreach (var row in await db.EmailOutboxMessages.ToArrayAsync())
        { Assert.DoesNotContain("@example.test", row.ProtectedPayload); Assert.Equal(64, row.RecipientHash!.Length); }
        var outbox = scope.ServiceProvider.GetRequiredService<EmailOutboxService>();
        while (await outbox.DeliverOneAsync(default)) { }
        Assert.Single(app.Transport.Messages); Assert.Contains("#token=", app.Transport.Messages.Single().Email.Body);
        Assert.Equal(new[] { "sent", "suppressed" }, await db.EmailOutboxMessages.OrderBy(e => e.Status).Select(e => e.Status).ToArrayAsync());
        Assert.All(await db.EmailOutboxMessages.AsNoTracking().ToArrayAsync(), row => Assert.Null(row.ProtectedPayload));
    }

    [PostgreSqlFact]
    public async Task Reset_is_single_use_revokes_all_owned_credentials_and_keeps_other_accounts_and_shopping_lists()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SecurityFactory();
        using var owner = app.Client(); using var secondSession = app.Client(); using var other = app.Client(); using var anonymous = app.Client();
        var account = await Register(owner, "owner@example.test"); var otherAccount = await Register(other, "other@example.test");
        (await secondSession.PostAsJsonAsync("/api/auth/login", new SignInRequest("owner@example.test", Password))).EnsureSuccessStatusCode();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            var authorization = new OpenIddictEntityFrameworkCoreAuthorization { Id = Guid.NewGuid().ToString(), Subject = account.Account.Id.ToString(), Status = "valid", Type = "permanent" };
            db.Add(authorization); db.Add(new OpenIddictEntityFrameworkCoreToken { Id = Guid.NewGuid().ToString(), Subject = account.Account.Id.ToString(), Status = "valid", Authorization = authorization, Type = "refresh_token" });
            var connection = new AiConnection { UserAccountId = account.Account.Id, AuthorizationId = authorization.Id, ClientId = "test", DisplayName = "Test", GrantedScopes = "lists.read", CreatedDate = app.Clock.Now.UtcDateTime };
            db.AiConnections.Add(connection);
            var session = await db.UserSessions.FirstAsync(s => s.UserAccountId == account.Account.Id);
            db.ExtensionDevices.Add(new() { UserAccountId = account.Account.Id, UserSessionId = session.Id, InstallationId = Guid.NewGuid(),
                TokenHash = SessionToken.Hash(SessionToken.Create()), ExtensionId = new string('a', 32), ExtensionVersion = "1", LinkedAt = app.Clock.Now.UtcDateTime });
            db.ExtensionTasks.Add(new() { UserAccountId = account.Account.Id, AiConnectionId = connection.Id, RequestId = Guid.NewGuid(), RequestHash = new string('A', 64),
                Type = "capture", Status = "queued", Retailer = "coles", Url = "https://www.coles.com.au/product/123456", ShoppingListId = account.ShoppingListId, Quantity = 1,
                CreatedAt = app.Clock.Now.UtcDateTime, ExpiresAt = app.Clock.Now.UtcDateTime.AddMinutes(10) });
            await db.SaveChangesAsync();
        }
        var token = await ResetLink(app, anonymous, "owner@example.test");
        var result = await anonymous.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(token, Changed));
        Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(token, Changed))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await secondSession.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new SignInRequest("owner@example.test", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/login", new SignInRequest("owner@example.test", Changed))).StatusCode);
        using var verify = app.Services.CreateScope(); var state = verify.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.True(await state.ShoppingLists.AnyAsync(l => l.Id == account.ShoppingListId && l.UserAccountId == account.Account.Id));
        Assert.All(await state.AiConnections.ToArrayAsync(), c => Assert.NotNull(c.RevokedDate));
        Assert.All(await state.ExtensionDevices.ToArrayAsync(), d => Assert.NotNull(d.RevokedAt));
        Assert.Equal("cancelled", (await state.ExtensionTasks.SingleAsync()).Status);
        Assert.All(await state.Set<OpenIddictEntityFrameworkCoreAuthorization>().ToArrayAsync(), a => Assert.Equal("revoked", a.Status));
        Assert.All(await state.Set<OpenIddictEntityFrameworkCoreToken>().ToArrayAsync(), t => Assert.Equal("revoked", t.Status));
        var untouched = await state.UserAccounts.SingleAsync(a => a.Id == otherAccount.Account.Id);
        Assert.True(PasswordHasher.Verify(Password, untouched.PasswordHash, untouched.PasswordSalt));
    }

    [PostgreSqlFact]
    public async Task Concurrent_reset_attempts_have_only_one_winner_and_tampered_links_do_not_change_passwords()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SecurityFactory();
        using var owner = app.Client(); using var a = app.Client(); using var b = app.Client();
        await Register(owner, "owner@example.test"); var token = await ResetLink(app, a, "owner@example.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(token + "x", Changed))).StatusCode);
        var outcomes = await Task.WhenAll(a.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(token, Changed)),
            b.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(token, "Another-password7!")));
        Assert.Single(outcomes, r => r.StatusCode == HttpStatusCode.NoContent); Assert.Single(outcomes, r => r.StatusCode == HttpStatusCode.BadRequest);
        app.Clock.Now += TimeSpan.FromMinutes(16);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(token, Changed))).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Expired_tickets_and_weak_passwords_leave_the_existing_credential_valid()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SecurityFactory();
        using var owner = app.Client(); using var anonymous = app.Client();
        await Register(owner, "owner@example.test"); var token = await ResetLink(app, anonymous, "owner@example.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(token, "weak"))).StatusCode);
        app.Clock.Now += TimeSpan.FromMinutes(16);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(token, Changed))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/login", new SignInRequest("owner@example.test", Password))).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Password_change_requires_current_proof_and_account_context_and_invalidates_issued_links()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SecurityFactory();
        using var owner = app.Client(); using var anonymous = app.Client();
        var account = await Register(owner, "owner@example.test"); var token = await ResetLink(app, anonymous, "owner@example.test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest(account.Account.Id, Password, Changed))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest(account.Account.Id + 1, Password, Changed))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest(account.Account.Id, "wrong", Changed))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest(account.Account.Id, Password, Changed))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/recovery/complete", new ResetPasswordRequest(token, Changed))).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Recovery_obeys_csrf_rate_limits_disabled_state_and_password_policy()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new SecurityFactory();
        using var anonymous = app.Client(); anonymous.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("/api/auth/recovery/request", new RecoveryRequest("unknown@example.test"))).StatusCode);
        anonymous.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/auth/recovery/request", new RecoveryRequest("unknown@example.test"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.PostAsJsonAsync("/api/auth/recovery/request", new RecoveryRequest("unknown@example.test"))).StatusCode);
        await using var disabled = new SecurityFactory(false); using var client = disabled.Client();
        Assert.Contains("false", await client.GetStringAsync("/api/auth/recovery"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/auth/recovery/request", new RecoveryRequest("unknown@example.test"))).StatusCode);
    }

    private static async Task<CurrentSessionResponse> Register(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, "Security tester"));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
    }
    private static async Task<string> ResetLink(SecurityFactory app, HttpClient client, string email)
    {
        (await client.PostAsJsonAsync("/api/auth/recovery/request", new RecoveryRequest(email))).EnsureSuccessStatusCode();
        using var scope = app.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<EmailOutboxService>().DeliverOneAsync(default);
        var body = app.Transport.Messages.Last(m => m.Email.Subject == "Reset your MyShoppingList password").Email.Body;
        var link = body.Split('\n').Single(line => line.StartsWith("https://frontend.example/", StringComparison.Ordinal));
        return Uri.UnescapeDataString(new Uri(link).Fragment[7..]);
    }

    internal sealed class CaptureTransport : IEmailTransport
    {
        public ConcurrentQueue<(Guid Id, OutboundEmail Email)> Messages { get; } = new();
        public int Failures { get; set; }
        public Task SendAsync(Guid id, OutboundEmail email, CancellationToken ct)
        {
            if (Failures-- > 0) throw new IOException("Do not log private SMTP exception contents.");
            Messages.Enqueue((id, email)); return Task.CompletedTask;
        }
    }
    internal sealed class SecurityClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class SecurityFactory(bool recovery = true, bool operations = false) : WebApplicationFactory<Program>
    {
        public CaptureTransport Transport { get; } = new();
        public SecurityClock Clock { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); builder.UseSetting("EmailDelivery:Enabled", "true");
            builder.UseSetting("EmailDelivery:Host", "127.0.0.1"); builder.UseSetting("EmailDelivery:Port", "2525");
            builder.UseSetting("EmailDelivery:FromAddress", "alerts@example.test"); builder.UseSetting("EmailDelivery:AllowLoopbackWithoutTls", "true");
            builder.UseSetting("AccountRecovery:Enabled", recovery ? "true" : "false"); builder.UseSetting("AccountRecovery:WebsiteOrigin", "https://frontend.example");
            if (operations)
            {
                builder.UseSetting("Operations:Enabled", "true"); builder.UseSetting("Operations:SimulateDelivery", "false");
                builder.UseSetting("Operations:IncidentRecipients:0", "alerts@example.test");
            }
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>(); services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.RemoveAll<IEmailTransport>(); services.AddSingleton<IEmailTransport>(Transport);
                services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                if (operations) services.PostConfigure<OperationsOptions>(o => { o.SimulateDelivery = false; o.IncidentRecipients = ["alerts@example.test"]; });
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
        public HttpClient Client()
        {
            var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1"); return client;
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); await Serilog.Log.CloseAndFlushAsync(); }
    }
}
