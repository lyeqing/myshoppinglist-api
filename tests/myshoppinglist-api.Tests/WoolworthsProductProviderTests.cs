using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using Xunit.Abstractions;

namespace myshoppinglist_api.Tests;

public class WoolworthsProductProviderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Provider_extracts_source_without_database_or_search()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(RetailerHttpClientTests.Html(WoolworthsFixture.Html)));
        var provider = Provider(RetailerHttpClientTests.Client(handler));
        var result = await provider.GetProductFromUrlAsync(WoolworthsProductParserTests.Url, CancellationToken.None);
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(result);
        Assert.Single(handler.Requests);
        var search = Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure>(
            await provider.SearchAsync(new() { Name = "Coca-Cola" }, null, CancellationToken.None));
        Assert.Equal(ProviderFailureKind.NotSupported, search.Error.Kind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://www.woolworths.com.au/browse/drinks")]
    [InlineData("https://www.coles.com.au/product/example-32731")]
    public async Task Non_product_or_other_retailer_urls_never_fetch(string url)
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Must not fetch."));
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await Provider(RetailerHttpClientTests.Client(handler))
            .GetProductFromUrlAsync(new(url), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Requested_location_is_not_silently_replaced_with_anonymous_price()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Must not fetch."));
        var candidate = new ShopProductSearchResult(new()
        {
            ShopCode = "woolworths", ProductUrl = WoolworthsProductParserTests.Url, Identity = new() { Name = "Coca-Cola" },
            SourceType = myshoppinglist_api.Models.SourceType.StructuredData, CheckedDate = WoolworthsProductParserTests.Checked
        });
        var result = Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(await Provider(RetailerHttpClientTests.Client(handler))
            .GetOfferAsync(candidate, new() { ShopCode = "woolworths", StoreCode = "123" }, CancellationToken.None));
        Assert.Equal(ProviderFailureKind.NotSupported, result.Error.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Offer_refresh_rejects_changed_gtin()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(RetailerHttpClientTests.Html(WoolworthsFixture.Html)));
        var candidate = new ShopProductSearchResult(new()
        {
            ShopCode = "woolworths", ProductUrl = WoolworthsProductParserTests.Url, Identity = new() { Name = "Product", GTIN = "different" },
            SourceType = myshoppinglist_api.Models.SourceType.StructuredData, CheckedDate = WoolworthsProductParserTests.Checked
        });
        var result = Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(await Provider(RetailerHttpClientTests.Client(handler))
            .GetOfferAsync(candidate, null, CancellationToken.None));
        Assert.Equal("offer_identity_changed", result.Error.Code);
    }

    [LiveWoolworthsFact]
    public async Task Live_public_page_can_be_extracted_using_safe_transport()
    {
        var catalog = new RetailerCatalog();
        var connection = new SafeRetailerConnection(catalog);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectCallback = (context, token) => connection.ConnectAsync(context.DnsEndPoint, token)
        };
        var client = new RetailerHttpClient(new TestHttpClientFactory(handler), new(catalog), Options.Create(new RetailerHttpOptions()),
            TimeProvider.System, NullLogger<RetailerHttpClient>.Instance);
        var result = await Provider(client).GetProductFromUrlAsync(WoolworthsProductParserTests.Url, CancellationToken.None);
        if (result is ProviderResult<ExtractedShopProduct>.Failure error)
            output.WriteLine($"Live result: {error.Error.Kind} / {error.Error.Code}");
        var product = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(result).Value;
        Assert.Equal("32731", product.ShopProductCode);
        Assert.NotEmpty(product.Identity.Name);
        var offer = Assert.IsType<ProviderResult<ShopProductOffer>.Success>(product.Offer).Value;
        Assert.Equal("AUD", offer.Currency);
        output.WriteLine($"Live product {product.ShopProductCode}: price {offer.Price} {offer.Currency}; scope {offer.PriceScope}; checked {offer.CheckedDate:O}");
    }

    private static WoolworthsProductProvider Provider(RetailerHttpClient client) =>
        new(client, new(), new(new RetailerCatalog()), NullLogger<WoolworthsProductProvider>.Instance);
}

public sealed class LiveWoolworthsFactAttribute : FactAttribute
{
    public LiveWoolworthsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYSHOPPINGLIST_LIVE_WOOLWORTHS") != "1")
            Skip = "Opt-in only: set MYSHOPPINGLIST_LIVE_WOOLWORTHS=1 to fetch a public Woolworths page.";
    }
}
