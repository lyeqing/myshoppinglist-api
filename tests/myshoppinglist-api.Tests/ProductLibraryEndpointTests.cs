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
public class ProductLibraryEndpointTests
{
    [PostgreSqlFact]
    public async Task ProductLibrary_endpoints_require_session_request_protection_ownership_and_valid_inputs()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(f, false);
        await using var app = new ApiFactory(f);
        using var client = await app.ClientAsync(); using var anonymous = app.CreateClient();
        const string library = "/api/account/products";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(library)).StatusCode);
        var response = await client.GetAsync(library);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var page = (await response.Content.ReadFromJsonAsync<ProductLibraryPage>())!;
        var productId = Assert.Single(page.Items).Product.Id;
        var path = $"{library}/{productId}/preferences";
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, new ProductPreferenceUpdate(f.Scope.UserId, "favorite", true))).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, new ProductPreferenceUpdate(-1, "favorite", true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, new ProductPreferenceUpdate(f.Scope.UserId, "favorite", true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(library + "?view=history&view=favorites")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(library + "?offset=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(library + $"/preferences?productIds={productId}&expectedAccountId=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(library + $"/preferences?productIds={productId}&expectedAccountId={f.Scope.UserId}")).StatusCode);
        var target = await f.Service.CreateAsync(f.Scope.UserId, new("Saved products"), null, default);
        var added = (await (await client.PostAsJsonAsync($"/api/shopping-lists/{target.Id}/products/{productId}", new ProductLibraryAddRequest(f.Scope.UserId))).Content.ReadFromJsonAsync<ProductLibraryAddResponse>())!;
        Assert.False(added.AlreadyOnList);
    }

    private sealed class ApiFactory(ListFixture fixture) : WebApplicationFactory<Program>
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
            fixture.Scope.Db.UserSessions.Add(new() { UserAccountId = fixture.Scope.UserId, TokenHash = SessionToken.Hash(token), CreatedDate = fixture.Scope.Clock.Now.UtcDateTime, ExpiresDate = fixture.Scope.Clock.Now.UtcDateTime.AddHours(3) });
            await fixture.Scope.Db.SaveChangesAsync(); fixture.Scope.Db.ChangeTracker.Clear();
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false });
            client.DefaultRequestHeaders.Add("Cookie", "myshoppinglist_session=" + token);
            client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
            return client;
        }
    }
}
