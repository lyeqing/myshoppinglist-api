using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using Xunit.Abstractions;

namespace myshoppinglist_api.Tests;

public class ColesProductBrowserTests(ITestOutputHelper output)
{
    private static ColesProductBrowser Browser(RetailerSearchOptions? options = null) =>
        new(Options.Create(options ?? new()), TimeProvider.System, NullLogger<ColesProductBrowser>.Instance);

    [Fact]
    public async Task Invalid_disabled_and_cancelled_requests_do_not_launch()
    {
        using var browser = Browser();
        Assert.Equal(ProviderFailureKind.InvalidUrl, Assert.IsType<ProviderResult<RetailerPage>.Failure>(
            await browser.ReadAsync(new("https://localhost/product/test-123"), default)).Error.Kind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.ReadAsync(ColesProductParserTests.Url, new(true)));
        using var disabled = Browser(new() { Enabled = false });
        Assert.Equal(ProviderFailureKind.NotSupported, Assert.IsType<ProviderResult<RetailerPage>.Failure>(
            await disabled.ReadAsync(ColesProductParserTests.Url, default)).Error.Kind);
    }

    [LocalSearchBrowserTheory]
    [InlineData("product")]
    [InlineData("challenge")]
    [InlineData("oversize")]
    [InlineData("loading")]
    [InlineData("wrong-product")]
    public async Task Controlled_page_preserves_parser_data_and_rejects_unusable_pages(string scenario)
    {
        var html = scenario switch
        {
            "challenge" => "<iframe src='/_Incapsula_Resource'></iframe>",
            "oversize" => "<script type='application/ld+json'>" + new string('x', 2000001) + "</script>",
            "loading" => "<p>Loading...</p>",
            "wrong-product" => ColesProductParserTests.Fixture.Replace("1849307", "9999999"),
            _ => ColesProductParserTests.Fixture
        };
        using var service = Browser();
        using var playwright = await Playwright.CreateAsync();
        await using var guard = new RetailerBrowserNetworkGuard("coles", new(), default,
            (_, _) => Task.FromResult(Array.Empty<System.Net.IPAddress>()),
            (_, _) => throw new InvalidOperationException("No external connections allowed."));
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true, Channel = Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_BROWSER_CHANNEL"),
            ChromiumSandbox = true, Proxy = new() { Server = guard.ProxyUrl },
            Args = ["--proxy-bypass-list=<-loopback>", "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"]
        });
        await using var context = await browser.NewContextAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block });
        await context.RouteAsync("**/*", async route =>
        {
            if (route.Request.Url == ColesProductParserTests.Url.AbsoluteUri)
                await route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = html });
            else await route.AbortAsync();
        });
        await context.RouteWebSocketAsync("**/*", _ => { });
        var page = await context.NewPageAsync();
        if (scenario == "loading")
        {
            await Assert.ThrowsAsync<TimeoutException>(() => service.ReadPageAsync(page, ColesProductParserTests.Url, 300));
            return;
        }
        var result = await service.ReadPageAsync(page, ColesProductParserTests.Url, 5000);
        if (scenario is "challenge" or "oversize")
        {
            Assert.Equal(scenario == "challenge" ? "retailer_access_restricted" : "product_snapshot_too_large",
                Assert.IsType<ProviderResult<RetailerPage>.Failure>(result).Error.Code);
            return;
        }
        var snapshot = Assert.IsType<ProviderResult<RetailerPage>.Success>(result).Value;
        var parsed = await new ColesProductParser().ParseAsync(snapshot, default);
        if (scenario == "wrong-product")
        {
            Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(parsed);
            return;
        }
        var product = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(parsed).Value;
        Assert.Equal("1849307", product.ShopProductCode);
        var offer = Assert.IsType<ProviderResult<ShopProductOffer>.Success>(product.Offer).Value;
        Assert.Equal(23m, offer.Price);
        Assert.Equal(myshoppinglist_api.Models.PriceScope.Unknown, offer.PriceScope);
        Assert.Null(offer.Location);
    }

    [LiveColesProductBrowserTheory]
    [InlineData("coles-kitchen-supreme-pizza-445g-1435461?pid=meals-hub_productlist_oven-faves", "1435461")]
    [InlineData("coles-kitchen-limited-edition-chicken-kyiv-pizza-500g-1435392", "1435392")]
    [InlineData("coca-cola-classic-soft-drink-bottle-1.25l-123011", "123011")]
    public async Task Live_product_is_verified_through_guarded_browser(string path, string code)
    {
        using var browser = Browser(new() { BrowserChannel = "chrome" });
        var result = await browser.ReadAsync(new("https://www.coles.com.au/product/" + path), default);
        if (result is ProviderResult<RetailerPage>.Failure failure)
            output.WriteLine($"Coles {code}: {failure.Error.Kind} / {failure.Error.Code}");
        var snapshot = Assert.IsType<ProviderResult<RetailerPage>.Success>(result).Value;
        var parsed = await new ColesProductParser().ParseAsync(snapshot, default);
        if (parsed is ProviderResult<ExtractedShopProduct>.Failure error)
            output.WriteLine($"Coles {code}: {error.Error.Kind} / {error.Error.Code}");
        var product = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(parsed).Value;
        Assert.Equal(code, product.ShopProductCode);
        output.WriteLine($"Coles {code}: {product.Identity.Name}");
        if (product.Offer is ProviderResult<ShopProductOffer>.Success offer)
        {
            Assert.Equal(myshoppinglist_api.Models.PriceScope.Unknown, offer.Value.PriceScope);
            Assert.Null(offer.Value.Location);
            output.WriteLine($"Price: {offer.Value.Price}; promotion: {offer.Value.SpecialDescription}");
        }
        else output.WriteLine("No verified single-pack offer.");
    }
}

public sealed class LiveColesProductBrowserTheoryAttribute : TheoryAttribute
{
    public LiveColesProductBrowserTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYSHOPPINGLIST_LIVE_COLES_BROWSER") != "1")
            Skip = "Opt-in: MYSHOPPINGLIST_LIVE_COLES_BROWSER=1 reads three public Coles products using guarded Chrome.";
    }
}
