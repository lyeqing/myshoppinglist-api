using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ShoppingListEndpointTests
{
    [PostgreSqlFact]
    public async Task List_management_routes_enforce_tier_ownership_versions_and_request_protection()
    {
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(f, false);
        await using var app = new ListApiFactory(f);
        using var client = await app.ClientAsync();
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/shopping-lists/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/shopping-lists/history")).StatusCode);
        var management = (await client.GetFromJsonAsync<ShoppingListManagement>("/api/shopping-lists/manage"))!;
        var list = Assert.Single(management.Lists);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/shopping-lists/{list.Id}/archive", new ShoppingListActionRequest(list.UpdatedDate))).StatusCode);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/shopping-lists", new ShoppingListCreateRequest("New"))).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/shopping-lists", new ShoppingListCreateRequest("New"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/shopping-lists", new ShoppingListCreateRequest("Too many"))).StatusCode);
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsPaid, true));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/shopping-lists/{other.Scope.ListId}/archive", new ShoppingListActionRequest(list.UpdatedDate))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/shopping-lists/{list.Id}/archive", new ShoppingListActionRequest(list.UpdatedDate.AddSeconds(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/shopping-lists/{list.Id}/archive", new ShoppingListActionRequest(list.UpdatedDate))).StatusCode);
        var archived = Assert.Single((await client.GetFromJsonAsync<ShoppingListSummary[]>("/api/shopping-lists/history"))!);
        Assert.Single((await client.GetFromJsonAsync<ShoppingListHistory>($"/api/shopping-lists/{list.Id}/history"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/shopping-lists/{other.Scope.ListId}/history")).StatusCode);
        var deleted = await client.SendAsync(new(HttpMethod.Delete, $"/api/shopping-lists/{list.Id}") { Content = JsonContent.Create(new ShoppingListActionRequest(archived.UpdatedDate)) });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ShoppingListSummary[]>("/api/shopping-lists/history"))!);
    }
    [PostgreSqlFact]
    public async Task Planning_and_delete_routes_require_ownership_session_and_request_protection()
    {
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        await using var app = new ListApiFactory(f);
        using var client = await app.ClientAsync();
        using var anonymous = app.CreateClient();
        var path = $"/api/shopping-lists/{f.Scope.ListId}/plan";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/shopping-lists/{other.Scope.ListId}/plan")).StatusCode);
        var response = await client.GetAsync(path);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Single((await response.Content.ReadFromJsonAsync<ShoppingListPlan>())!.Items);
        var refresh = $"/api/shopping-lists/{f.Scope.ListId}/refresh-prices";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(refresh, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/shopping-lists/{other.Scope.ListId}/refresh-prices", new { })).StatusCode);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(refresh, new { })).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        var item = await f.ItemAsync();
        async Task<HttpResponseMessage> Delete(HttpClient http, string route, DateTime version) => await http.SendAsync(new(HttpMethod.Delete, route)
        { Content = JsonContent.Create(new ShoppingListItemDeleteRequest(version)) });
        var route = Route(f) + $"/{item.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(anonymous, route, item.UpdatedDate)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(client, Route(other) + $"/{other.ItemId}", item.UpdatedDate)).StatusCode);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await Delete(client, route, item.UpdatedDate)).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        Assert.Equal(HttpStatusCode.Conflict, (await Delete(client, route, item.UpdatedDate.AddSeconds(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, route, item.UpdatedDate)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ShoppingListPlan>(path))!.Items);
    }

    [PostgreSqlFact]
    public async Task Owner_can_read_update_hide_restore_and_detect_stale_edits()
    {
        await using var f = await ListFixture.CreateAsync();
        await using var app = new ListApiFactory(f);
        using var client = await app.ClientAsync();
        var response = await client.GetAsync(Route(f));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<ShoppingListItemPage>())!.Items);
        await f.AddItemAsync();
        var request = new ShoppingListItemUpdateRequest(5, "Milk first", true, true, item.UpdatedDate);
        var edited = await client.PutAsJsonAsync(Route(f) + $"/{item.Id}", request);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode); Assert.True(edited.Headers.CacheControl!.NoStore);
        var saved = (await edited.Content.ReadFromJsonAsync<ShoppingListItemResponse>())!;
        Assert.Equal(5, saved.Quantity); Assert.NotNull(saved.PurchasedDate);
        Assert.Single((await client.GetFromJsonAsync<ShoppingListItemPage>(Route(f)))!.Items);
        Assert.Equal(2, (await client.GetFromJsonAsync<ShoppingListItemPage>(Route(f) + "?includeHidden=true"))!.Items.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(Route(f) + $"/{item.Id}", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Route(f) + $"/{item.Id}", request with
        { IsHidden = false, IsPurchased = false, Notes = null, ExpectedUpdatedDate = saved.UpdatedDate })).StatusCode);
        Assert.Null((await client.GetFromJsonAsync<ShoppingListItemPage>(Route(f)))!.Items[0].PurchasedDate);
    }

    [PostgreSqlFact]
    public async Task Authentication_ownership_and_browser_request_protection_apply_to_edits()
    {
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        await using var app = new ListApiFactory(f);
        using var client = await app.ClientAsync();
        using var anonymous = app.CreateClient();
        var item = await f.ItemAsync();
        var request = new ShoppingListItemUpdateRequest(3, null, false, false, item.UpdatedDate);
        var route = Route(f) + $"/{f.ItemId}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route(f))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(route, request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Route(other))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Route(other) + $"/{other.ItemId}", request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Route(f) + $"/{other.ItemId}", request)).StatusCode);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(route, request)).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(route, request)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        f.Scope.Clock.Now += TimeSpan.FromHours(4);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route(f))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(route, request)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Invalid_json_missing_fields_and_validation_errors_do_not_edit_the_item()
    {
        await using var f = await ListFixture.CreateAsync();
        await using var app = new ListApiFactory(f);
        using var client = await app.ClientAsync();
        var item = await f.ItemAsync();
        foreach (var query in new[] { "?pageSize=0", "?pageSize=51", "?beforeId=0", "?includeHidden=invalid" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route(f) + query)).StatusCode);
        var route = Route(f) + $"/{f.ItemId}";
        foreach (var json in new[] { "null", "{", "{}", "{\"quantity\":1}", "{\"quantity\":1.5}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(route, new StringContent(json, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        var request = new ShoppingListItemUpdateRequest(0, null, false, false, item.UpdatedDate);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(route, request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(route, request with { Quantity = 1, Notes = new string('a', 4001) })).StatusCode);
        Assert.Equal(item, await f.ItemAsync());
    }

    private static string Route(ListFixture f) => $"/api/shopping-lists/{f.Scope.ListId}/items";
    private sealed class ListApiFactory(ListFixture fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(fixture.Scope.Clock);
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
        public async Task<HttpClient> ClientAsync()
        {
            var token = SessionToken.Create();
            fixture.Scope.Db.UserSessions.Add(new()
            {
                UserAccountId = fixture.Scope.UserId,
                TokenHash = SessionToken.Hash(token),
                CreatedDate = fixture.Scope.Clock.Now.UtcDateTime,
                ExpiresDate = fixture.Scope.Clock.Now.UtcDateTime.AddHours(3)
            });
            await fixture.Scope.Db.SaveChangesAsync(); fixture.Scope.Db.ChangeTracker.Clear();
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
            client.DefaultRequestHeaders.Add("Cookie", "myshoppinglist_session=" + token);
            client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
            return client;
        }
    }
}
