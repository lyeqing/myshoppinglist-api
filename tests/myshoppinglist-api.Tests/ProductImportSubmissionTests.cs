using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ProductImportSubmissionTests
{
    [PostgreSqlFact]
    public async Task Creates_queued_job_and_pending_results_without_fetching()
    {
        await using var scope = await SubmissionScope.CreateAsync();
        var result = await scope.SubmitAsync(new(scope.Url + "#details", 3));
        Assert.Equal(202, result.StatusCode); Assert.False(result.Response!.Reused);
        var job = await scope.Db.ProductImportJobs.SingleAsync(j => j.Id == result.Response.JobId);
        Assert.Equal(ProductImportJobStatus.Queued, job.Status);
        Assert.Equal(scope.Url, job.NormalisedSourceUrl); Assert.Equal(3, job.RequestedQuantity);
        Assert.Null(job.ClaimToken); Assert.Null(job.ProductId);
        var results = await scope.Db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == job.Id).ToListAsync();
        Assert.Equal(5, results.Count); Assert.All(results, r => Assert.Equal(RetailerLookupStatus.Pending, r.Status));
    }

    [PostgreSqlFact]
    public async Task Concurrent_normalised_duplicates_share_job_and_original_quantity()
    {
        await using var scope = await SubmissionScope.CreateAsync();
        var first = await scope.SubmitAsync(new(scope.Url, 4));
        async Task<ProductImportSubmissionResult> Submit(int quantity)
        {
            await using var db = PersistenceScope.Context();
            return await scope.Service(db).SubmitAsync(scope.AccountId, scope.ListId, new(scope.Url + "#fragment", quantity), default);
        }
        var results = await Task.WhenAll(Submit(7), Submit(9));
        Assert.All(results, r => { Assert.True(r.Response!.Reused); Assert.Equal(first.Response!.JobId, r.Response.JobId); Assert.Equal(4, r.Response.Quantity); });
        Assert.Equal(1, await scope.Db.ProductImportJobs.CountAsync(j => j.ShoppingListId == scope.ListId));
        // A different URL submitted concurrently must also create only one row.
        var newUrl = scope.Url + "-other";
        async Task<ProductImportSubmissionResult> Fresh()
        {
            await using var db = PersistenceScope.Context();
            return await scope.Service(db).SubmitAsync(scope.AccountId, scope.ListId, new(newUrl, 2), default);
        }
        var fresh = await Task.WhenAll(Fresh(), Fresh());
        Assert.Equal(fresh[0].Response!.JobId, fresh[1].Response!.JobId);
        Assert.Single(fresh, r => !r.Response!.Reused);
    }

    [PostgreSqlFact]
    public async Task Invalid_quantity_url_or_unimplemented_retailer_creates_nothing()
    {
        await using var scope = await SubmissionScope.CreateAsync();
        foreach (var request in new ProductImportRequest[] { new(scope.Url, 0), new(scope.Url, -1), new(null),
            new("https://localhost/private"), new("http://www.coles.com.au/product/a"), new("https://www.woolworths.com.au/shop/productdetails/1") })
            Assert.Equal(400, (await scope.SubmitAsync(request)).StatusCode);
        Assert.False(await scope.Db.ProductImportJobs.AnyAsync(j => j.ShoppingListId == scope.ListId));
    }

    [PostgreSqlFact]
    public async Task Wrong_owner_is_not_found_and_archived_or_expired_list_is_unavailable()
    {
        await using var scope = await SubmissionScope.CreateAsync();
        await using var other = await SubmissionScope.CreateAsync();
        Assert.Equal(404, (await scope.Service().SubmitAsync(other.AccountId, scope.ListId, new(scope.Url), default)).StatusCode);
        await scope.Db.ShoppingLists.Where(l => l.Id == scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        Assert.Equal(409, (await scope.SubmitAsync(new(scope.Url))).StatusCode);
        await scope.Db.ShoppingLists.Where(l => l.Id == scope.ListId).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.IsArchived, false).SetProperty(l => l.ExpiresDate, scope.Clock.Now.UtcDateTime.AddSeconds(-1)));
        Assert.Equal(409, (await scope.SubmitAsync(new(scope.Url))).StatusCode);
        Assert.False(await scope.Db.ProductImportJobs.AnyAsync(j => j.ShoppingListId == scope.ListId));
    }

    [PostgreSqlFact]
    public async Task Expired_trial_is_rejected_and_terminal_job_does_not_block_resubmission()
    {
        await using var scope = await SubmissionScope.CreateAsync();
        var first = await scope.SubmitAsync(new(scope.Url));
        await scope.Db.ProductImportJobs.Where(j => j.Id == first.Response!.JobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, ProductImportJobStatus.Failed));
        var next = await scope.SubmitAsync(new(scope.Url));
        Assert.NotEqual(first.Response!.JobId, next.Response!.JobId);
        scope.Clock.Now += TimeSpan.FromHours(4);
        Assert.Equal(401, (await scope.SubmitAsync(new(scope.Url))).StatusCode);
    }
}

internal sealed class SubmissionScope : IAsyncDisposable
{
    private readonly ImportFixture fixture;
    private SubmissionScope(ImportFixture fixture) => this.fixture = fixture;
    public MyShoppingListDbContext Db => fixture.Scope.Db;
    public PersistenceClock Clock => fixture.Scope.Clock;
    public long AccountId => fixture.Scope.UserId;
    public long ListId => fixture.Scope.ListId;
    public string Url => fixture.Scope.Source.ProductUrl.AbsoluteUri;
    public static async Task<SubmissionScope> CreateAsync()
    {
        var fixture = await ImportFixture.CreateAsync();
        await fixture.Scope.Db.ProductImportJobs.Where(j => j.Id == fixture.Scope.Job.Id).ExecuteDeleteAsync();
        return new(fixture);
    }
    public ProductImportSubmissionService Service(MyShoppingListDbContext? db = null) => new(db ?? Db,
        new(new RetailerCatalog()), new RetailerProviderRegistry([new NoFetchProvider()]), Clock, NullLogger<ProductImportSubmissionService>.Instance);
    public Task<ProductImportSubmissionResult> SubmitAsync(ProductImportRequest request) => Service().SubmitAsync(AccountId, ListId, request, default);
    public ValueTask DisposeAsync() => fixture.DisposeAsync();
    private sealed class NoFetchProvider : IShopProductProvider
    {
        public string ShopCode => "coles";
        public Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(Uri url, CancellationToken token) => throw new InvalidOperationException("Submission must not fetch.");
        public Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(ProductIdentity product, ShopLocationContext? location, CancellationToken token) => throw new NotSupportedException();
        public Task<ProviderResult<ShopProductOffer>> GetOfferAsync(ShopProductSearchResult product, ShopLocationContext? location, CancellationToken token) => throw new NotSupportedException();
    }
}
