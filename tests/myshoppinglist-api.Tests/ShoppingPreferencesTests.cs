using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ShoppingPreferencesTests
{
    private static ShoppingPreferencesService Preferences(ListFixture f) => new(f.Scope.Db, f.Scope.Clock, new RetailerProviderRegistry([]));
    private static async Task Choose(ListFixture f, params long[] ids)
    {
        var view = await Preferences(f).ReadAsync(f.Scope.UserId, default);
        await Preferences(f).SaveAsync(f.Scope.UserId, new(f.Scope.UserId, true, true, false,
            ids.Select(id => new PreferredShopSelection(id, true)).ToArray(), view.UpdatedDate), default);
    }

    [PostgreSqlFact]
    public async Task Defaults_persist_independent_flags_and_reject_invalid_or_stale_settings()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        var service = Preferences(f);
        var view = await service.ReadAsync(f.Scope.UserId, default);
        Assert.False(view.CompareOnlyPreferredShops); Assert.True(view.IncludeDifferentPackSizes); Assert.False(view.IncludeDifferentBrands); Assert.Empty(view.Shops);
        Assert.Equal(5, (await ShoppingPreferencesService.AllowedShops(f.Scope.Db, f.Scope.UserId).ToArrayAsync()).Length);
        foreach (var pack in new[] { true, false }) foreach (var brands in new[] { true, false })
        {
            var before = view;
            view = await service.SaveAsync(f.Scope.UserId, new(f.Scope.UserId, true, pack, brands, [new(2, true), new(1, false)], before.UpdatedDate), default);
            Assert.Equal(pack, view.IncludeDifferentPackSizes); Assert.Equal(brands, view.IncludeDifferentBrands);
            Assert.Equal(new long[] { 2, 1 }, view.Shops.Select(s => s.ShopId));
            Assert.Equal(new long[] { 2 }, await ShoppingPreferencesService.AllowedShops(f.Scope.Db, f.Scope.UserId).Select(s => s.Id).ToArrayAsync());
            Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => service.SaveAsync(f.Scope.UserId, new(f.Scope.UserId, true, pack, brands, [new(1, true)], before.UpdatedDate), default))).Status);
        }
        var unchanged = await Preferences(other).ReadAsync(other.Scope.UserId, default);
        Assert.False(unchanged.CompareOnlyPreferredShops); Assert.Empty(unchanged.Shops);
        foreach (var shops in new PreferredShopSelection[][] { [], [new(1, false)], [new(999999, true)], [new(1, true), new(1, true)] })
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => service.SaveAsync(f.Scope.UserId, new(f.Scope.UserId, true, true, false, shops, view.UpdatedDate), default))).Status);
        Assert.Equal(JsonSerializer.Serialize(view), JsonSerializer.Serialize(await service.ReadAsync(f.Scope.UserId, default)));
    }

    [PostgreSqlFact]
    public async Task Preferences_filter_planning_in_store_manual_refresh_and_shared_automatic_refresh()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        var url = "https://www.coles.com.au/product/" + Random.Shared.NextInt64(100000000, 900000000);
        await f.Scope.Db.ShopProducts.Where(m => m.ShopProductCode == f.Scope.Code).ExecuteUpdateAsync(s => s.SetProperty(m => m.ProductUrl, url));
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code).ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddDays(-8)));
        await Choose(f, 2);
        var plan = (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!;
        Assert.Single(plan.Items); Assert.Empty(plan.Items[0].Prices); Assert.Equal(0, plan.Lowest.PricedCount); Assert.True(plan.StoreContext!.PreferredOnly); Assert.Equal(["Woolworths"], plan.StoreContext.ShopNames);
        var detail = (await new InStoreShoppingService(f.Scope.Db, f.Scope.Clock, new()).DetailAsync(f.Scope.UserId, f.Scope.ListId, default))!;
        Assert.Single(detail.Items); Assert.Null(Assert.Single(detail.Items[0].Prices).Price); Assert.Equal("Match not checked", detail.Items[0].Prices[0].Status); Assert.Equal(2, Assert.Single(detail.Retailers).Id);
        var refresh = new PriceRefreshService(f.Scope.Db, f.Scope.Clock, new(), Options.Create(new PriceRefreshOptions()));
        Assert.Equal(0, (await refresh.RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "UTC", default))!.Queued);
        Assert.Equal(0, await refresh.ScanAsync(default));
        var item = await f.ItemAsync();
        f.Scope.Db.ShoppingListProducts.Add(new() { ShoppingListId = other.Scope.ListId, ProductId = item.Product.Id, Quantity = 1, AddedDate = f.Scope.Clock.Now.UtcDateTime, UpdatedDate = f.Scope.Clock.Now.UtcDateTime });
        await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
        Assert.Equal(1, await refresh.ScanAsync(default));
        Assert.Equal(0, (await refresh.RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, null, "UTC", default))!.AlreadyRunning);
        await Choose(other, 2);
        Assert.Equal(0, await refresh.ScanAsync(default));
        var restored = await Preferences(f).ReadAsync(f.Scope.UserId, default);
        await Preferences(f).SaveAsync(f.Scope.UserId, new(f.Scope.UserId, false, true, false, restored.Shops, restored.UpdatedDate), default);
        Assert.Equal(1, (await refresh.RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "UTC", default))!.AlreadyRunning);
    }

    [PostgreSqlFact]
    public async Task Excluded_retailers_are_never_searched_and_historical_failures_do_not_block_completion()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ImportFixture.CreateAsync();
        var claim = await RetailerComparisonTests.PrepareAsync(f);
        var account = await f.Scope.Db.UserAccounts.SingleAsync(a => a.Id == f.Scope.UserId);
        account.CompareOnlyPreferredShops = true;
        f.Scope.Db.UserPreferredShops.Add(new() { UserAccountId = account.Id, ShopId = 1, IsEnabled = true });
        await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
        var provider = new RetailerComparisonTests.ComparisonProvider(RetailerComparisonTests.Other(f));
        await RetailerComparisonTests.RunAsync(f, claim, provider);
        Assert.Equal(0, provider.Searches + provider.Fetches);
        Assert.Equal(ProductImportJobStatus.Completed, (await f.ReadAsync()).Status);
        var view = await new ProductImportStatusService(f.Scope.Db, f.Scope.Clock).ReadAsync(f.Scope.UserId, claim.JobId, default);
        Assert.NotNull(view!.Product); Assert.All(view.Retailers, r => Assert.Equal(1, r.ShopId));
        var retry = await new ShoppingListComparisonRetryService(f.Scope.Db, f.Scope.Clock).ReadAsync(f.Scope.UserId, f.Scope.ListId, default);
        Assert.False(Assert.Single(retry).Value.CanRetry);
    }

    [PostgreSqlFact]
    public async Task Settings_api_requires_session_protection_and_filters_new_import_jobs()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        await using var app = new ListApiFactory(f);
        using var client = await app.ClientAsync(); using var anonymous = app.CreateClient();
        const string path = "/api/account/shopping-preferences";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var response = await client.GetAsync(path); Assert.True(response.Headers.CacheControl!.NoStore);
        var view = (await response.Content.ReadFromJsonAsync<ShoppingPreferencesResponse>())!;
        Assert.Contains(view.AvailableShops, s => s.Code == "coles" && s.ComparisonSupported);
        Assert.Contains(view.AvailableShops, s => s.Code == "aldi" && !s.ComparisonSupported);
        var update = new ShoppingPreferencesUpdate(f.Scope.UserId, true, true, false, [new(1, true)], view.UpdatedDate);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, update)).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, update with { ExpectedAccountId = other.Scope.UserId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, update)).StatusCode);
        var submitted = await client.PostAsJsonAsync($"/api/shopping-lists/{f.Scope.ListId}/products/url", new { url = "https://www.coles.com.au/product/" + Random.Shared.NextInt64(100000000, 900000000), quantity = 1 });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var accepted = await submitted.Content.ReadFromJsonAsync<JsonElement>();
        var acceptedId = accepted.GetProperty("jobId").GetInt64();
        var retailerIds = await f.Scope.Db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == acceptedId).Select(r => r.ShopId).ToArrayAsync();
        Assert.Equal(new long[] { 1 }, retailerIds);
        Assert.Empty((await Preferences(other).ReadAsync(other.Scope.UserId, default)).Shops);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/account/" + other.Scope.UserId + "/shopping-preferences")).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, update)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Trial_conversion_preserves_store_choices_and_comparison_defaults()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await Choose(f, 2, 3);
        await using var app = new ListApiFactory(f); using var client = await app.ClientAsync();
        var registered = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(Guid.NewGuid() + "@example.test", "Abcdef1!", "Shopper"));
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var session = (await registered.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
        Assert.Equal(f.Scope.UserId, session.Account.Id); Assert.False(session.Account.IsTrial);
        var prefs = await Preferences(f).ReadAsync(f.Scope.UserId, default);
        Assert.True(prefs.CompareOnlyPreferredShops); Assert.Equal([2L, 3L], prefs.Shops.Select(s => s.ShopId)); Assert.True(prefs.IncludeDifferentPackSizes); Assert.False(prefs.IncludeDifferentBrands);
    }

    [PostgreSqlFact]
    public async Task Customer_extension_skips_excluded_store_and_rechecks_choices_before_accepting_comparison()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await using var app = new ListApiFactory(f);
        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<UserExtensionImportService>();
        string code = Random.Shared.NextInt64(100000000, 900000000).ToString();
        string url = "https://www.coles.com.au/product/" + code;
        var evidence = new ColesEvidence(null, [JsonSerializer.Serialize(new Dictionary<string,object?> {
            ["@type"]="Product",["sku"]=code,["name"]=f.Scope.Source.Identity.Name,["gtin"]=f.Scope.Source.Identity.GTIN,
            ["brand"]=new { name=f.Scope.Source.Identity.Brand },["offers"]=new { price=23,priceCurrency="AUD",url } })]);
        try
        {
            var work = await service.StartAsync(f.Scope.UserId, new(f.Scope.ListId, url, 1, evidence, Guid.NewGuid()), default);
            Assert.Equal("search", work.Stage); Assert.NotNull(work.Url);
            await Choose(f, 1);
            var observations = await f.Scope.Db.ContributionObservations.CountAsync();
            var completed = await service.ResultAsync(f.Scope.UserId, work.JobId, new(work.StepToken, work.Url!, false, null, null, false, "network_error"), default);
            Assert.Equal("Completed", completed.Status); Assert.Null(completed.Url);
            Assert.Equal(observations, await f.Scope.Db.ContributionObservations.CountAsync());
            var next = await service.StartAsync(f.Scope.UserId, new(f.Scope.ListId, url, 2, evidence, Guid.NewGuid()), default);
            Assert.Equal("Completed", next.Status); Assert.Null(next.Url);
        }
        finally
        {
            var extraIds = await f.Scope.Db.ShopProducts.Where(m => m.ShopProductCode == code).Select(m => m.Id).ToArrayAsync();
            await f.Scope.Db.ProductImportRetailerResults.Where(r => r.ShopProductId != null && extraIds.Contains(r.ShopProductId.Value)).ExecuteDeleteAsync();
            await f.Scope.Db.ShopProductPrices.Where(p => extraIds.Contains(p.ShopProductId)).ExecuteDeleteAsync();
            await f.Scope.Db.ShopProductPriceHistory.Where(p => extraIds.Contains(p.ShopProductId)).ExecuteDeleteAsync();
            await f.Scope.Db.ShopProducts.Where(m => extraIds.Contains(m.Id)).ExecuteDeleteAsync();
        }

    }

    [PostgreSqlFact]
    public async Task Newly_selected_stores_can_be_compared_using_the_existing_explicit_retry()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ImportFixture.CreateAsync();
        var claim = await RetailerComparisonTests.PrepareAsync(f);
        var prefs = new ShoppingPreferencesService(f.Scope.Db, f.Scope.Clock, new RetailerProviderRegistry([]));
        var current = await prefs.ReadAsync(f.Scope.UserId, default);
        await prefs.SaveAsync(f.Scope.UserId, new(f.Scope.UserId, true, true, false, [new(1, true)], current.UpdatedDate), default);
        var provider = new RetailerComparisonTests.ComparisonProvider(RetailerComparisonTests.Other(f));
        await RetailerComparisonTests.RunAsync(f, claim, provider);
        Assert.Equal(0, provider.Searches);
        current = await prefs.ReadAsync(f.Scope.UserId, default);
        await prefs.SaveAsync(f.Scope.UserId, new(f.Scope.UserId, true, true, false, [new(2, true)], current.UpdatedDate), default);
        var retryService = new ShoppingListComparisonRetryService(f.Scope.Db, f.Scope.Clock);
        var views = await retryService.ReadAsync(f.Scope.UserId, f.Scope.ListId, default);
        var view = Assert.Single(views).Value;
        Assert.True(view.CanRetry); Assert.Equal("comparison_not_checked", view.ErrorCode);
        await retryService.RetryAsync(f.Scope.UserId, f.Scope.ListId, views.Single().Key, default);
        claim = (await f.Jobs().ClaimNextAsync(default))!;
        await RetailerComparisonTests.RunAsync(f, claim, provider);
        Assert.Equal(1, provider.Searches);
        Assert.Equal(ProductImportJobStatus.Completed, (await f.ReadAsync()).Status);
        Assert.Equal(19m, await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopId == 2 && p.ShopProduct.ShopProductCode == f.Scope.Code).Select(p => p.Price).SingleAsync());
    }

    [PostgreSqlFact]
    public async Task Default_comparison_behaviour_remains_compatible()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await new RetailerComparisonTests().Restart_after_comparison_commit_preserves_result_without_fetching_again();
        await new RetailerComparisonTests().Extension_wait_does_not_leave_later_retailers_unchecked();
    }

    [PostgreSqlFact]
    public async Task Additive_migration_preserves_existing_products_and_accounts()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        var db = f.Scope.Db;
        var item = await f.ItemAsync();
        // This scope's search path contains only its temporary schema, never application tables.
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE "UserPreferredShops";
            ALTER TABLE "UserAccounts" DROP COLUMN "CompareOnlyPreferredShops", DROP COLUMN "IncludeDifferentPackSizes", DROP COLUMN "IncludeDifferentBrands";
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
            """);
        var migrations = db.Database.GetMigrations().ToArray();
        foreach (var migration in migrations[..^1])
            await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({migration}, {"10.0.11"})""");
        await db.Database.MigrateAsync();
        var account = await db.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == f.Scope.UserId);
        Assert.False(account.CompareOnlyPreferredShops); Assert.True(account.IncludeDifferentPackSizes); Assert.False(account.IncludeDifferentBrands);
        Assert.Equal(item.Id, (await f.ItemAsync()).Id); Assert.Equal(item.Product.Id, (await f.ItemAsync()).Product.Id);
        Assert.Empty(await db.UserPreferredShops.ToListAsync());
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
