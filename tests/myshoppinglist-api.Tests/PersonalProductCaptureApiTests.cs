using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class PersonalProductCaptureApiTests
{
    private const string Path = "/api/user-extension/captures";

    [PostgreSqlFact]
    public async Task All_three_retailers_capture_into_private_storage_and_the_list_without_collection_tasks()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        foreach (var retailer in new[] { "coles", "woolworths", "aldi" })
        {
            var request = f.Request(retailer);
            var response = await client.PostAsJsonAsync(Path, request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var saved = (await response.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
            Assert.Equal("Completed", saved.Status); Assert.True(saved.PriceSaved); Assert.False(saved.Reused);
            Assert.Equal(request.ObservedAt, saved.ObservedAt);
            Assert.Equal(f.First.Id, (await f.Db.Products.SingleAsync(p => p.Id == saved.ProductId)).UserAccountId);
            var mapping = await f.Db.ShopProducts.SingleAsync(m => m.ProductId == saved.ProductId && m.ProductUrl == request.Url);
            var price = await f.Db.ShopProductPrices.SingleAsync(p => p.ShopProductId == mapping.Id);
            Assert.Equal(f.First.Id, mapping.UserAccountId); Assert.Equal(f.First.Id, price.UserAccountId);
            Assert.Equal(request.ObservedAt.UtcTicks / 10, price.CheckedDate.Ticks / 10);
            Assert.Equal(SourceType.BrowserExtension, price.SourceType);
            Assert.Equal(PriceScope.Unknown, price.PriceScope); Assert.Null(price.ShopLocationId);
            Assert.Equal(2, (await f.Db.ShoppingListProducts.SingleAsync(i => i.Id == saved.ShoppingListProductId)).Quantity);
            Assert.Equal(ProductImportJobStatus.Completed, (await f.Db.ProductImportJobs.SingleAsync(j => j.Id == saved.JobId)).Status);
            var observation = await f.Db.ContributionObservations.SingleAsync(o => o.ImportJobId == saved.JobId);
            Assert.Equal(f.First.Id, observation.UserAccountId); Assert.Equal("Accepted", observation.Outcome);
            Assert.Equal(request.ObservedAt.UtcTicks / 10, observation.CollectedAt.Ticks / 10);
            Assert.Null(observation.TaskId);
        }
        var listResponse = await client.GetAsync($"/api/shopping-lists/{f.FirstList.Id}/items");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = (await listResponse.Content.ReadFromJsonAsync<ShoppingListItemPage>())!;
        Assert.Equal(2, list.Items.Count); // Exact Coles/Woolworths identity shares this account's product.
        Assert.Empty(await f.Db.ColesExtensionTasks.ToArrayAsync());
        Assert.Empty(await f.Db.UserExtensionImportTasks.ToArrayAsync());
        Assert.Empty(await f.Db.PackDiscoveryJobs.ToArrayAsync());
        Assert.Empty(await f.Db.BrandDiscoveryJobs.ToArrayAsync());
        Assert.All(await f.Db.ProductImportRetailerResults.ToArrayAsync(), r => Assert.NotEqual(RetailerLookupStatus.Pending, r.Status));
    }

    [PostgreSqlFact]
    public async Task Accounts_can_use_the_same_request_id_and_product_without_sharing_metadata_or_prices()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var first = await f.ClientAsync(f.First.Id);
        using var second = await f.ClientAsync(f.Second.Id);
        var a = f.Request();
        var b = a with { ListId = f.SecondList.Id, Evidence = CaptureApiScope.Evidence(a.Url, 9m) };
        var firstResponse = await first.PostAsJsonAsync(Path, a);
        var secondResponse = await second.PostAsJsonAsync(Path, b);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode); Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var savedA = (await firstResponse.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        var savedB = (await secondResponse.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        Assert.NotEqual(savedA.ProductId, savedB.ProductId);
        Assert.Equal(4m, (await f.Db.ShopProductPrices.SingleAsync(p => p.UserAccountId == f.First.Id)).Price);
        Assert.Equal(9m, (await f.Db.ShopProductPrices.SingleAsync(p => p.UserAccountId == f.Second.Id)).Price);
        Assert.Equal(2, await f.Db.ProductImportJobs.CountAsync(j => j.CaptureRequestId == a.RequestId));
        var denied = await first.PostAsJsonAsync(Path, f.Request() with { ListId = f.SecondList.Id });
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var spoof = JsonSerializer.SerializeToElement(new { a.ListId, a.Url, a.Quantity, a.Evidence, RequestId = Guid.NewGuid(),
            a.ObservedAt, UserAccountId = f.Second.Id });
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync(Path, spoof)).StatusCode);
        Assert.Equal(1, await f.Db.Products.CountAsync(p => p.UserAccountId == f.Second.Id));
    }

    [PostgreSqlFact]
    public async Task Replays_return_the_original_receipt_and_never_overwrite_later_edits()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var request = f.Request();
        var response = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        await f.Db.ShoppingListProducts.Where(i => i.Id == saved.ShoppingListProductId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Quantity, 7).SetProperty(i => i.Notes, "Later edit").SetProperty(i => i.IsPurchased, true));
        var repeated = await client.PostAsJsonAsync(Path, request with { Url = request.Url + "#fragment",
            ObservedAt = request.ObservedAt.ToOffset(TimeSpan.FromHours(10.5)) });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var receipt = (await repeated.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        Assert.Equal(saved with { Reused = true }, receipt);
        var edited = await f.Db.ShoppingListProducts.SingleAsync();
        Assert.Equal(7, edited.Quantity); Assert.Equal("Later edit", edited.Notes); Assert.True(edited.IsPurchased);
        Assert.Single(await f.Db.ProductImportJobs.ToArrayAsync());
        Assert.Single(await f.Db.ContributionObservations.ToArrayAsync());
        Assert.Single(await f.Db.ShopProductPriceHistory.ToArrayAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, request with { Quantity = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, request with { Evidence = CaptureApiScope.Evidence(request.Url, 1m) })).StatusCode);
        Assert.Single(await f.Db.ProductImportJobs.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Concurrent_http_retries_create_only_one_capture()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var request = f.Request();
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync(Path, request)));
        foreach (var response in responses) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipts = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>()));
        Assert.Single(receipts.Select(r => r!.JobId).Distinct());
        Assert.Equal(2, receipts.Count(r => r!.Reused));
        Assert.Single(await f.Db.Products.ToArrayAsync()); Assert.Single(await f.Db.ShoppingListProducts.ToArrayAsync());
        Assert.Single(await f.Db.ContributionObservations.ToArrayAsync()); Assert.Single(await f.Db.ShopProductPriceHistory.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Missing_price_keeps_the_product_without_inventing_price_or_location()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var request = f.Request("aldi") with { Evidence = AldiProductParserTests.Evidence(price: null) };
        var response = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!.PriceSaved);
        Assert.Single(await f.Db.Products.ToArrayAsync()); Assert.Single(await f.Db.ShoppingListProducts.ToArrayAsync());
        Assert.Empty(await f.Db.ShopProductPrices.ToArrayAsync()); Assert.Empty(await f.Db.ShopProductPriceHistory.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Old_observations_keep_their_capture_date_and_are_not_presented_as_fresh_prices()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var request = f.Request() with { ObservedAt = f.Clock.Now.AddDays(-10) };
        var response = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var price = await f.Db.ShopProductPrices.SingleAsync();
        Assert.Equal(request.ObservedAt.UtcTicks / 10, price.CheckedDate.Ticks / 10);
        var plan = (await new myshoppinglist_api.Services.ShoppingListService(f.Db, f.Clock).PlanAsync(f.First.Id, f.FirstList.Id, default))!;
        Assert.Equal("Stale price", Assert.Single(Assert.Single(plan.Items).Prices).Status);
        Assert.Equal(0, plan.Lowest.Subtotal);
    }

    [PostgreSqlFact]
    public async Task Invalid_evidence_dates_and_quantities_leave_no_partial_capture()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var valid = f.Request();
        foreach (var invalid in new[] { valid with { Evidence = new("{}", []) }, valid with { Quantity = 0 },
            valid with { RequestId = Guid.Empty }, valid with { ObservedAt = default }, valid with { ObservedAt = f.Clock.Now.AddDays(1) },
            valid with { Url = "http://www.coles.com.au/product/123456" }, valid with { Evidence = new(null, [new string('x', 2000001)]) } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, invalid)).StatusCode);
        Assert.Empty(await f.Db.Products.ToArrayAsync()); Assert.Empty(await f.Db.ProductImportJobs.ToArrayAsync());
        Assert.Empty(await f.Db.ContributionObservations.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Persistence_conflict_rolls_back_the_receipt_and_observation_too()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var first = f.Request();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Path, first)).StatusCode);
        var conflict = f.Request() with { Evidence = CaptureApiScope.Evidence(first.Url, 4m, "4006381333931") };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, conflict)).StatusCode);
        Assert.Single(await f.Db.ProductImportJobs.ToArrayAsync()); Assert.Single(await f.Db.ContributionObservations.ToArrayAsync());
        Assert.Single(await f.Db.Products.ToArrayAsync()); Assert.Single(await f.Db.ShopProductPriceHistory.ToArrayAsync());
        Assert.False(await f.Db.ProductImportJobs.AnyAsync(j => j.CaptureRequestId == conflict.RequestId));
    }

    [PostgreSqlFact]
    public async Task Removed_item_is_not_resurrected_by_delivery_retry()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var request = f.Request();
        var response = await client.PostAsJsonAsync(Path, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        await f.Db.ProductImportJobs.Where(j => j.Id == saved.JobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.ShoppingListProductId, (long?)null));
        await f.Db.ShoppingListProducts.Where(i => i.Id == saved.ShoppingListProductId).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        Assert.Empty(await f.Db.ShoppingListProducts.ToArrayAsync());
        Assert.Single(await f.Db.ProductImportJobs.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Capture_is_disabled_by_default_and_requires_an_allowed_extension_and_live_session()
    {
        await using var f = await CaptureApiScope.CreateAsync(enabled: false);
        Assert.False(new UserExtensionOptions().PersonalCapturesEnabled);
        using var valid = await f.ClientAsync(f.First.Id);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await valid.PostAsJsonAsync(Path, f.Request())).StatusCode);
        valid.DefaultRequestHeaders.Remove("X-MyShoppingList-Extension");
        Assert.Equal(HttpStatusCode.Forbidden, (await valid.PostAsJsonAsync(Path, f.Request())).StatusCode);
        using var anonymous = f.Api.CreateClient();
        anonymous.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Path, f.Request())).StatusCode);
        using var expired = await f.ClientAsync(f.First.Id);
        await f.Db.UserSessions.Where(s => s.UserAccountId == f.First.Id).ExecuteUpdateAsync(s => s.SetProperty(s => s.RevokedDate, f.Clock.Now.UtcDateTime));
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.PostAsJsonAsync(Path, f.Request())).StatusCode);
        Assert.Empty(await f.Db.ProductImportJobs.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Extension_origin_cookie_request_protection_and_trial_expiry_remain_enforced()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        client.DefaultRequestHeaders.Add("Origin", "https://unrelated.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Path, f.Request())).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        var token = client.DefaultRequestHeaders.Authorization!.Parameter;
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("Cookie", "myshoppinglist_session=" + token);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Path, f.Request())).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Path, f.Request())).StatusCode);
        await f.Db.UserAccounts.Where(a => a.Id == f.First.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresDate, f.Clock.Now.UtcDateTime.AddSeconds(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Path, f.Request())).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Capture_submission_rate_limit_counts_retries_without_duplicate_writes()
    {
        await using var f = await CaptureApiScope.CreateAsync(rateLimit: 2);
        using var client = await f.ClientAsync(f.First.Id);
        var request = f.Request();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        Assert.Single(await f.Db.ProductImportJobs.ToArrayAsync());
    }
}

