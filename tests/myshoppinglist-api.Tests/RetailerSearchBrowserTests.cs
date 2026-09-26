using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using Xunit.Abstractions;
using Microsoft.Playwright;
using myshoppinglist_api.Providers;

namespace myshoppinglist_api.Tests;

public class RetailerSearchBrowserTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("https://www.coles.com.au/product/coca-cola-1.25l-123011", true)]
    [InlineData("https://coles.com.au/product/123011", true)]
    [InlineData("https://www.coles.com.au/product/another-999", false)]
    [InlineData("https://www.coles.com.au/search/products", false)]
    [InlineData("https://www.coles.com.au/account", false)]
    [InlineData("https://www.coles.com.au/cart", false)]
    [InlineData("https://www.coles.com.au.evil.example/product/123011", false)]
    [InlineData("http://www.coles.com.au/product/123011", false)]
    [InlineData("https://www.coles.com.au:444/product/123011", false)]
    [InlineData("https://user@www.coles.com.au/product/123011", false)]
    public void Product_navigation_is_opt_in_and_identity_bound(string url, bool allowed)
    {
        var product = new Uri("https://www.coles.com.au/product/123011");
        Assert.Equal(allowed, RetailerBrowserNetworkGuard.IsAllowedColesProductRequest(product, url, "GET", "document", true));
        Assert.False(RetailerBrowserNetworkGuard.IsAllowedColesProductRequest(product, url, "POST", "document", true));
        if (!url.EndsWith("/search/products"))
            Assert.False(RetailerBrowserNetworkGuard.IsAllowedRequest("coles", url, "GET", "document", true));
        Assert.True(RetailerBrowserNetworkGuard.IsAllowedRequest("coles", "https://www.coles.com.au/search/products", "GET", "document", true));
        Assert.True(RetailerBrowserNetworkGuard.IsAllowedRequest("woolworths", "https://www.woolworths.com.au/shop/search/products", "GET", "document", true));
        Assert.False(RetailerBrowserNetworkGuard.IsAllowedColesProductRequest(product, "https://localhost/script.js", "GET", "script", false));
    }

    [Fact]
    public async Task Disabled_invalid_and_cancelled_searches_never_launch_a_browser()
    {
        using var disabled = Browser(new() { Enabled = false });
        Assert.Equal(ProviderFailureKind.NotSupported, Assert.IsType<ProviderResult<RetailerPage>.Failure>(
            await disabled.ReadAsync("coles", "drink", CancellationToken.None)).Error.Kind);
        using var browser = Browser(new());
        Assert.Equal(ProviderFailureKind.InvalidProduct, Assert.IsType<ProviderResult<RetailerPage>.Failure>(
            await browser.ReadAsync("evil", "drink", CancellationToken.None)).Error.Kind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.ReadAsync("coles", "drink", new(true)));
    }

    [LocalSearchBrowserTheory]
    [InlineData("coles")]
    [InlineData("woolworths")]
    public async Task Controlled_browser_reads_rendered_and_shadow_dom_links_without_external_network(string shop)
    {
        var html = shop == "coles" ? SearchFixtures.Coles.Replace("<a href", "<a class='product__link' href") : """
            <h1>Results</h1><section data-testid="search-results-product-scrollable-content"><wc-product-tile></wc-product-tile></section>
            <a href="/shop/productdetails/999/outside-results">Recommendation</a>
            <script>document.querySelector('wc-product-tile').attachShadow({mode:'open'}).innerHTML =
                '<a href="/shop/productdetails/32731/coca-cola-classic-soft-drink-bottle">Coca-Cola Classic 1.25L</a>';</script>
            """;
        await WithControlledPage(shop, html, async (service, page, url) =>
        {
            var result = Assert.IsType<ProviderResult<RetailerPage>.Success>(await service.ReadPageAsync(page, shop, url, 5000)).Value;
            var parsed = shop == "coles" ? await new ColesSearchParser().ParseAsync(result, "coca-cola", 5, CancellationToken.None)
                : await new WoolworthsSearchParser().ParseAsync(result, "coca-cola", 5, CancellationToken.None);
            var links = Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Success>(parsed).Value;
            Assert.Equal(shop == "coles" ? 2 : 1, links.Count);
            Assert.DoesNotContain(links, l => l.AbsolutePath.Contains("999"));
        });
    }

    [LocalSearchBrowserTheory]
    [InlineData("challenge")]
    [InlineData("loading")]
    [InlineData("empty")]
    public async Task Controlled_browser_distinguishes_challenge_loading_and_true_empty_results(string scenario)
    {
        var html = scenario switch
        {
            "challenge" => "<iframe src='/_Incapsula_Resource'></iframe>",
            "empty" => "<h1>No results for &quot;coca-cola&quot;</h1>",
            _ => "<p>Loading...</p>"
        };
        await WithControlledPage("coles", html, async (service, page, url) =>
        {
            if (scenario == "loading")
            { await Assert.ThrowsAsync<TimeoutException>(() => service.ReadPageAsync(page, "coles", url, 300)); return; }
            var result = await service.ReadPageAsync(page, "coles", url, 5000);
            if (scenario == "challenge")
                Assert.Equal(ProviderFailureKind.AccessRestricted, Assert.IsType<ProviderResult<RetailerPage>.Failure>(result).Error.Kind);
            else
            {
                var parsed = await new ColesSearchParser().ParseAsync(Assert.IsType<ProviderResult<RetailerPage>.Success>(result).Value,
                    "coca-cola", 5, CancellationToken.None);
                Assert.Empty(Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Success>(parsed).Value);
            }
        });
    }

    private static async Task WithControlledPage(string shop, string html, Func<RetailerSearchBrowser, IPage, Uri, Task> verify)
    {
        using var service = Browser(new());
        using var playwright = await Playwright.CreateAsync();
        await using var guard = new RetailerBrowserNetworkGuard(shop, new(), CancellationToken.None,
            // Chromium may preconnect before request interception. An empty fake DNS result
            // rejects those sockets too, without performing any external DNS or TCP work.
            (_, _) => Task.FromResult(Array.Empty<System.Net.IPAddress>()),
            (_, _) => throw new InvalidOperationException("Controlled browser tests must never connect to a retailer."));
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true, Channel = Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_BROWSER_CHANNEL"),
            ChromiumSandbox = true, Proxy = new() { Server = guard.ProxyUrl },
            Args = ["--proxy-bypass-list=<-loopback>", "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"]
        });
        await using var context = await browser.NewContextAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block });
        var url = ProductSearchQueryBuilder.SearchUrl(shop, "coca-cola");
        await context.RouteAsync("**/*", async route =>
        {
            if (route.Request.Url == url.AbsoluteUri) await route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = html });
            else await route.AbortAsync();
        });
        await context.RouteWebSocketAsync("**/*", _ => { });
        var page = await context.NewPageAsync();
        await verify(service, page, url);
    }

    [LiveRetailerSearchTheory]
    [InlineData("coles")]
    [InlineData("woolworths")]
    public async Task Live_search_renders_verifiable_candidates_through_guarded_browser(string shop)
    {
        using var browser = Browser(new() { BrowserChannel = "chrome" });
        var result = await browser.ReadAsync(shop, "coca-cola", CancellationToken.None);
        if (result is ProviderResult<RetailerPage>.Failure failed) output.WriteLine($"Live {shop}: {failed.Error.Kind} / {failed.Error.Code}");
        var page = Assert.IsType<ProviderResult<RetailerPage>.Success>(result).Value;
        var parsed = shop == "coles" ? await new ColesSearchParser().ParseAsync(page, "coca-cola", 5, CancellationToken.None)
            : await new WoolworthsSearchParser().ParseAsync(page, "coca-cola", 5, CancellationToken.None);
        if (parsed is ProviderResult<IReadOnlyList<Uri>>.Failure failure) output.WriteLine($"Live {shop}: {failure.Error.Kind} / {failure.Error.Code}");
        var links = Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Success>(parsed).Value;
        Assert.NotEmpty(links); Assert.InRange(links.Count, 1, 5);
        output.WriteLine($"Live {shop}: {links.Count} verified product links");
    }

    private static RetailerSearchBrowser Browser(RetailerSearchOptions options) => new(Options.Create(options), TimeProvider.System, NullLogger<RetailerSearchBrowser>.Instance);
}

public sealed class LiveRetailerSearchTheoryAttribute : TheoryAttribute
{
    public LiveRetailerSearchTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYSHOPPINGLIST_LIVE_SEARCH") != "1")
            Skip = "Opt-in only: MYSHOPPINGLIST_LIVE_SEARCH=1 uses installed Chrome to read public retailer searches.";
    }
}

public sealed class LocalSearchBrowserTheoryAttribute : TheoryAttribute
{
    public LocalSearchBrowserTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYSHOPPINGLIST_BROWSER_TESTS") != "1")
            Skip = "Set MYSHOPPINGLIST_BROWSER_TESTS=1 to run controlled Chromium tests without retailer network access.";
    }
}
