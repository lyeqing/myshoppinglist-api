using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Tests;

public class RetailerSearchProviderTests
{
    [Theory]
    [InlineData("coles")]
    [InlineData("woolworths")]
    public async Task Candidates_are_verified_using_product_pages_and_are_not_claimed_to_be_matches(string shop)
    {
        var url = shop == "coles" ? ColesProductParserTests.Url : WoolworthsProductParserTests.Url;
        var html = shop == "coles" ? ColesProductParserTests.Fixture : WoolworthsFixture.Html;
        var browser = new FakeSearchBrowser(shop, url);
        var handler = new RecordingHandler((_, _) => Task.FromResult(RetailerHttpClientTests.Html(html)));
        var result = Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success>(
            await Provider(shop, handler, browser).SearchAsync(new() { Name = "Coca-Cola" }, null, CancellationToken.None));
        var candidate = Assert.Single(result.Value).Product;
        Assert.NotNull(candidate.Identity.GTIN); Assert.Equal(shop, candidate.ShopCode);
        Assert.Single(handler.Requests); Assert.Equal(url, handler.Requests[0]); Assert.Equal(1, browser.Calls);
    }
    [Theory]
    [InlineData("coles")]
    [InlineData("woolworths")]
    public async Task Restrictions_and_candidate_read_failures_are_not_reported_as_no_matches(string shop)
    {
        var browser = new FakeSearchBrowser(shop, shop == "coles" ? ColesProductParserTests.Url : WoolworthsProductParserTests.Url);
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)));
        var provider = Provider(shop, handler, browser);
        var failure = Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure>(await provider.SearchAsync(new() { Name = "Coca-Cola" }, null, CancellationToken.None));
        Assert.Equal(ProviderFailureKind.RateLimited, failure.Error.Kind);
        browser.Error = new(ProviderFailureKind.AccessRestricted, "restricted", "Unavailable");
        Assert.Equal(ProviderFailureKind.AccessRestricted, Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure>(
            await provider.SearchAsync(new() { Name = "Coca-Cola" }, null, CancellationToken.None)).Error.Kind);
        Assert.Single(handler.Requests);
    }
    [Theory]
    [InlineData("coles")]
    [InlineData("woolworths")]
    public async Task Unsupported_location_invalid_query_and_cancelled_calls_never_start_browser(string shop)
    {
        var browser = new FakeSearchBrowser(shop, ColesProductParserTests.Url);
        var provider = Provider(shop, new RecordingHandler((_, _) => throw new InvalidOperationException()), browser);
        Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure>(await provider.SearchAsync(new() { Name = "Coca-Cola" }, new() { ShopCode = shop }, CancellationToken.None));
        Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure>(await provider.SearchAsync(new() { Name = " " }, null, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(new() { Name = "Coca-Cola" }, null, new(true)));
        Assert.Equal(0, browser.Calls);
    }
    [Theory]
    [InlineData("coles")]
    [InlineData("woolworths")]
    public async Task Candidate_count_and_simultaneous_reads_are_bounded(string shop)
    {
        var browser = new FakeSearchBrowser(shop, new(shop == "coles" ? "https://www.coles.com.au/product/test-1" : "https://www.woolworths.com.au/shop/productdetails/1/test")) { Many = true };
        var active = 0; var peak = 0; var count = 0;
        var handler = new RecordingHandler(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active); Interlocked.Increment(ref count);
            Interlocked.Exchange(ref peak, Math.Max(Volatile.Read(ref peak), current));
            await Task.Delay(20, token); Interlocked.Decrement(ref active);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        var result = await Provider(shop, handler, browser).SearchAsync(new() { Name = "Coca-Cola" }, null, CancellationToken.None);
        Assert.Empty(Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success>(result).Value);
        Assert.Equal(5, count); Assert.InRange(peak, 1, 2);
    }

    private static IShopProductProvider Provider(string shop, HttpMessageHandler handler, IRetailerSearchBrowser browser)
    {
        var http = RetailerHttpClientTests.Client(handler); var validator = new myshoppinglist_api.Security.ProductUrlValidator(new());
        return shop == "coles" ? new ColesProductProvider(http, new(), validator, NullLogger<ColesProductProvider>.Instance, browser)
            : new WoolworthsProductProvider(http, new(), validator, NullLogger<WoolworthsProductProvider>.Instance, browser);
    }
    private sealed class FakeSearchBrowser(string shop, Uri productUrl) : IRetailerSearchBrowser
    {
        public int Calls { get; private set; }
        public bool Many { get; init; }
        public ProviderFailure? Error { get; set; }
        public Task<ProviderResult<RetailerPage>> ReadAsync(string shopCode, string query, CancellationToken token)
        {
            Calls++; token.ThrowIfCancellationRequested();
            if (Error is not null) return Task.FromResult<ProviderResult<RetailerPage>>(new ProviderResult<RetailerPage>.Failure(Error));
            var container = shop == "coles" ? "class='coles-targeting-search-content-container'" : "data-testid='search-results-product-scrollable-content'";
            var links = Many ? string.Join("", Enumerable.Range(1, 20).Select(i => $"<a href='{productUrl.AbsoluteUri.Replace("1", i.ToString())}'>Coca-Cola</a>"))
                : $"<a href='{productUrl}'>Coca-Cola</a>";
            return Task.FromResult<ProviderResult<RetailerPage>>(new ProviderResult<RetailerPage>.Success(new(ProductSearchQueryBuilder.SearchUrl(shopCode, query), $"<section {container}>{links}</section>", DateTimeOffset.UtcNow)));
        }
    }
}
