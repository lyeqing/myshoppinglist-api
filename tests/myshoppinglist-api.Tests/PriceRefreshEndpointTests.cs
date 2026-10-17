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
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class PriceRefreshEndpointTests
{
    [PostgreSqlFact]
    public async Task Refresh_endpoints_enforce_authentication_ownership_validation_and_return_counts()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        await f.Scope.Db.ShopProducts.Where(m => m.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProductUrl, "https://www.coles.com.au/product/" + Random.Shared.NextInt64(100000000, 900000000)));
        await using var app = new ListApiFactory(f);
        using var client = await app.ClientAsync();
        using var anonymous = app.CreateClient();
        var path = $"/api/shopping-lists/{f.Scope.ListId}/items/{f.ItemId}/refresh-prices";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/shopping-lists/{other.Scope.ListId}/items/{other.ItemId}/refresh-prices", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/shopping-lists/{f.Scope.ListId}/items/{other.ItemId}/refresh-prices", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/shopping-lists/{f.Scope.ListId}/items/0/refresh-prices", new { })).StatusCode);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new { })).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        client.DefaultRequestHeaders.Add("X-Client-Timezone", "Australia/Adelaide");
        var response = await client.PostAsJsonAsync(path, new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<PriceRefreshResult>())!;
        Assert.Equal(0, result.Queued);
        Assert.Equal(1, result.Current);
        Assert.Equal(0, result.CheckedUnavailable);
        using var listResponse = await client.PostAsJsonAsync($"/api/shopping-lists/{f.Scope.ListId}/refresh-prices", new { });
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(result, await listResponse.Content.ReadFromJsonAsync<PriceRefreshResult>());
    }
    [PostgreSqlFact]
    public async Task Product_and_list_refresh_separate_current_and_checked_unavailable_prices()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        var now = f.Scope.Clock.Now.UtcDateTime;
        now = now.AddTicks(-(now.Ticks % 10)); // PostgreSQL stores microsecond precision.
        var item = await f.ItemAsync();
        await f.Scope.Db.ShopProducts.Where(m => m.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProductUrl, "https://www.coles.com.au/product/123456789"));
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, now).SetProperty(p => p.PriceScope, PriceScope.Catalogue));
        await using var app = new ListApiFactory(f);
        using var client = await app.ClientAsync();
        client.DefaultRequestHeaders.Add("X-Client-Timezone", "UTC");
        foreach (var path in new[] { $"/api/shopping-lists/{f.Scope.ListId}/items/{f.ItemId}/refresh-prices",
            $"/api/shopping-lists/{f.Scope.ListId}/refresh-prices" })
        {
            using var response = await client.PostAsJsonAsync(path, new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal(new PriceRefreshResult(0, 0, 0, 0, 1), await response.Content.ReadFromJsonAsync<PriceRefreshResult>());
        }
        var mapping = new ShopProduct { ProductId = item.Product.Id, ShopId = 2, NameAtShop = item.Product.Name,
            ProductUrl = "https://www.woolworths.com.au/shop/productdetails/123456789", MatchType = Models.MatchType.Exact,
            FirstFoundDate = now, LastFoundDate = now };
        try
        {
            f.Scope.Db.ShopProductPrices.Add(new() { ShopProduct = mapping, Price = 20, PriceScope = PriceScope.National,
                CheckedDate = now, CreatedDate = now, UpdatedDate = now });
            await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
            using var mixed = await client.PostAsJsonAsync($"/api/shopping-lists/{f.Scope.ListId}/refresh-prices", new { });
            Assert.Equal(new PriceRefreshResult(0, 0, 1, 0, 1), await mixed.Content.ReadFromJsonAsync<PriceRefreshResult>());
            Assert.False(await f.Scope.Db.ColesExtensionTasks.AnyAsync());
            var observations = await f.Scope.Db.ShopProductPrices.AsNoTracking().OrderBy(p => p.Price).ToArrayAsync();
            Assert.Equal(new[] { 20m, 23m }, observations.Select(p => p.Price));
            Assert.All(observations, p => Assert.Equal(now, p.CheckedDate));
        }
        finally
        {
            f.Scope.Db.ChangeTracker.Clear();
            await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProductId == mapping.Id).ExecuteDeleteAsync();
            await f.Scope.Db.ShopProducts.Where(m => m.Id == mapping.Id).ExecuteDeleteAsync();
        }
    }

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
