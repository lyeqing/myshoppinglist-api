using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class PackDiscoveryTests
{
    private static PackDiscoveryService Jobs(ListFixture f) => new(f.Scope.Db, f.Scope.Clock,
        Options.Create(new ProductImportOptions { Enabled = true }), Options.Create(new PriceRefreshOptions()));
    private static PackDiscoveryPersistenceService Store(ListFixture f)
    {
        var normalisation = new ProductNormalisationService(); var urls = new ProductUrlValidator(new RetailerCatalog());
        return new(f.Scope.Db, Jobs(f), new(f.Scope.Db, normalisation, new(normalisation)), new(f.Scope.Db),
            new(f.Scope.Db, Options.Create(new PriceOptions()), f.Scope.Clock, urls), urls, f.Scope.Clock);
    }
    private static async Task<Product> Seed(ListFixture f)
    {
        var id = (await f.ItemAsync()).Product.Id;
        var p = await f.Scope.Db.Products.SingleAsync(p => p.Id == id);
        p.Name = "Acme Original Coffee 500g"; p.Brand = "Acme"; p.Variant = null; p.PackQuantity = 1; p.PackSize = 500; p.PackUnit = "g";
        var account = await f.Scope.Db.UserAccounts.SingleAsync(a => a.Id == f.Scope.UserId);
        account.CompareOnlyPreferredShops = true;
        f.Scope.Db.UserPreferredShops.Add(new() { UserAccountId = account.Id, ShopId = 2, IsEnabled = true });
        await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear(); return p;
    }
    private static ExtractedShopProduct Candidate(ListFixture f, string code = "987654", decimal size = 1000) => new()
    {
        ShopCode = "woolworths",
        ProductUrl = new("https://www.woolworths.com.au/shop/productdetails/" + code),
        ShopProductCode = code,
        Identity = new() { Name = $"Acme Original Coffee {size}g", Brand = "Acme", PackQuantity = 1, PackSize = size, PackUnit = "g" },
        CheckedDate = f.Scope.Clock.Now.AddSeconds(-1),
        SourceType = SourceType.StructuredData,
        Offer = new ProviderResult<ShopProductOffer>.Success(new()
        {
            ShopCode = "woolworths",
            Price = 13,
            Currency = "AUD",
            InStock = true,
            SourceUrl = new("https://www.woolworths.com.au/shop/productdetails/" + code),
            CheckedDate = f.Scope.Clock.Now.AddSeconds(-1),
            PriceScope = PriceScope.Online,
            SourceType = SourceType.StructuredData
        })
    };
    private static async Task<PackDiscoveryClaim> Queue(ListFixture f)
    {
        Assert.Equal(1, (await Jobs(f).RequestAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, new(f.Scope.UserId), default)).Queued);
        return (await Jobs(f).ClaimAsync(default))!;
    }
    private sealed class Provider(Func<string, Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>>> action) : IShopProductProvider, IPackSizeSearchProvider
    {
        public int Calls; public string? Query; public string ShopCode => "woolworths";
        public Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchPackSizesAsync(string query, CancellationToken token) { Calls++; Query = query; return action(query); }
        public Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(Uri url, CancellationToken token) => throw new InvalidOperationException();
        public Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(ProductIdentity product, ShopLocationContext? location, CancellationToken token) => throw new InvalidOperationException();
        public Task<ProviderResult<ShopProductOffer>> GetOfferAsync(ShopProductSearchResult product, ShopLocationContext? location, CancellationToken token) => throw new InvalidOperationException();
    }
    private static Task Run(ListFixture f, PackDiscoveryClaim claim, Provider provider) => new PackDiscoveryProcessor(f.Scope.Db, Jobs(f), new RetailerProviderRegistry([provider]), Store(f)).RunAsync(claim, default);
    private static ProviderResult<IReadOnlyList<ShopProductSearchResult>> Results(params ExtractedShopProduct[] candidates) => new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success(candidates.Select(c => new ShopProductSearchResult(c)).ToArray());

    [PostgreSqlFact]
    public async Task Requests_are_owned_bounded_deduplicated_and_respect_preferences_and_remaining_items()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var f = await ListFixture.CreateAsync();
        await Seed(f); var jobs = Jobs(f);
        Assert.Empty((await jobs.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, default)).Jobs);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => jobs.ReadAsync(-1, f.Scope.ListId, null, default))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => jobs.RequestAsync(f.Scope.UserId, f.Scope.ListId, null, new(-1), default))).Status);
        Assert.Equal(1, (await jobs.RequestAsync(f.Scope.UserId, f.Scope.ListId, null, new(f.Scope.UserId), default)).Queued);
        Assert.Equal(1, (await jobs.RequestAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, new(f.Scope.UserId), default)).AlreadyRunning);
        Assert.Equal(1, await f.Scope.Db.PackDiscoveryJobs.CountAsync());
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IncludeDifferentPackSizes, false));
        Assert.False((await jobs.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, default)).Enabled);
        Assert.Null(await jobs.ClaimAsync(default));
        Assert.Equal("Cancelled", (await f.Scope.Db.PackDiscoveryJobs.AsNoTracking().SingleAsync()).Status);
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IncludeDifferentPackSizes, true));
        await f.Scope.Db.ShoppingListProducts.Where(i => i.Id == f.ItemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsPurchased, true));
        Assert.False((await jobs.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, default)).CanStart);
        Assert.Equal(0, (await jobs.RequestAsync(f.Scope.UserId, f.Scope.ListId, null, new(f.Scope.UserId), default)).Queued);
    }

    [PostgreSqlFact]
    public async Task Discovery_saves_distinct_package_and_price_provenance_without_changing_list_or_exact_mapping()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var f = await ListFixture.CreateAsync();
        var original = await Seed(f); var before = await f.ItemAsync(); var claim = await Queue(f);
        var candidate = Candidate(f);
        var provider = new Provider(_ => Task.FromResult(Results(candidate, candidate with { ProductUrl = new(candidate.ProductUrl.AbsoluteUri + "?from=search") }, candidate,
            candidate with { ProductUrl = new("https://www.woolworths.com.au/shop/productdetails/123456"), Identity = candidate.Identity with { Name = "Acme Decaf Coffee 1000g" } })));
        await Run(f, claim, provider);
        Assert.Equal(1, provider.Calls); Assert.DoesNotContain("500", provider.Query!); Assert.Contains("original", provider.Query!);
        var progress = Assert.Single((await Jobs(f).ReadAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, default)).Jobs);
        Assert.Equal("Completed", progress.Status); Assert.Equal(1, progress.FoundCount);
        var saved = await f.Scope.Db.ShopProducts.AsNoTracking().SingleAsync(m => m.ShopProductCode == "987654");
        Assert.NotEqual(original.Id, saved.ProductId);
        var price = await f.Scope.Db.ShopProductPrices.AsNoTracking().SingleAsync(p => p.ShopProductId == saved.Id);
        Assert.Equal(13, price.Price); Assert.Equal(candidate.CheckedDate.UtcDateTime.Ticks / 10, price.CheckedDate.Ticks / 10); Assert.NotNull(price.ContributionObservationId);
        Assert.Equal(before, await f.ItemAsync());
        var plan = (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!;
        Assert.Single(Assert.Single(plan.Items).PackAlternatives!); Assert.Equal(0, plan.Lowest.Subtotal);
        Assert.Equal(1, (await Jobs(f).RequestAsync(f.Scope.UserId, f.Scope.ListId, null, new(f.Scope.UserId), default)).Recent);
    }

    [PostgreSqlFact]
    public async Task Changing_preferences_during_lookup_prevents_catalogue_writes()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var f = await ListFixture.CreateAsync();
        await Seed(f); var claim = await Queue(f);
        var provider = new Provider(async _ =>
        {
            await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IncludeDifferentPackSizes, false));
            return Results(Candidate(f));
        });
        await Run(f, claim, provider);
        Assert.Equal("Cancelled", (await f.Scope.Db.PackDiscoveryJobs.AsNoTracking().SingleAsync()).Status);
        Assert.False(await f.Scope.Db.ShopProducts.AnyAsync(m => m.ShopProductCode == "987654"));
    }

    [PostgreSqlFact]
    public async Task Limits_and_partial_failures_keep_verified_results_and_existing_prices()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var f = await ListFixture.CreateAsync();
        await Seed(f); var claim = await Queue(f);
        var candidates = Enumerable.Range(1, 10).Select(i => Candidate(f, (987650 + i).ToString(), 500 + i * 100)).ToArray();
        candidates[1] = candidates[1] with { Offer = new ProviderResult<ShopProductOffer>.Failure(new(ProviderFailureKind.ParseError, "missing_price", "No price")) };
        await Run(f, claim, new(_ => Task.FromResult(Results(candidates))));
        var job = await f.Scope.Db.PackDiscoveryJobs.AsNoTracking().SingleAsync();
        Assert.Equal("Failed", job.Status); Assert.Equal(7, job.FoundCount); Assert.Equal("some_candidates_unavailable", job.ErrorCode);
        Assert.Equal(7, await f.Scope.Db.ShopProducts.CountAsync(m => m.ShopId == 2));
        Assert.False(await f.Scope.Db.ShopProducts.AnyAsync(m => m.ShopProductCode == "987659"));
        Assert.Equal(1, await f.Scope.Db.ShoppingListProducts.CountAsync(i => i.ShoppingListId == f.Scope.ListId));
        Assert.NotEmpty((await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!.Items[0].PackAlternatives!);
    }

    [PostgreSqlFact]
    public async Task Interrupted_claims_are_fenced_and_waiting_does_not_consume_failure_attempts()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var f = await ListFixture.CreateAsync();
        await Seed(f); var old = await Queue(f); var jobs = Jobs(f);
        f.Scope.Clock.Now += TimeSpan.FromMinutes(4);
        var current = (await jobs.ClaimAsync(default))!; Assert.NotEqual(old.Token, current.Token);
        Assert.False(await Store(f).SaveAsync(old, Candidate(f), default));
        await jobs.FinishAsync(old, "Completed", 99, null, default);
        Assert.Equal(current.Token, (await f.Scope.Db.PackDiscoveryJobs.AsNoTracking().SingleAsync()).ClaimToken);
        var waiting = new Provider(_ => throw new ColesWorkPendingException());
        await Run(f, current, waiting);
        var job = await f.Scope.Db.PackDiscoveryJobs.AsNoTracking().SingleAsync();
        Assert.Equal("WaitingForExtension", job.Status); Assert.Equal(0, job.Attempts);
        Assert.Null(await jobs.ClaimAsync(default));
        f.Scope.Clock.Now += TimeSpan.FromSeconds(31);
        var resumed = (await jobs.ClaimAsync(default))!;
        await Run(f, resumed, new(_ => Task.FromResult(Results())));
        Assert.Equal("Completed", (await f.Scope.Db.PackDiscoveryJobs.AsNoTracking().SingleAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task Retailer_failures_cool_down_and_expired_or_deleted_items_stop_work()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var f = await ListFixture.CreateAsync();
        await Seed(f); var claim = await Queue(f); var jobs = Jobs(f);
        await Run(f, claim, new(_ => Task.FromResult<ProviderResult<IReadOnlyList<ShopProductSearchResult>>>(new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure(new(ProviderFailureKind.AccessRestricted, "retailer_access_restricted", "Blocked")))));
        var progress = (await jobs.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, default)).Jobs.Single();
        Assert.Equal("Failed", progress.Status); Assert.NotNull(progress.RetryAfter);
        Assert.Equal(1, (await jobs.RequestAsync(f.Scope.UserId, f.Scope.ListId, null, new(f.Scope.UserId), default)).Recent);
        await ShoppingListServiceTests.RegisterFixtureAsync(f, false);
        f.Scope.Clock.Now += TimeSpan.FromHours(7);
        Assert.Equal(1, (await jobs.RequestAsync(f.Scope.UserId, f.Scope.ListId, null, new(f.Scope.UserId), default)).Queued);
        await f.Scope.Db.ShoppingListProducts.Where(i => i.Id == f.ItemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsHidden, true));
        Assert.Null(await jobs.ClaimAsync(default));
        Assert.Equal("Cancelled", (await f.Scope.Db.PackDiscoveryJobs.AsNoTracking().SingleAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task Migration_adds_only_discovery_storage_and_preserves_existing_list_data()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var f = await ListFixture.CreateAsync();
        var before = await f.ItemAsync();
        await f.Scope.Db.Database.ExecuteSqlRawAsync("DROP TABLE \"PackDiscoveryJobs\"");
        var migrations = f.Scope.Db.Database.GetMigrations().ToArray();
        var target = Array.FindIndex(migrations, m => m.EndsWith("_AddPackDiscoveryJobs", StringComparison.Ordinal));
        Assert.True(target > 0);
        var latest = migrations[target]; var previous = migrations[target - 1];
        var history = f.Scope.Db.GetService<IHistoryRepository>();
        await f.Scope.Db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
        await f.Scope.Db.Database.ExecuteSqlRawAsync(f.Scope.Db.GetService<IMigrator>().GenerateScript(previous, latest));
        Assert.Empty(await f.Scope.Db.PackDiscoveryJobs.ToArrayAsync());
        Assert.Equal(before, await f.ItemAsync());
    }

    [PostgreSqlFact]
    public async Task Endpoints_require_session_browser_protection_and_expected_account()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var f = await ListFixture.CreateAsync();
        await Seed(f); await using var app = new ApiFactory(f); using var client = await app.ClientAsync(); using var anonymous = app.CreateClient();
        var path = $"/api/shopping-lists/{f.Scope.ListId}/items/{f.ItemId}/pack-discovery";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var read = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.True(read.Headers.CacheControl!.NoStore);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new PackDiscoveryRequest(f.Scope.UserId))).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new PackDiscoveryRequest(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(path, new PackDiscoveryRequest(f.Scope.UserId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/shopping-lists/{f.Scope.ListId}/items/99999999/pack-discovery")).StatusCode);
    }
    private sealed class ApiFactory(ListFixture f) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>(); services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(f.Scope.Clock);
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = true);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
        public async Task<HttpClient> ClientAsync()
        {
            var token = SessionToken.Create(); f.Scope.Db.UserSessions.Add(new() { UserAccountId = f.Scope.UserId, TokenHash = SessionToken.Hash(token), CreatedDate = f.Scope.Clock.Now.UtcDateTime, ExpiresDate = f.Scope.Clock.Now.UtcDateTime.AddHours(3) });
            await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false });
            client.DefaultRequestHeaders.Add("Cookie", "myshoppinglist_session=" + token); client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1"); return client;
        }
    }
    private sealed class QueueProvider : IColesExtensionQueue
    {
        public List<string> Queries = [];
        public Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(string query, CancellationToken token) { Queries.Add(query); return Task.FromResult(Results()); }
        public Task<ProviderResult<ExtractedShopProduct>> ProductAsync(Uri url, CancellationToken token) => throw new InvalidOperationException();
    }
    [Fact]
    public async Task Coles_discovery_uses_one_family_query_and_rejects_unbounded_input()
    {
        var queue = new QueueProvider(); var provider = new ColesProductProvider(queue);
        await provider.SearchPackSizesAsync("acme original coffee", default); Assert.Equal(new[] { "acme original coffee" }, queue.Queries);
        Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure>(await provider.SearchPackSizesAsync(new string('x', 161), default));
        Assert.Single(queue.Queries);
    }
    [PostgreSqlFact]
    public async Task Concurrent_requests_queue_one_job_per_item_and_retailer()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var f = await ListFixture.CreateAsync();
        await Seed(f);
        async Task<PackDiscoveryView> Submit()
        {
            await using var db = PersistenceScope.Context();
            var service = new PackDiscoveryService(db, f.Scope.Clock, Options.Create(new ProductImportOptions { Enabled = true }), Options.Create(new PriceRefreshOptions()));
            return await service.RequestAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, new(f.Scope.UserId), default);
        }
        var results = await Task.WhenAll(Submit(), Submit());
        Assert.Equal(1, results.Sum(r => r.Queued)); Assert.Equal(1, results.Sum(r => r.AlreadyRunning));
        Assert.Equal(1, await f.Scope.Db.PackDiscoveryJobs.CountAsync());
    }

    private sealed class FamilyBrowser : IRetailerSearchBrowser
    {
        public List<string> Queries = [];
        public Task<ProviderResult<RetailerPage>> ReadAsync(string shopCode, string query, CancellationToken token)
        {
            Queries.Add(query);
            var html = $"<section data-testid='search-results-product-scrollable-content'><a href='{WoolworthsProductParserTests.Url}'>Product</a></section>";
            return Task.FromResult<ProviderResult<RetailerPage>>(new ProviderResult<RetailerPage>.Success(new(ProductSearchQueryBuilder.SearchUrl(shopCode, query), html, DateTimeOffset.UtcNow)));
        }
    }
    [Fact]
    public async Task Woolworths_family_search_does_not_broaden_or_depend_on_exact_package_match()
    {
        var browser = new FamilyBrowser();
        var handler = new RecordingHandler((_, _) => Task.FromResult(RetailerHttpClientTests.Html(WoolworthsFixture.Html)));
        var provider = new WoolworthsProductProvider(RetailerHttpClientTests.Client(handler), new(), new(new RetailerCatalog()), NullLogger<WoolworthsProductProvider>.Instance, browser);
        var result = await provider.SearchPackSizesAsync("yopro mango yoghurt", default);
        Assert.Single(Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success>(result).Value);
        Assert.Equal(new[] { "yopro mango yoghurt" }, browser.Queries); Assert.Single(handler.Requests);
    }

}
