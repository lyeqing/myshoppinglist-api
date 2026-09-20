using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class RetailerComparisonTests
{
    [PostgreSqlFact]
    public async Task Restart_after_comparison_commit_preserves_result_without_fetching_again()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var provider = new ComparisonProvider(Other(f));
        await using (var services = Services(f, provider))
        await using (var scope = services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<RetailerComparisonPersistenceService>().SaveExactAsync(claim, 2, provider.Source, default));
        var first = await ResultAsync(f);
        f.Scope.Clock.Now += TimeSpan.FromSeconds(601);
        await f.Jobs().RecoverExpiredAsync(default);
        claim = (await f.Jobs().ClaimNextAsync(default))!;
        await RunAsync(f, claim, provider);
        var second = await ResultAsync(f);
        Assert.Equal(first.ShopProductId, second.ShopProductId); Assert.Equal(first.CheckedDate, second.CheckedDate);
        Assert.Equal(0, provider.Searches + provider.Fetches);
        Assert.Equal(ProductImportJobStatus.Partial, (await f.ReadAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task Invalid_offer_rolls_back_mapping_and_records_failed_check()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var source = Other(f);
        source = source with { Offer = new ProviderResult<ShopProductOffer>.Success(
            ((ProviderResult<ShopProductOffer>.Success)source.Offer!).Value with { Price = -1 }) };
        await RunAsync(f, claim, new ComparisonProvider(source));
        Assert.Equal(RetailerLookupStatus.CheckFailed, (await ResultAsync(f)).Status);
        Assert.False(await f.Scope.Db.ShopProducts.AnyAsync(p => p.ShopId == 2 && p.ShopProductCode == f.Scope.Code));
    }

    [PostgreSqlFact]
    public async Task Expired_promotion_forces_refresh_even_with_recent_check_time()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var provider = new ComparisonProvider(Other(f));
        await SeedMappingAsync(f, claim, provider.Source);
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopId == 2 && p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SpecialEndDate, f.Scope.Clock.Now.UtcDateTime.AddSeconds(-1)));
        await RunAsync(f, claim, provider);
        Assert.Equal(1, provider.Fetches); Assert.False((await ResultAsync(f)).IsFromCache);
    }

    [PostgreSqlFact]
    public async Task Exact_search_saves_shared_product_price_and_history()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var provider = new ComparisonProvider(Other(f));
        await RunAsync(f, claim, provider);
        var result = await ResultAsync(f);
        Assert.Equal(RetailerLookupStatus.Exact, result.Status); Assert.False(result.IsFromCache);
        var mapping = await f.Scope.Db.ShopProducts.SingleAsync(p => p.Id == result.ShopProductId);
        Assert.Equal((await f.ReadAsync()).ProductId, mapping.ProductId);
        Assert.Equal(19, (await f.Scope.Db.ShopProductPrices.SingleAsync(p => p.ShopProductId == mapping.Id)).Price);
        Assert.Single(await f.Scope.Db.ShopProductPriceHistory.Where(p => p.ShopProductId == mapping.Id).ToListAsync());
        Assert.Equal(1, provider.Searches); Assert.Equal(ProductImportJobStatus.Partial, (await f.ReadAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task Fresh_exact_cache_avoids_network_and_history_writes()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var provider = new ComparisonProvider(Other(f));
        await SeedMappingAsync(f, claim, provider.Source);
        await RunAsync(f, claim, provider);
        var result = await ResultAsync(f);
        Assert.True(result.IsFromCache); Assert.Equal(RetailerLookupStatus.Exact, result.Status);
        Assert.Equal(0, provider.Searches + provider.Fetches);
        Assert.Single(await f.Scope.Db.ShopProductPriceHistory.Where(p => p.ShopProductId == result.ShopProductId).ToListAsync());
    }

    [PostgreSqlFact]
    public async Task Stale_mapping_refreshes_known_url_without_search()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var source = Other(f);
        await SeedMappingAsync(f, claim, source);
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopId == 2 && p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddHours(-7)));
        var provider = new ComparisonProvider(source with { CheckedDate = f.Scope.Clock.Now,
            Offer = new ProviderResult<ShopProductOffer>.Success(((ProviderResult<ShopProductOffer>.Success)source.Offer!).Value with
            { Price = 18, CheckedDate = f.Scope.Clock.Now }) });
        await RunAsync(f, claim, provider);
        Assert.Equal(1, provider.Fetches); Assert.Equal(0, provider.Searches);
        Assert.False((await ResultAsync(f)).IsFromCache);
        Assert.Equal(18, (await f.Scope.Db.ShopProductPrices.SingleAsync(p => p.ShopProduct.ShopId == 2 && p.ShopProduct.ShopProductCode == f.Scope.Code)).Price);
    }

    [PostgreSqlFact]
    public async Task Likely_candidate_never_creates_canonical_mapping_or_price()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var source = Other(f);
        var provider = new ComparisonProvider(source with { Identity = source.Identity with { GTIN = null, PackQuantity = null, PackSize = null } });
        await RunAsync(f, claim, provider);
        var result = await ResultAsync(f);
        Assert.Equal(RetailerLookupStatus.Likely, result.Status); Assert.Null(result.ShopProductId);
        Assert.False(await f.Scope.Db.ShopProducts.AnyAsync(p => p.ShopId == 2 && p.ShopProductCode == f.Scope.Code));
    }

    [PostgreSqlFact]
    public async Task Ambiguous_exact_candidates_are_not_arbitrarily_selected()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var provider = new ComparisonProvider(Other(f)) { Duplicate = true };
        await RunAsync(f, claim, provider);
        var result = await ResultAsync(f);
        Assert.Equal(RetailerLookupStatus.CheckFailed, result.Status); Assert.Equal("ambiguous_exact_matches", result.ErrorCode);
        Assert.Null(result.ShopProductId);
    }

    [PostgreSqlFact]
    public async Task Empty_search_is_not_found_but_access_denial_is_unavailable_without_retry()
    {
        foreach (var denied in new[] { false, true })
        {
            await using var f = await ImportFixture.CreateAsync();
            var claim = await PrepareAsync(f);
            var provider = new ComparisonProvider(Other(f)) { Empty = !denied,
                Failure = denied ? new(ProviderFailureKind.AccessRestricted, "access_restricted", "Denied") : null };
            await RunAsync(f, claim, provider);
            Assert.Equal(denied ? RetailerLookupStatus.Unavailable : RetailerLookupStatus.NotFound, (await ResultAsync(f)).Status);
            Assert.Equal(ProductImportJobStatus.Partial, (await f.ReadAsync()).Status);
            Assert.NotNull((await f.ReadAsync()).ShoppingListProductId);
        }
    }

    [PostgreSqlFact]
    public async Task Transient_failures_retry_only_unfinished_checks_then_finish_at_limit()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var provider = new ComparisonProvider(Other(f)) { Failure = new(ProviderFailureKind.Timeout, "timeout", "Temporary") };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await RunAsync(f, claim, provider);
            Assert.Equal(attempt, provider.Searches);
            var job = await f.ReadAsync();
            Assert.NotNull(job.ShoppingListProductId);
            if (attempt == 3)
            { Assert.Equal(ProductImportJobStatus.Partial, job.Status); Assert.Equal(RetailerLookupStatus.Unavailable, (await ResultAsync(f)).Status); }
            else
            {
                Assert.Equal(ProductImportJobStatus.Queued, job.Status); Assert.Equal(RetailerLookupStatus.Pending, (await ResultAsync(f)).Status);
                f.Scope.Clock.Now += TimeSpan.FromMinutes(2);
                claim = (await f.Jobs().ClaimNextAsync(default))!;
            }
        }
        Assert.Single(await f.Scope.Db.ShoppingListProducts.Where(p => p.ShoppingListId == f.Scope.ListId).ToListAsync());
    }

    [PostgreSqlFact]
    public async Task Replacement_claim_during_search_fences_all_comparison_writes()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var replacement = Guid.NewGuid();
        var provider = new ComparisonProvider(Other(f)) { BeforeSearch = async () =>
            await f.Scope.Db.ProductImportJobs.Where(j => j.Id == claim.JobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.ClaimToken, replacement)) };
        await RunAsync(f, claim, provider);
        Assert.Equal(replacement, (await f.ReadAsync()).ClaimToken);
        Assert.Null((await ResultAsync(f)).ShopProductId);
        Assert.False(await f.Scope.Db.ShopProducts.AnyAsync(p => p.ShopId == 2 && p.ShopProductCode == f.Scope.Code));
    }

    [PostgreSqlFact]
    public async Task Expiry_during_search_prevents_comparison_writes_and_cancels_job()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await PrepareAsync(f);
        var provider = new ComparisonProvider(Other(f)) { BeforeSearch = async () =>
            await f.Scope.Db.UserAccounts.Where(u => u.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.ExpiresDate, f.Scope.Clock.Now.UtcDateTime.AddSeconds(-1))) };
        await RunAsync(f, claim, provider);
        Assert.Equal(ProductImportJobStatus.Cancelled, (await f.ReadAsync()).Status);
        Assert.False(await f.Scope.Db.ShopProducts.AnyAsync(p => p.ShopId == 2 && p.ShopProductCode == f.Scope.Code));
    }

    internal static async Task<ProductImportClaim> PrepareAsync(ImportFixture f)
    {
        var claim = (await f.Jobs().ClaimNextAsync(default))!;
        Assert.Equal(ProductPersistenceStatus.Success, (await f.Scope.Service.SaveAsync(claim.JobId, claim.UserAccountId, claim.Token, f.Scope.Source, default)).Status);
        return claim;
    }
    internal static ExtractedShopProduct Other(ImportFixture f)
    {
        var url = new Uri("https://www.woolworths.com.au/shop/productdetails/" + f.Scope.Code);
        return f.Scope.Source with { ShopCode = "woolworths", ProductUrl = url,
            Offer = new ProviderResult<ShopProductOffer>.Success(f.Scope.Offer with { ShopCode = "woolworths", SourceUrl = url, Price = 19 }) };
    }
    internal static ServiceProvider Services(ImportFixture f, IShopProductProvider provider) => new ServiceCollection()
        .AddLogging().AddSingleton<TimeProvider>(f.Scope.Clock).AddSingleton<IOptions<ProductImportOptions>>(Options.Create(f.Options))
        .AddScoped<MyShoppingListDbContext>(_ => PersistenceScope.Context()).AddScoped<ProductImportJobService>()
        .AddSingleton(new RetailerProviderRegistry([provider])).AddSingleton(new ProductUrlValidator(new RetailerCatalog()))
        .AddComparisonTests().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    internal static async Task RunAsync(ImportFixture f, ProductImportClaim claim, IShopProductProvider provider)
    {
        await using var services = Services(f, provider);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RetailerComparisonService>().RunAsync(claim, default);
    }
    internal static Task<ProductImportRetailerResult> ResultAsync(ImportFixture f) => f.Scope.Db.ProductImportRetailerResults.AsNoTracking()
        .SingleAsync(r => r.ProductImportJobId == f.Scope.Job.Id && r.ShopId == 2);
    internal static async Task SeedMappingAsync(ImportFixture f, ProductImportClaim claim, ExtractedShopProduct source)
    {
        await using var services = Services(f, new ComparisonProvider(source));
        await using var scope = services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<RetailerComparisonPersistenceService>().SaveExactAsync(claim, 2, source, default));
        await f.Scope.Db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == claim.JobId && r.ShopId == 2).ExecuteDeleteAsync();
    }
    internal sealed class ComparisonProvider(ExtractedShopProduct source) : IShopProductProvider
    {
        public ExtractedShopProduct Source { get; } = source;
        public string ShopCode => "woolworths";
        public int Searches { get; private set; }
        public int Fetches { get; private set; }
        public bool Duplicate { get; init; }
        public bool Empty { get; init; }
        public ProviderFailure? Failure { get; set; }
        public Func<Task>? BeforeSearch { get; init; }
        public async Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(ProductIdentity product, ShopLocationContext? location, CancellationToken token)
        {
            Searches++; if (BeforeSearch is not null) await BeforeSearch(); token.ThrowIfCancellationRequested();
            return Failure is not null ? new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure(Failure)
                : new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success(Empty ? [] : Duplicate ? [new(Source), new(Source)] : [new(Source)]);
        }
        public Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(Uri url, CancellationToken token)
        { Fetches++; return Task.FromResult<ProviderResult<ExtractedShopProduct>>(new ProviderResult<ExtractedShopProduct>.Success(Source)); }
        public Task<ProviderResult<ShopProductOffer>> GetOfferAsync(ShopProductSearchResult product, ShopLocationContext? location, CancellationToken token) => throw new NotSupportedException();
    }
}

internal static class ComparisonTestServices
{
    internal static IServiceCollection AddComparisonTests(this IServiceCollection services) => services
        .AddSingleton<ProductNormalisationService>().AddSingleton<ProductMatchingService>()
        .AddSingleton<IOptions<PriceOptions>>(Options.Create(new PriceOptions()))
        .AddScoped<ShopProductService>().AddScoped<PriceService>()
        .AddScoped<RetailerComparisonPersistenceService>().AddScoped<RetailerComparisonService>();
}
