using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using myshoppinglist_api.Services.Models;
using myshoppinglist_api.Workers;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ProductImportProcessorTests
{
    [PostgreSqlFact]
    public async Task Source_success_saves_product_and_finishes_partial_with_honest_comparison_results()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var provider = new StubSource(fixture.Scope.Source);
        await ProcessAsync(fixture, provider);
        var job = await fixture.ReadAsync();
        Assert.Equal(ProductImportJobStatus.Partial, job.Status);
        Assert.NotNull(job.ShoppingListProductId); Assert.Null(job.ClaimToken);
        var results = await fixture.Scope.Db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == job.Id).ToListAsync();
        Assert.Equal(5, results.Count);
        Assert.Equal(RetailerLookupStatus.Exact, results.Single(r => r.ShopId == job.SourceShopId).Status);
        Assert.All(results.Where(r => r.ShopId != job.SourceShopId), r => Assert.Equal(RetailerLookupStatus.NotSupported, r.Status));
        Assert.Equal(1, provider.Calls);
    }

    [PostgreSqlFact]
    public async Task Temporary_source_failure_retries_but_not_found_is_terminal()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var provider = new StubSource(fixture.Scope.Source)
        { Result = new ProviderResult<ExtractedShopProduct>.Failure(new(ProviderFailureKind.Timeout, "timeout", "Temporary")) };
        await ProcessAsync(fixture, provider);
        Assert.Equal(ProductImportJobStatus.Queued, (await fixture.ReadAsync()).Status);
        fixture.Scope.Clock.Now += TimeSpan.FromMinutes(1);
        provider.Result = new ProviderResult<ExtractedShopProduct>.Failure(new(ProviderFailureKind.NotFound, "not_found", "Missing"));
        await ProcessAsync(fixture, provider);
        var job = await fixture.ReadAsync();
        Assert.Equal(ProductImportJobStatus.Failed, job.Status); Assert.Equal(2, job.AttemptCount);
        Assert.Null(job.ShoppingListProductId); Assert.Null(job.NextAttemptDate);
    }

    [PostgreSqlFact]
    public async Task Expired_trial_prevents_network_work()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var provider = new StubSource(fixture.Scope.Source);
        await fixture.Scope.Db.UserAccounts.Where(u => u.Id == fixture.Scope.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ExpiresDate, fixture.Scope.Clock.Now.UtcDateTime.AddSeconds(-1)));
        await ProcessAsync(fixture, provider);
        Assert.Equal(0, provider.Calls);
        Assert.Equal("access_expired", (await fixture.ReadAsync()).ErrorCode);
    }

    [PostgreSqlFact]
    public async Task Access_expiring_during_fetch_prevents_product_writes()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var provider = new StubSource(fixture.Scope.Source)
        {
            BeforeReturn = async token => await fixture.Scope.Db.ShoppingLists.Where(l => l.Id == fixture.Scope.ListId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true), token)
        };
        await ProcessAsync(fixture, provider);
        Assert.Null((await fixture.ReadAsync()).ProductId);
        Assert.False(await fixture.Scope.Db.ShopProducts.AnyAsync(p => p.ShopProductCode == fixture.Scope.Code));
    }

    [PostgreSqlFact]
    public async Task Invalid_source_url_never_calls_provider()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await fixture.Scope.Db.ProductImportJobs.Where(j => j.Id == fixture.Scope.Job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.SourceUrl, "https://localhost/private"));
        var provider = new StubSource(fixture.Scope.Source);
        await ProcessAsync(fixture, provider);
        Assert.Equal("invalid_source_url", (await fixture.ReadAsync()).ErrorCode);
        Assert.Equal(0, provider.Calls);
    }

    [PostgreSqlFact]
    public async Task Unimplemented_source_does_not_retry_or_call_another_provider()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await fixture.Scope.Db.ProductImportJobs.Where(j => j.Id == fixture.Scope.Job.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.SourceUrl, "https://www.woolworths.com.au/shop/productdetails/123"));
        var provider = new StubSource(fixture.Scope.Source);
        await ProcessAsync(fixture, provider);
        var job = await fixture.ReadAsync();
        Assert.Equal(ProductImportJobStatus.Failed, job.Status);
        Assert.Equal("source_not_supported", job.ErrorCode); Assert.Null(job.NextAttemptDate);
        Assert.Equal(0, provider.Calls);
    }

    [PostgreSqlFact]
    public async Task Archived_list_prevents_network_work()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await fixture.Scope.Db.ShoppingLists.Where(l => l.Id == fixture.Scope.ListId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        var provider = new StubSource(fixture.Scope.Source);
        await ProcessAsync(fixture, provider);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(ProductImportJobStatus.Failed, (await fixture.ReadAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task Cancellation_propagates_and_leaves_claim_recoverable()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var provider = new StubSource(fixture.Scope.Source) { BeforeReturn = _ => { cancellation.Cancel(); return Task.CompletedTask; } };
        var claim = (await fixture.Jobs().ClaimNextAsync(default))!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Processor(fixture, provider).ProcessAsync(claim, cancellation.Token));
        var job = await fixture.ReadAsync();
        Assert.Equal(ProductImportJobStatus.Processing, job.Status); Assert.Null(job.ProductId);
        fixture.Scope.Clock.Now += TimeSpan.FromSeconds(601);
        Assert.Equal(1, await fixture.Jobs().RecoverExpiredAsync(default));
    }

    [PostgreSqlFact]
    public async Task Restart_after_source_commit_reuses_list_item_without_fetching_again()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var first = (await fixture.Jobs().ClaimNextAsync(default))!;
        var result = await fixture.Scope.Service.SaveAsync(first.JobId, first.UserAccountId, first.Token, fixture.Scope.Source, default);
        Assert.Equal(ProductPersistenceStatus.Success, result.Status);
        fixture.Scope.Clock.Now += TimeSpan.FromSeconds(601);
        await fixture.Jobs().RecoverExpiredAsync(default);
        var provider = new StubSource(fixture.Scope.Source);
        await ProcessAsync(fixture, provider);
        var job = await fixture.ReadAsync();
        Assert.Equal(ProductImportJobStatus.Partial, job.Status);
        Assert.Equal(result.ShoppingListProductId, job.ShoppingListProductId); Assert.Equal(0, provider.Calls);
        Assert.Equal(2, (await fixture.Scope.Db.ShoppingListProducts.SingleAsync(i => i.Id == job.ShoppingListProductId)).Quantity);
    }

    [PostgreSqlFact]
    public async Task Price_failure_keeps_source_product_and_unavailable_status()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var provider = new StubSource(fixture.Scope.Source with
        { Offer = new ProviderResult<ShopProductOffer>.Failure(new(ProviderFailureKind.Timeout, "timeout", "Unavailable")) });
        await ProcessAsync(fixture, provider);
        var job = await fixture.ReadAsync();
        Assert.NotNull(job.ProductId); Assert.Equal(ProductImportJobStatus.Partial, job.Status);
        Assert.Equal(RetailerLookupStatus.Unavailable, (await fixture.Scope.Db.ProductImportRetailerResults
            .SingleAsync(r => r.ProductImportJobId == job.Id && r.ShopId == job.SourceShopId)).Status);
    }

    [PostgreSqlFact]
    public async Task Hosted_worker_renews_lease_during_fetch_and_stops_cleanly()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        fixture.Options.RenewalSeconds = 1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubSource(fixture.Scope.Source) { BeforeReturn = async token =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        await using var services = Services(fixture, provider);
        using var worker = new ProductImportWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(fixture.Options),
            fixture.Scope.Clock, NullLogger<ProductImportWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var original = (await fixture.ReadAsync()).LeaseExpiresDate;
            fixture.Scope.Clock.Now += TimeSpan.FromSeconds(10);
            await UntilAsync(async () => (await fixture.ReadAsync()).LeaseExpiresDate > original);
            release.TrySetResult();
            await UntilAsync(async () => (await fixture.ReadAsync()).Status == ProductImportJobStatus.Partial);
        }
        finally { await worker.StopAsync(default); }
        Assert.Equal(1, provider.Calls);
    }

    [PostgreSqlFact]
    public async Task Hosted_worker_cancels_inflight_provider_on_shutdown()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubSource(fixture.Scope.Source) { BeforeReturn = async token =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        await using var services = Services(fixture, provider);
        using var worker = new ProductImportWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(fixture.Options),
            fixture.Scope.Clock, NullLogger<ProductImportWorker>.Instance);
        await worker.StartAsync(default);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(ProductImportJobStatus.Processing, (await fixture.ReadAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task Hosted_worker_cancels_fetch_when_its_claim_is_replaced()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        fixture.Options.RenewalSeconds = 1;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubSource(fixture.Scope.Source) { BeforeReturn = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        } };
        await using var services = Services(fixture, provider);
        using var worker = new ProductImportWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(fixture.Options),
            fixture.Scope.Clock, NullLogger<ProductImportWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var replacement = Guid.NewGuid();
            await fixture.Scope.Db.ProductImportJobs.Where(j => j.Id == fixture.Scope.Job.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ClaimToken, replacement));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var job = await fixture.ReadAsync();
            Assert.Equal(replacement, job.ClaimToken); Assert.Null(job.ProductId);
            Assert.Equal(ProductImportJobStatus.Processing, job.Status);
        }
        finally { await worker.StopAsync(default); }
    }

    [PostgreSqlFact]
    public async Task Hosted_worker_bounds_unexpected_provider_failures()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        fixture.Options.MaxAttempts = 1;
        var provider = new StubSource(fixture.Scope.Source)
        { BeforeReturn = _ => throw new InvalidOperationException("Simulated provider bug") };
        await using var services = Services(fixture, provider);
        using var worker = new ProductImportWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(fixture.Options),
            fixture.Scope.Clock, NullLogger<ProductImportWorker>.Instance);
        await worker.StartAsync(default);
        try { await UntilAsync(async () => (await fixture.ReadAsync()).Status == ProductImportJobStatus.Failed); }
        finally { await worker.StopAsync(default); }
        var job = await fixture.ReadAsync();
        Assert.Equal("unexpected_processing_failure", job.ErrorCode);
        Assert.Equal(1, provider.Calls); Assert.Null(job.NextAttemptDate);
    }

    private static async Task ProcessAsync(ImportFixture fixture, StubSource provider)
    {
        var claim = Assert.IsType<ProductImportClaim>(await fixture.Jobs().ClaimNextAsync(default));
        await Processor(fixture, provider).ProcessAsync(claim, default);
    }
    private static ProductImportProcessor Processor(ImportFixture fixture, StubSource provider) => new(fixture.Jobs(),
        fixture.Scope.Service, new RetailerProviderRegistry([provider]), new ProductUrlValidator(new RetailerCatalog()),
        NullLogger<ProductImportProcessor>.Instance);
    private static ServiceProvider Services(ImportFixture fixture, StubSource provider) => new ServiceCollection()
        .AddSingleton<TimeProvider>(fixture.Scope.Clock)
        .AddSingleton<IOptions<ProductImportOptions>>(Options.Create(fixture.Options))
        .AddLogging()
        .AddScoped<MyShoppingListDbContext>(_ => PersistenceScope.Context())
        .AddScoped<ProductImportJobService>()
        .AddScoped(s => PersistenceScope.BuildService(s.GetRequiredService<MyShoppingListDbContext>(), fixture.Scope.Clock))
        .AddSingleton(new RetailerProviderRegistry([provider]))
        .AddSingleton(new ProductUrlValidator(new RetailerCatalog()))
        .AddScoped<ProductImportProcessor>().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(30, timeout.Token);
    }

    private sealed class StubSource(ExtractedShopProduct source) : IShopProductProvider
    {
        public string ShopCode => "coles";
        public int Calls { get; private set; }
        public ProviderResult<ExtractedShopProduct> Result { get; set; } = new ProviderResult<ExtractedShopProduct>.Success(source);
        public Func<CancellationToken, Task>? BeforeReturn { get; init; }
        public async Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(Uri url, CancellationToken token)
        { Calls++; if (BeforeReturn is not null) await BeforeReturn(token); return Result; }
        public Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(ProductIdentity product, ShopLocationContext? location, CancellationToken token) => throw new NotSupportedException();
        public Task<ProviderResult<ShopProductOffer>> GetOfferAsync(ShopProductSearchResult product, ShopLocationContext? location, CancellationToken token) => throw new NotSupportedException();
    }
}
