using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using OpenIddict.EntityFrameworkCore.Models;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class ExtensionBridgeTests
{
    private const string ExtensionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Url = "https://www.coles.com.au/product/123456";

    [PostgreSqlFact]
    public async Task Device_credentials_are_hashed_scoped_rotated_and_revoked_with_the_source_session()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var app = new BridgeFactory();
        using var browser = app.Client(true);
        var account = await RegisterAsync(browser, "Owner");
        var installation = Guid.NewGuid();
        var credentials = await LinkAsync(browser, account.Account.Id, installation);
        using var device = app.Device(credentials);
        Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync("/api/extension-device/heartbeat", new ExtensionDeviceHeartbeat("0.9.0", ["coles"], true, 1))).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().ExtensionDevices.AsNoTracking().SingleAsync();
            Assert.Equal(SessionToken.Hash(credentials.DeviceToken), stored.TokenHash); Assert.NotEqual(credentials.DeviceToken, stored.TokenHash);
        }
        foreach (var path in new[] { "/api/auth/me", "/api/shopping-lists/manage", "/api/account/extension-devices" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/extension-device/tasks")).StatusCode);
        var summary = await browser.GetStringAsync("/api/account/extension-devices");
        Assert.DoesNotContain(credentials.DeviceToken, summary); Assert.DoesNotContain("tokenHash", summary); Assert.DoesNotContain("installationId", summary);
        var rotated = await LinkAsync(browser, account.Account.Id, installation);
        Assert.Equal(credentials.DeviceId, rotated.DeviceId); Assert.NotEqual(credentials.DeviceToken, rotated.DeviceToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/extension-device/tasks")).StatusCode);
        using var replacement = app.Device(rotated);
        Assert.Equal(HttpStatusCode.OK, (await replacement.GetAsync("/api/extension-device/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replacement.GetAsync("/api/extension-device/tasks")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Capture_requires_approval_assignment_fresh_evidence_and_atomic_private_save()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var app = new BridgeFactory();
        using var browser = app.Client(true); var account = await RegisterAsync(browser, "Capture owner");
        var credentials = await LinkAsync(browser, account.Account.Id, Guid.NewGuid()); using var device = app.Device(credentials);
        await SeenAsync(device);
        var authorization = await GrantAsync(app, account.Account.Id);
        var requestId = Guid.NewGuid();
        var queued = await RequestAsync(app, account.Account.Id, authorization, requestId, account.ShoppingListId!.Value);
        Assert.Equal("queued", queued.Status); Assert.NotNull(queued.Task);
        var again = await RequestAsync(app, account.Account.Id, authorization, requestId, account.ShoppingListId.Value);
        Assert.Equal(queued.Task.Id, again.Task!.Id);
        var pending = (await device.GetFromJsonAsync<ExtensionTaskInfo[]>("/api/extension-device/tasks"))!;
        Assert.Equal("awaiting_user", Assert.Single(pending).Status);
        var id = queued.Task.Id;
        var noApproval = await device.PostAsJsonAsync($"/api/extension-device/tasks/{id}/start", new ExtensionTaskClaimRequest(SessionToken.Create()));
        Assert.Equal(HttpStatusCode.Forbidden, noApproval.StatusCode);
        var decision = await device.PostAsJsonAsync($"/api/extension-device/tasks/{id}/decision", new ExtensionTaskDecision(true));
        decision.EnsureSuccessStatusCode(); var claim = (await decision.Content.ReadFromJsonAsync<ExtensionTaskClaim>())!;
        Assert.NotNull(claim.ClaimToken);
        using var anotherDevice = app.Device(await LinkAsync(browser, account.Account.Id, Guid.NewGuid())); await SeenAsync(anotherDevice);
        Assert.Equal(HttpStatusCode.Conflict, (await anotherDevice.PostAsJsonAsync($"/api/extension-device/tasks/{id}/decision", new ExtensionTaskDecision(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anotherDevice.PostAsJsonAsync($"/api/extension-device/tasks/{id}/start", new ExtensionTaskClaimRequest(claim.ClaimToken))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync($"/api/extension-device/tasks/{id}/start", new ExtensionTaskClaimRequest(claim.ClaimToken))).StatusCode);
        var evidence = Evidence();
        var body = new ExtensionTaskCapture(claim.ClaimToken, evidence, app.Clock.Now);
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync($"/api/extension-device/tasks/{id}/capture", body with { ObservedAt = app.Clock.Now.AddMinutes(-1) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync($"/api/extension-device/tasks/{id}/preview", body)).StatusCode);
        using (var scope = app.Services.CreateScope()) Assert.Empty(await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().Products.ToArrayAsync());
        var completed = await device.PostAsJsonAsync($"/api/extension-device/tasks/{id}/capture", body);
        completed.EnsureSuccessStatusCode(); var result = (await completed.Content.ReadFromJsonAsync<ExtensionTaskInfo>())!;
        Assert.Equal("completed", result.Status); Assert.True(result.Result!.Value.GetProperty("priceSaved").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync($"/api/extension-device/tasks/{id}/capture", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync($"/api/extension-device/tasks/{id}/capture", body with { ObservedAt = app.Clock.Now.AddSeconds(-1) })).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            Assert.Equal(account.Account.Id, (await db.Products.SingleAsync()).UserAccountId);
            Assert.Single(await db.ShoppingListProducts.ToArrayAsync());
            Assert.Equal(2, (await db.ShoppingListProducts.SingleAsync()).Quantity);
            Assert.Single(await db.ShopProductPrices.Where(p => p.UserAccountId == account.Account.Id).ToArrayAsync());
            Assert.Empty(await db.ColesExtensionTasks.ToArrayAsync());
        }
    }

    [PostgreSqlFact]
    public async Task Other_accounts_devices_and_ai_connections_cannot_see_or_claim_a_task()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new BridgeFactory();
        using var a = app.Client(true); using var b = app.Client(true);
        var first = await RegisterAsync(a, "First"); var second = await RegisterAsync(b, "Second");
        var keyA = await LinkAsync(a, first.Account.Id, Guid.NewGuid()); var keyB = await LinkAsync(b, second.Account.Id, Guid.NewGuid());
        using var deviceA = app.Device(keyA); using var deviceB = app.Device(keyB); await SeenAsync(deviceA); await SeenAsync(deviceB);
        var authorization = await GrantAsync(app, first.Account.Id);
        var otherAuthorization = await GrantAsync(app, first.Account.Id);
        var queued = await RequestAsync(app, first.Account.Id, authorization, Guid.NewGuid(), first.ShoppingListId!.Value);
        var id = queued.Task!.Id;
        Assert.Empty((await deviceB.GetFromJsonAsync<ExtensionTaskInfo[]>("/api/extension-device/tasks"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await deviceB.GetAsync($"/api/extension-device/tasks/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await deviceB.PostAsJsonAsync($"/api/extension-device/tasks/{id}/decision", new ExtensionTaskDecision(true))).StatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ExtensionTaskService>().ReadAsync(first.Account.Id, otherAuthorization, Guid.Parse(id), default));
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/account/extension-devices/{keyA.DeviceId}")).StatusCode);
        using var forged = app.Device(keyA with { DeviceId = keyB.DeviceId });
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/extension-device/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/account/extension-devices/{keyA.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await deviceA.GetAsync("/api/extension-device/tasks")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Missing_offline_denied_and_expired_devices_are_not_reported_as_success()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new BridgeFactory();
        using var browser = app.Client(true); var account = await RegisterAsync(browser, "States");
        var authorization = await GrantAsync(app, account.Account.Id);
        Assert.Equal("not_linked", (await RequestAsync(app, account.Account.Id, authorization, Guid.NewGuid(), account.ShoppingListId!.Value)).Status);
        var key = await LinkAsync(browser, account.Account.Id, Guid.NewGuid()); using var device = app.Device(key);
        Assert.Equal("offline", (await RequestAsync(app, account.Account.Id, authorization, Guid.NewGuid(), account.ShoppingListId.Value)).Status);
        await device.PostAsJsonAsync("/api/extension-device/heartbeat", new ExtensionDeviceHeartbeat("0.9.0", [], true, 1));
        Assert.Equal("needs_permission", (await RequestAsync(app, account.Account.Id, authorization, Guid.NewGuid(), account.ShoppingListId.Value)).Status);
        await SeenAsync(device);
        var denied = (await RequestAsync(app, account.Account.Id, authorization, Guid.NewGuid(), account.ShoppingListId.Value)).Task!;
        var rejection = await device.PostAsJsonAsync($"/api/extension-device/tasks/{denied.Id}/decision", new ExtensionTaskDecision(false));
        rejection.EnsureSuccessStatusCode(); Assert.Equal("cancelled", (await rejection.Content.ReadFromJsonAsync<ExtensionTaskClaim>())!.Task.Status);
        var expires = (await RequestAsync(app, account.Account.Id, authorization, Guid.NewGuid(), account.ShoppingListId.Value)).Task!;
        app.Clock.Now += TimeSpan.FromMinutes(11);
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync($"/api/extension-device/tasks/{expires.Id}/decision", new ExtensionTaskDecision(true))).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ExtensionTaskService>();
            Assert.Equal("expired", (await service.ReadAsync(account.Account.Id, authorization, Guid.Parse(expires.Id), default))!.Status);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().Products.ToArrayAsync());
        }
    }

    [PostgreSqlFact]
    public async Task Search_is_bounded_unverified_idempotent_and_stops_after_ai_revocation()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new BridgeFactory();
        using var browser = app.Client(true); var account = await RegisterAsync(browser, "Search");
        using var device = app.Device(await LinkAsync(browser, account.Account.Id, Guid.NewGuid())); await SeenAsync(device);
        var authorization = await GrantAsync(app, account.Account.Id);
        async Task<ExtensionTaskInfo> Search()
        {
            using var scope = app.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<ExtensionTaskService>().RequestAsync(account.Account.Id,
                authorization, Guid.NewGuid(), "search", "rice", "coles", null, 1, default)).Task!;
        }
        async Task<ExtensionTaskClaim> Approve(ExtensionTaskInfo task)
        {
            var approved = await device.PostAsJsonAsync($"/api/extension-device/tasks/{task.Id}/decision", new ExtensionTaskDecision(true));
            approved.EnsureSuccessStatusCode(); var claim = (await approved.Content.ReadFromJsonAsync<ExtensionTaskClaim>())!;
            (await device.PostAsJsonAsync($"/api/extension-device/tasks/{task.Id}/start", new ExtensionTaskClaimRequest(claim.ClaimToken!))).EnsureSuccessStatusCode();
            return claim;
        }
        var search = await Search(); var claim = await Approve(search);
        var path = $"/api/extension-device/tasks/{search.Id}/search";
        var body = new ExtensionTaskSearch(claim.ClaimToken!, [new(Url, null)], false, app.Clock.Now);
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(path, body with { Candidates = [null!] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(path, body with { Candidates = Enumerable.Range(0, 6).Select(i => new ExtensionSearchCandidate($"https://www.coles.com.au/product/{123456 + i}", null)).ToArray() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(path, body with { Candidates = [new("https://evil.test/product/123456", "Rice")] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsJsonAsync(path, body with { Candidates = [], EmptyConfirmed = false })).StatusCode);
        var completed = await device.PostAsJsonAsync(path, body); completed.EnsureSuccessStatusCode();
        var result = (await completed.Content.ReadFromJsonAsync<ExtensionTaskInfo>())!;
        Assert.False(result.Result!.Value.GetProperty("pricesVerified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.Result.Value.GetProperty("candidates")[0].GetProperty("name").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync(path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync(path, body with { Candidates = [], EmptyConfirmed = true })).StatusCode);
        var revoked = await Search(); var revokeClaim = await Approve(revoked);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            await db.AiConnections.Where(c => c.AuthorizationId == authorization).ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedDate, app.Clock.Now.UtcDateTime));
            Assert.Empty(await db.Products.ToArrayAsync()); Assert.Empty(await db.ShopProductPrices.ToArrayAsync());
        }
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync($"/api/extension-device/tasks/{revoked.Id}/search", body with { ClaimToken = revokeClaim.ClaimToken! })).StatusCode);
        var ended = (await device.GetFromJsonAsync<ExtensionTaskInfo>($"/api/extension-device/tasks/{revoked.Id}"))!;
        Assert.Equal("cancelled", ended.Status); Assert.Equal("origin_revoked", ended.ErrorCode);
    }

    private static ColesEvidence Evidence() => new(null, [JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org", ["@type"] = "Product", ["sku"] = "123456", ["url"] = Url,
        ["name"] = "Bridge rice 1kg", ["brand"] = new { name = "Bridge" },
        ["offers"] = new Dictionary<string, object> { ["@type"] = "Offer", ["price"] = 4, ["priceCurrency"] = "AUD", ["availability"] = "https://schema.org/InStock" }
    })]);
    private static async Task<CurrentSessionResponse> RegisterAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(Guid.NewGuid() + "@example.test", "Long test passphrase 123!", name));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
    }
    private static async Task<ExtensionDeviceCredentials> LinkAsync(HttpClient client, long account, Guid installation)
    {
        var response = await client.PostAsJsonAsync("/api/user-extension/devices", new ExtensionDeviceRegistration(account, installation, "0.9.0", 1));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<ExtensionDeviceCredentials>())!;
    }
    private static async Task SeenAsync(HttpClient device) => (await device.PostAsJsonAsync("/api/extension-device/heartbeat", new ExtensionDeviceHeartbeat("0.9.0", ["coles", "woolworths", "aldi"], true, 1))).EnsureSuccessStatusCode();
    private static async Task<string> GrantAsync(BridgeFactory app, long account)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var id = Guid.NewGuid().ToString();
        db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Add(new() { Id = id, Status = "valid", Type = "permanent", Subject = account.ToString(CultureInfo.InvariantCulture), CreationDate = app.Clock.Now.UtcDateTime });
        db.AiConnections.Add(new() { UserAccountId = account, AuthorizationId = id, ClientId = "bridge-test", DisplayName = "Approved test AI", GrantedScopes = "extension.capture", CreatedDate = app.Clock.Now.UtcDateTime });
        await db.SaveChangesAsync(); return id;
    }
    private static async Task<ExtensionTaskRequestResult> RequestAsync(BridgeFactory app, long account, string authorization, Guid request, long list)
    {
        using var scope = app.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ExtensionTaskService>()
            .RequestAsync(account, authorization, request, "capture", Url, null, list, 2, default);
    }
    private sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public TestClock Clock { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in new Dictionary<string, string> { ["ExtensionBridge:Enabled"] = "true", ["UserExtension:PersonalCapturesEnabled"] = "true",
                ["UserExtension:AllowedExtensionIds:0"] = ExtensionId, ["AiIntegration:Enabled"] = "true", ["AiIntegration:Issuer"] = "https://localhost/",
                ["AiIntegration:Resource"] = "https://localhost/mcp", ["AiIntegration:FrontendOrigin"] = "https://frontend.example", ["AiIntegration:DevelopmentEphemeralKeys"] = "true" }) builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>(); services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.PostConfigure<UserExtensionOptions>(o => { o.AllowedExtensionIds = [ExtensionId]; o.PersonalCapturesEnabled = true; });
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>(); services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
        public HttpClient Client(bool cookies)
        {
            var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false, HandleCookies = cookies });
            client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1"); client.DefaultRequestHeaders.Add("X-MyShoppingList-Extension", ExtensionId); return client;
        }
        public HttpClient Device(ExtensionDeviceCredentials credentials)
        {
            var client = Client(false); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.DeviceToken);
            client.DefaultRequestHeaders.Add(ExtensionDeviceAuthenticationHandler.DeviceIdHeader, credentials.DeviceId.ToString());
            client.DefaultRequestHeaders.Add("Origin", "chrome-extension://" + ExtensionId); return client;
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); await Serilog.Log.CloseAndFlushAsync(); }
    }
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
