using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ProductImportEndpointTests
{
    [PostgreSqlFact]
    public async Task Discovery_endpoint_checks_owner_pagination_and_expired_session()
    {
        await using var app = new ImportApiFactory();
        using var client = app.Client(); using var other = app.Client();
        var trial = await Read<TrialStartResponse>(await client.PostAsync("/api/auth/trial", null));
        await other.PostAsync("/api/auth/trial", null);
        for (var i = 0; i < 3; i++) await client.PostAsJsonAsync(SubmitUrl(trial.ShoppingListId), new ProductImportRequest(app.Provider.Url + i));
        var route = $"/api/shopping-lists/{trial.ShoppingListId}/imports";
        var first = await Read<ProductImportPage>(await client.GetAsync(route + "?pageSize=2"));
        Assert.Equal(2, first.Items.Count); Assert.NotNull(first.NextBeforeId);
        var second = await Read<ProductImportPage>(await client.GetAsync(route + $"?pageSize=2&beforeId={first.NextBeforeId}"));
        Assert.Single(second.Items); Assert.Null(second.NextBeforeId);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?pageSize=51")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?beforeId=0")).StatusCode);
        app.Clock.Now += TimeSpan.FromHours(4);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static async Task<T> Read<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Json))!;
    private static string SubmitUrl(long listId) => $"/api/shopping-lists/{listId}/products/url";

    [PostgreSqlFact]
    public Task Submission_returns_202_before_fetch_finishes_and_worker_completes_the_job() => AssertSourceImport("coles");

    [PostgreSqlFact]
    public Task Woolworths_submission_runs_through_the_worker_and_polling_endpoint() => AssertSourceImport("woolworths");

    private static async Task AssertSourceImport(string retailer)
    {
        await using var app = new ImportApiFactory(worker: true, retailer: retailer);
        await app.AssertEmptyQueueAsync();
        using var client = app.Client();
        var trial = await Read<TrialStartResponse>(await client.PostAsync("/api/auth/trial", null));
        var response = await client.PostAsJsonAsync(SubmitUrl(trial.ShoppingListId), new ProductImportRequest(app.Provider.Url, 3));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await Read<ProductImportAcceptedResponse>(response);
        Assert.Equal(accepted.StatusUrl, response.Headers.Location!.OriginalString);
        Assert.True(response.Headers.CacheControl!.NoStore);
        await app.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pendingResponse = await client.GetAsync(accepted.StatusUrl);
        var pending = await Read<ProductImportStatusResponse>(pendingResponse);
        Assert.Equal(ProductImportJobStatus.Processing, pending.Status); Assert.Null(pending.Product);
        var json = await pendingResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("claimToken", json); Assert.DoesNotContain("leaseExpiresDate", json);
        app.Provider.Release.TrySetResult();
        ProductImportStatusResponse? completed = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        do
        {
            completed = await Read<ProductImportStatusResponse>(await client.GetAsync(accepted.StatusUrl, timeout.Token));
            if (completed.Status is ProductImportJobStatus.Partial or ProductImportJobStatus.Failed) break;
            await Task.Delay(30, timeout.Token);
        } while (true);
        Assert.Equal(ProductImportJobStatus.Partial, completed.Status);
        Assert.NotNull(completed.Product); Assert.NotNull(completed.ShoppingListProductId);
        Assert.Equal(3, completed.Quantity);
        Assert.Equal(1, app.Provider.Calls);
        Assert.Equal(4, completed.Retailers.Count(r => r.Status == RetailerLookupStatus.NotSupported));
        Assert.Equal(23, Assert.Single(completed.Retailers.Single(r => r.Status == RetailerLookupStatus.Exact).Prices).Price);
    }

    [PostgreSqlFact]
    public async Task Other_user_cannot_submit_to_list_or_read_job_and_anonymous_access_is_rejected()
    {
        await using var app = new ImportApiFactory();
        using var first = app.Client(); using var other = app.Client(); using var anonymous = app.Client();
        var trial = await Read<TrialStartResponse>(await first.PostAsync("/api/auth/trial", null));
        await other.PostAsync("/api/auth/trial", null);
        var accepted = await Read<ProductImportAcceptedResponse>(await first.PostAsJsonAsync(SubmitUrl(trial.ShoppingListId), new ProductImportRequest(app.Provider.Url)));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(accepted.StatusUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(SubmitUrl(trial.ShoppingListId), new ProductImportRequest(app.Provider.Url))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(accepted.StatusUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(SubmitUrl(trial.ShoppingListId), new ProductImportRequest(app.Provider.Url))).StatusCode);
        Assert.Equal(0, app.Provider.Calls);
    }

    [PostgreSqlFact]
    public async Task Duplicate_click_reuses_job_and_validation_or_missing_browser_header_creates_no_extra_job()
    {
        await using var app = new ImportApiFactory();
        using var client = app.Client();
        var trial = await Read<TrialStartResponse>(await client.PostAsync("/api/auth/trial", null));
        var url = SubmitUrl(trial.ShoppingListId);
        var first = await Read<ProductImportAcceptedResponse>(await client.PostAsJsonAsync(url, new ProductImportRequest(app.Provider.Url, 2)));
        var second = await Read<ProductImportAcceptedResponse>(await client.PostAsJsonAsync(url, new ProductImportRequest(app.Provider.Url + "#details", 9)));
        Assert.Equal(first.JobId, second.JobId); Assert.True(second.Reused); Assert.Equal(2, second.Quantity);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new ProductImportRequest(app.Provider.Url, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { url = app.Provider.Url, quantity = 1.5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, new StringContent("{", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, new StringContent("null", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new ProductImportRequest("https://localhost/private"))).StatusCode);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, new ProductImportRequest(app.Provider.Url))).StatusCode);
        await using var db = PersistenceScope.Context();
        Assert.Equal(1, await db.ProductImportJobs.CountAsync(j => j.ShoppingListId == trial.ShoppingListId));
    }

    [PostgreSqlFact]
    public async Task Rate_limit_is_per_account_and_does_not_throttle_status_polling()
    {
        await using var app = new ImportApiFactory(limit: 1);
        using var first = app.Client(); using var other = app.Client();
        var trial = await Read<TrialStartResponse>(await first.PostAsync("/api/auth/trial", null));
        var secondTrial = await Read<TrialStartResponse>(await other.PostAsync("/api/auth/trial", null));
        var accepted = await Read<ProductImportAcceptedResponse>(await first.PostAsJsonAsync(SubmitUrl(trial.ShoppingListId), new ProductImportRequest(app.Provider.Url)));
        var limited = await first.PostAsJsonAsync(SubmitUrl(trial.ShoppingListId), new ProductImportRequest(app.Provider.Url));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(accepted.StatusUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await other.PostAsJsonAsync(SubmitUrl(secondTrial.ShoppingListId), new ProductImportRequest(app.Provider.Url))).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Expired_session_cannot_submit_or_poll()
    {
        await using var app = new ImportApiFactory();
        using var client = app.Client();
        var trial = await Read<TrialStartResponse>(await client.PostAsync("/api/auth/trial", null));
        var accepted = await Read<ProductImportAcceptedResponse>(await client.PostAsJsonAsync(SubmitUrl(trial.ShoppingListId), new ProductImportRequest(app.Provider.Url)));
        app.Clock.Now += TimeSpan.FromHours(4);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(accepted.StatusUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(SubmitUrl(trial.ShoppingListId), new ProductImportRequest(app.Provider.Url))).StatusCode);
    }

    private sealed class ImportApiFactory(bool worker = false, int limit = 20, string retailer = "coles") : WebApplicationFactory<Program>
    {
        public PersistenceClock Clock { get; } = new();
        public BlockingProvider Provider { get; } = new(retailer);
        private readonly ConcurrentBag<long> accounts = [];
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.PostConfigure<ProductImportOptions>(o => { o.Enabled = worker; o.SubmissionRequestsPerWindow = limit; });
                services.RemoveAll<IShopProductProvider>(); services.AddSingleton<IShopProductProvider>(Provider);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!)
                    .AddInterceptors(new AccountCapture(accounts)));
            });
        }
        public async Task AssertEmptyQueueAsync()
        {
            await using var db = PersistenceScope.Context();
            Assert.False(await db.ProductImportJobs.AnyAsync(j => j.Status == ProductImportJobStatus.Queued || j.Status == ProductImportJobStatus.Processing),
                "The worker HTTP test requires a database without unrelated active import jobs.");
        }
        public HttpClient Client()
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1"); return client;
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await using var db = PersistenceScope.Context();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var ids = accounts.ToArray();
            var products = await db.Products.Where(p => p.Name == Provider.Code).Select(p => p.Id).ToListAsync();
            var mappings = await db.ShopProducts.Where(p => products.Contains(p.ProductId)).Select(p => p.Id).ToListAsync();
            await db.UserAccounts.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync();
            await db.ShopProductPrices.Where(p => mappings.Contains(p.ShopProductId)).ExecuteDeleteAsync();
            await db.ShopProductPriceHistory.Where(p => mappings.Contains(p.ShopProductId)).ExecuteDeleteAsync();
            await db.ShopProducts.Where(p => mappings.Contains(p.Id)).ExecuteDeleteAsync();
            await db.Products.Where(p => products.Contains(p.Id)).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
    }
    private sealed class AccountCapture(ConcurrentBag<long> accounts) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            foreach (var account in data.Context!.ChangeTracker.Entries<UserAccount>()) accounts.Add(account.Entity.Id);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class BlockingProvider(string retailer) : IShopProductProvider
    {
        public string Code { get; } = "import-http-test-" + Guid.NewGuid().ToString("N");
        public string Url => (retailer == "coles" ? "https://www.coles.com.au/product/" : "https://www.woolworths.com.au/shop/productdetails/") + Code;
        public string ShopCode => retailer;
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(Uri url, CancellationToken token)
        {
            Calls++; Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            var checkedDate = DateTimeOffset.UtcNow.AddMinutes(-1);
            return new ProviderResult<ExtractedShopProduct>.Success(new()
            {
                ShopCode = ShopCode, ShopProductCode = Code, ProductUrl = url, Identity = new() { Name = Code, Brand = Code },
                SourceType = SourceType.StructuredData, CheckedDate = checkedDate,
                Offer = new ProviderResult<ShopProductOffer>.Success(new()
                { ShopCode = ShopCode, Price = 23, Currency = "AUD", PriceScope = PriceScope.Unknown,
                    SourceType = SourceType.StructuredData, SourceUrl = url, CheckedDate = checkedDate })
            });
        }
        public Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(ProductIdentity product, ShopLocationContext? location, CancellationToken token) => throw new NotSupportedException();
        public Task<ProviderResult<ShopProductOffer>> GetOfferAsync(ShopProductSearchResult product, ShopLocationContext? location, CancellationToken token) => throw new NotSupportedException();
    }
}