internal sealed class CaptureApiScope : IAsyncDisposable
{
    internal const string ExtensionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string original;
    private readonly string schema = "codex_capture_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; }
    public MyShoppingListDbContext Db { get; }
    public PersistenceClock Clock { get; } = new() { Now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero) };
    public UserAccount First { get; private set; } = null!;
    public UserAccount Second { get; private set; } = null!;
    public ShoppingList FirstList { get; private set; } = null!;
    public ShoppingList SecondList { get; private set; } = null!;
    public WebApplicationFactory<Program> Api { get; }
    private CaptureApiScope(string connection, bool enabled, int rateLimit)
    {
        original = connection;
        ConnectionString = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, Pooling = false }.ConnectionString;
        Db = new(new DbContextOptionsBuilder<MyShoppingListDbContext>().UseNpgsql(ConnectionString).Options);
        Api = new CaptureFactory(this, enabled, rateLimit);
    }
    public static async Task<CaptureApiScope> CreateAsync(bool enabled = true, int rateLimit = 100)
    {
        var f = new CaptureApiScope(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!, enabled, rateLimit);
        await f.SchemaCommand($"CREATE SCHEMA \"{f.schema}\"");
        try
        {
            await f.Db.Database.MigrateAsync();
            f.First = new() { DisplayName = "Capture trial", IsTrial = true, ExpiresDate = f.Clock.Now.UtcDateTime.AddHours(3), CreatedDate = f.Clock.Now.UtcDateTime, UpdatedDate = f.Clock.Now.UtcDateTime };
            f.Second = new() { DisplayName = "Capture registered", Email = "capture@example.test", PasswordHash = "fixture", PasswordSalt = "fixture", CreatedDate = f.Clock.Now.UtcDateTime, UpdatedDate = f.Clock.Now.UtcDateTime };
            f.FirstList = new() { UserAccount = f.First, Name = "First list", CreatedDate = f.Clock.Now.UtcDateTime, UpdatedDate = f.Clock.Now.UtcDateTime };
            f.SecondList = new() { UserAccount = f.Second, Name = "Second list", CreatedDate = f.Clock.Now.UtcDateTime, UpdatedDate = f.Clock.Now.UtcDateTime };
            f.Db.AddRange(f.FirstList, f.SecondList); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    public PersonalProductCaptureRequest Request(string retailer = "coles")
    {
        var url = retailer == "aldi" ? AldiProductParserTests.Url : retailer == "coles"
            ? "https://www.coles.com.au/product/123456" : "https://www.woolworths.com.au/shop/productdetails/123456";
        return new(FirstList.Id, url, 2, retailer == "aldi" ? AldiProductParserTests.Evidence() : Evidence(url), Guid.NewGuid(), Clock.Now.AddSeconds(-2));
    }
    public static ColesEvidence Evidence(string url, decimal? price = 4m, string gtin = "9300000000002") => new(null,
        [JsonSerializer.Serialize(new { type = "Product", sku = "123456", name = "Acme Original Coffee 500g", gtin,
            brand = new { name = "Acme" }, offers = new { price, priceCurrency = "AUD", url } }).Replace("\"type\"", "\"@type\"")]);
    public async Task<HttpClient> ClientAsync(long accountId)
    {
        var token = SessionToken.Create();
        Db.UserSessions.Add(new() { UserAccountId = accountId, TokenHash = SessionToken.Hash(token), CreatedDate = Clock.Now.UtcDateTime, ExpiresDate = Clock.Now.UtcDateTime.AddDays(1) });
        await Db.SaveChangesAsync(); Db.ChangeTracker.Clear();
        var client = Api.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-MyShoppingList-Extension", ExtensionId);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        return client;
    }
    private async Task SchemaCommand(string sql)
    {
        await using var connection = new NpgsqlConnection(original); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }
    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync(); await Db.DisposeAsync();
        await SchemaCommand($"DROP SCHEMA \"{schema}\" CASCADE");
    }
    private sealed class CaptureFactory(CaptureApiScope f, bool enabled, int rateLimit) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(f.ConnectionString));
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(f.Clock);
                services.Configure<UserExtensionOptions>(o => { o.PersonalCapturesEnabled = enabled; o.AllowedExtensionIds = [ExtensionId]; });
                services.PostConfigure<ProductImportOptions>(o => { o.Enabled = false; o.SubmissionRequestsPerWindow = rateLimit; });
                services.PostConfigure<PriceRefreshOptions>(o => o.Enabled = false);
            });
        }
    }
}
