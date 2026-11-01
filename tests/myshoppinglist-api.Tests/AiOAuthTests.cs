using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Authentication;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Mcp;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class AiOAuthTests
{
    [PostgreSqlFact]
    public async Task Consent_rechecks_the_source_session_after_credentials_are_revoked()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OAuthFactory();
        using var browser = app.Client(true);
        var response = await browser.PostAsJsonAsync("/api/auth/register", new RegisterRequest("revoked-consent@example.test", "Consent-password9!", "Consent tester"));
        response.EnsureSuccessStatusCode(); var account = (await response.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var session = await db.UserSessions.SingleAsync(s => s.UserAccountId == account.Account.Id);
        await db.UserSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedDate, app.Clock.Now.UtcDateTime));
        var service = scope.ServiceProvider.GetRequiredService<AiConnectionService>();
        var error = await Assert.ThrowsAsync<myshoppinglist_api.Services.UserImportException>(() =>
            service.ApproveAsync(account.Account.Id, "test-ai", ["lists.read"], default, session.Id));
        Assert.Equal(403, error.Status); Assert.Empty(await db.AiConnections.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Code_pkce_consent_mcp_read_and_immediate_revocation_use_real_protocol_and_sdk()
    {
        var measurements = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) => { if (instrument.Meter.Name == "MyShoppingList.Mcp") meter.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Enqueue(tags.ToArray()));
        listener.Start();
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var app = new OAuthFactory();
        using var browser = app.Client(true);
        using var remote = app.Client(false);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AiConnectionService>().EnsureClientsAsync(default);
        var register = await browser.PostAsJsonAsync("/api/auth/register", new RegisterRequest(Guid.NewGuid() + "@example.test", "Long test passphrase 123!", "Account A"));
        register.EnsureSuccessStatusCode();
        var session = (await register.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
        using var otherBrowser = app.Client(true);
        (await otherBrowser.PostAsJsonAsync("/api/auth/register", new RegisterRequest(Guid.NewGuid() + "@example.test", "Long test passphrase 123!", "Account B"))).EnsureSuccessStatusCode();
        var other = (await otherBrowser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!;
        var unauthenticated = await remote.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Contains("resource_metadata", unauthenticated.Headers.WwwAuthenticate.ToString());
        var metadata = await remote.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal("https://localhost/mcp", metadata.GetProperty("resource").GetString());
        var discovery = await remote.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        Assert.Equal("https://localhost/connect/token", discovery.GetProperty("token_endpoint").GetString());

        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var query = new Dictionary<string, string?> { ["client_id"] = "test-ai", ["redirect_uri"] = "https://client.example/callback",
            ["response_type"] = "code", ["scope"] = "lists.read offline_access", ["resource"] = "https://localhost/mcp",
            ["code_challenge_method"] = "S256", ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["state"] = "state-test" };
        foreach (var (key, value) in new[] { ("code_challenge", ""), ("code_challenge_method", "plain"), ("scope", "lists.write"), ("resource", "https://other.example/mcp"), ("redirect_uri", "https://attacker.example/callback") })
        {
            var invalid = new Dictionary<string, string?>(query) { [key] = value };
            var denied = await remote.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", invalid));
            Assert.DoesNotContain("code=", denied.Headers.Location?.ToString() ?? "");
            if (denied.Headers.Location is { } location) Assert.True(QueryHelpers.ParseQuery(location.Query).ContainsKey("error"));
            else Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            if (key == "redirect_uri") Assert.Null(denied.Headers.Location);
        }
        var cached = await remote.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", query));
        Assert.True(cached.StatusCode == HttpStatusCode.Redirect, await cached.Content.ReadAsStringAsync());
        // OpenIddict first redirects to the cached authorization request, then the application consent page.
        var consent = await remote.GetAsync(cached.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        Assert.StartsWith("https://frontend.example/ai/connect?", consent.Headers.Location!.ToString());
        var requestUri = QueryHelpers.ParseQuery(consent.Headers.Location.Query)["request_uri"].ToString();
        var preview = await browser.PostAsync("/api/ai/oauth/preview", Form(("request_uri", requestUri)));
        Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
        var description = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Test AI", description.GetProperty("clientName").GetString());
        var account = description.GetProperty("accountId").GetString()!;
        var changedAccount = await browser.PostAsync("/api/ai/oauth/authorize", Form(("request_uri", requestUri),
            ("decision", "approve"), ("expected_account_id", "0"), ("approved_scopes", "lists.read offline_access")));
        Assert.Equal(HttpStatusCode.Conflict, changedAccount.StatusCode);
        var approved = await browser.PostAsync("/api/ai/oauth/authorize", Form(("request_uri", requestUri),
            ("decision", "approve"), ("expected_account_id", account), ("approved_scopes", "lists.read offline_access")));
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        var callback = QueryHelpers.ParseQuery(approved.Headers.Location!.Query);
        Assert.Equal("state-test", callback["state"]);
        var tokenResponse = await remote.PostAsync("/connect/token", Form(("grant_type", "authorization_code"),
            ("client_id", "test-ai"), ("redirect_uri", "https://client.example/callback"), ("code", callback["code"]!),
            ("code_verifier", verifier), ("resource", "https://localhost/mcp")));
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(token.TryGetProperty("refresh_token", out var refresh));
        remote.DefaultRequestHeaders.Authorization = new("Bearer", token.GetProperty("access_token").GetString());
        await using var transport = new HttpClientTransport(new() { Endpoint = new("https://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp }, remote);
        await using var client = await McpClient.CreateAsync(transport);
        Assert.Contains(await client.ListToolsAsync(), t => t.Name == "get_my_lists");
        var result = await client.CallToolAsync("get_my_lists");
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(new[] { "get_current_list", "get_list_items", "get_my_lists", "search_my_products" }, (await client.ListToolsAsync()).Select(t => t.Name).Order().ToArray());
        Assert.Equal(true, (await client.CallToolAsync("search_my_products")).IsError); // No products.read grant.
        Assert.Equal(true, (await client.CallToolAsync("get_list_items", new Dictionary<string, object?> { ["listId"] = other.ShoppingListId!.Value.ToString() })).IsError);
        var own = await client.CallToolAsync("get_list_items", new Dictionary<string, object?> { ["listId"] = session.ShoppingListId!.Value.ToString() });
        Assert.NotEqual(true, own.IsError);
        using (var forgedOrigin = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) })
        {
            forgedOrigin.Headers.Add("Origin", "https://attacker.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await remote.SendAsync(forgedOrigin)).StatusCode);
        }
        var connections = (await browser.GetFromJsonAsync<AiConnectionSummary[]>("/api/ai/connections"))!;
        Assert.Single(connections);
        Assert.NotNull(connections[0].LastUsedAt);
        Assert.Empty((await otherBrowser.GetFromJsonAsync<AiConnectionSummary[]>("/api/ai/connections"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await otherBrowser.DeleteAsync("/api/ai/connections/" + connections[0].Id)).StatusCode);
        app.Clock.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(HttpStatusCode.Unauthorized, (await remote.PostAsJsonAsync("/mcp", new { })).StatusCode);
        remote.DefaultRequestHeaders.Authorization = null;
        var renewed = await remote.PostAsync("/connect/token", Form(("grant_type", "refresh_token"), ("refresh_token", refresh.GetString()!), ("resource", "https://localhost/mcp")));
        renewed.EnsureSuccessStatusCode();
        var renewedToken = await renewed.Content.ReadFromJsonAsync<JsonElement>();
        refresh = renewedToken.GetProperty("refresh_token");
        remote.DefaultRequestHeaders.Authorization = new("Bearer", renewedToken.GetProperty("access_token").GetString());
        var revoked = await browser.DeleteAsync("/api/ai/connections/" + connections[0].Id);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync("/api/ai/connections/" + connections[0].Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await remote.PostAsJsonAsync("/mcp", new { })).StatusCode);
        remote.DefaultRequestHeaders.Authorization = null;
        var refreshResponse = await remote.PostAsync("/connect/token", Form(("grant_type", "refresh_token"),
            ("client_id", "test-ai"), ("refresh_token", refresh.GetString()!)));
        Assert.Equal(HttpStatusCode.BadRequest, refreshResponse.StatusCode);
        Assert.Contains(measurements, tags => tags.Any(t => t.Key == "outcome" && Equals(t.Value, "success")));
        Assert.Contains(measurements, tags => tags.Any(t => t.Key == "outcome" && Equals(t.Value, "rejected")));
        Assert.All(measurements, tags => Assert.Equal(new[] { "outcome", "tool" }, tags.Select(t => t.Key).Order().ToArray()));
    }

    [PostgreSqlFact]
    public Task Official_sdk_discovers_metadata_and_completes_its_own_oauth_pkce_and_state_flow() => SdkFlowAsync(false, false);

    [PostgreSqlFact]
    public Task Official_sdk_write_grant_updates_the_same_lists_seen_by_the_website() => SdkFlowAsync(true, true);

    [PostgreSqlFact]
    public Task Enabling_write_tools_does_not_upgrade_a_read_only_grant() => SdkFlowAsync(true, false);

    private static async Task SdkFlowAsync(bool enableWrites, bool approveWrites)
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var app = new OAuthFactory(enableWrites);
        using var browser = app.Client(true);
        using var remote = app.Client(false);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AiConnectionService>().EnsureClientsAsync(default);
        (await browser.PostAsJsonAsync("/api/auth/register", new RegisterRequest(Guid.NewGuid() + "@example.test", "Long test passphrase 123!", "SDK owner"))).EnsureSuccessStatusCode();
        var callbacks = 0;
        await using var transport = new HttpClientTransport(new()
        {
            Endpoint = new("https://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = new ClientOAuthOptions
            {
                ClientId = "test-ai", RedirectUri = new("https://client.example/callback"), Scopes = approveWrites ? ["lists.read", "lists.write"] : ["lists.read"],
                AuthorizationCallbackHandler = async (flow, ct) =>
                {
                    callbacks++;
                    var cached = await browser.GetAsync(flow.AuthorizationUri, ct);
                    Assert.Equal(HttpStatusCode.Redirect, cached.StatusCode);
                    var consent = await browser.GetAsync(cached.Headers.Location, ct);
                    var requestUri = QueryHelpers.ParseQuery(consent.Headers.Location!.Query)["request_uri"].ToString();
                    var preview = await browser.PostAsync("/api/ai/oauth/preview", Form(("request_uri", requestUri)), ct);
                    preview.EnsureSuccessStatusCode();
                    var description = await preview.Content.ReadFromJsonAsync<JsonElement>(ct);
                    var approved = await browser.PostAsync("/api/ai/oauth/authorize", Form(("request_uri", requestUri),
                        ("decision", "approve"), ("expected_account_id", description.GetProperty("accountId").GetString()!),
                        ("approved_scopes", approveWrites ? "lists.read lists.write" : "lists.read")), ct);
                    Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
                    var callback = QueryHelpers.ParseQuery(approved.Headers.Location!.Query);
                    return new AuthorizationResult { Code = callback["code"]!, State = callback["state"]!, Iss = callback["iss"] };
                }
            }
        }, remote);
        await using var client = await McpClient.CreateAsync(transport);
        Assert.NotEqual(true, (await client.CallToolAsync("get_my_lists")).IsError);
        Assert.Equal(1, callbacks);
        if (!enableWrites) return;
        var requestId = Guid.NewGuid().ToString();
        var input = new Dictionary<string, object?> { ["requestId"] = requestId, ["name"] = "SDK-created list" };
        var created = await client.CallToolAsync("create_list", input);
        if (!approveWrites)
        {
            Assert.Equal(true, created.IsError);
            Assert.Equal("insufficient_scope", created.StructuredContent!.Value.GetProperty("error").GetString());
            Assert.Single((await browser.GetFromJsonAsync<ShoppingListManagement>("/api/shopping-lists/manage"))!.Lists);
            return;
        }
        Assert.NotEqual(true, created.IsError);
        var list = created.StructuredContent!.Value.GetProperty("listId").GetString()!;
        Assert.True((await client.CallToolAsync("create_list", input)).StructuredContent!.Value.GetProperty("replayed").GetBoolean());
        var selection = await client.CallToolAsync("set_current_list", new Dictionary<string, object?> { ["requestId"] = Guid.NewGuid().ToString(), ["listId"] = list, ["expectedSelectionVersion"] = "0" });
        Assert.NotEqual(true, selection.IsError);
        Assert.Equal(long.Parse(list), (await browser.GetFromJsonAsync<CurrentSessionResponse>("/api/auth/me"))!.CurrentShoppingListId);
        var added = await client.CallToolAsync("add_product_to_list", new Dictionary<string, object?> { ["requestId"] = Guid.NewGuid().ToString(), ["listId"] = list, ["genericName"] = "Unpriced pantry rice", ["quantity"] = 2 });
        Assert.NotEqual(true, added.IsError);
        var item = Assert.Single((await browser.GetFromJsonAsync<ShoppingListItemPage>($"/api/shopping-lists/{list}/items"))!.Items);
        Assert.Equal(2, item.Quantity); Assert.Equal("Unpriced pantry rice", item.Product.Name);
        var updated = await client.CallToolAsync("update_list_item", new Dictionary<string, object?> { ["requestId"] = Guid.NewGuid().ToString(), ["listId"] = list,
            ["itemId"] = item.Id.ToString(), ["expectedUpdatedAt"] = item.UpdatedDate.ToString("O"), ["quantity"] = 3 });
        Assert.NotEqual(true, updated.IsError);
        var timestamp = updated.StructuredContent!.Value.GetProperty("updatedAt").GetString()!;
        var stale = await client.CallToolAsync("update_list_item", new Dictionary<string, object?> { ["requestId"] = Guid.NewGuid().ToString(), ["listId"] = list,
            ["itemId"] = item.Id.ToString(), ["expectedUpdatedAt"] = item.UpdatedDate.ToString("O"), ["quantity"] = 7 });
        Assert.Equal(true, stale.IsError);
        Assert.True(stale.StructuredContent!.Value.GetProperty("refreshRequired").GetBoolean());
        var removed = await client.CallToolAsync("remove_list_item", new Dictionary<string, object?> { ["requestId"] = Guid.NewGuid().ToString(), ["listId"] = list,
            ["itemId"] = item.Id.ToString(), ["expectedUpdatedAt"] = timestamp, ["confirm"] = true });
        Assert.NotEqual(true, removed.IsError);
        Assert.Empty((await browser.GetFromJsonAsync<ShoppingListItemPage>($"/api/shopping-lists/{list}/items"))!.Items);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) => new(fields
        .Append((Key: "client_id", Value: "test-ai")).DistinctBy(f => f.Key).Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private sealed class OAuthFactory(bool enableWrites = false) : WebApplicationFactory<Program>
    {
        public OAuthClock Clock { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in new Dictionary<string, string> { ["Enabled"] = "true", ["Issuer"] = "https://localhost/",
                ["Resource"] = "https://localhost/mcp", ["FrontendOrigin"] = "https://frontend.example", ["DevelopmentEphemeralKeys"] = "true",
                ["Clients:0:ClientId"] = "test-ai", ["Clients:0:DisplayName"] = "Test AI", ["Clients:0:RedirectUris:0"] = "https://client.example/callback" })
                builder.UseSetting("AiIntegration:" + key, value);
            if (enableWrites)
            {
                builder.UseSetting("AiIntegration:AllowedScopes:0", "lists.read");
                builder.UseSetting("AiIntegration:AllowedScopes:1", "products.read");
                builder.UseSetting("AiIntegration:AllowedScopes:2", "lists.write");
            }
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
        public HttpClient Client(bool cookies)
        {
            var client = CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = cookies, AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
            return client;
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); await Serilog.Log.CloseAndFlushAsync(); }
    }
    private sealed class OAuthClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
