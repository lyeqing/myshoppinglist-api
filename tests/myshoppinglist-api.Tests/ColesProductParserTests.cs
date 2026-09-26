using System.Text.Json.Nodes;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Tests;

public class ColesProductParserTests
{
    [Theory]
    [InlineData("https://www.coles.com.au/product/coca-cola-classic-soft-drink-bottle-1.25l-123011", "123011")]
    [InlineData("https://www.coles.com.au/product/123011", "123011")]
    [InlineData("https://evil.example/product/coca-cola-1.25l-123011", null)]
    [InlineData("https://www.coles.com.au/product/../account-123011", null)]
    public void Decimal_sizes_in_current_Coles_product_slugs_are_supported(string url, string? code) =>
        Assert.Equal(code, ColesProductParser.ProductCode(new(url)));

    internal static readonly Uri Url = new("https://www.coles.com.au/product/coca-cola-classic-soft-drink-multipack-cans-375ml-10-pack-1849307");
    internal static readonly DateTimeOffset Checked = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    internal static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Coles", "coles-1849307.html"));

    [Fact]
    public async Task Observed_main_product_is_selected_without_using_variation_or_multibuy_price()
    {
        var product = Success(await Parse(Fixture));
        Assert.Equal("1849307", product.ShopProductCode);
        Assert.Equal("9300675014779", product.Identity.GTIN);
        Assert.Equal("Coca-Cola", product.Identity.Brand);
        Assert.Contains("10 Pack", product.Identity.Name);
        Assert.Null(product.Identity.PackQuantity); // Actual source description contradicts its title.
        Assert.Equal(375m, product.Identity.PackSize);
        Assert.Equal("mL", product.Identity.PackUnit);
        var offer = Assert.IsType<ProviderResult<ShopProductOffer>.Success>(product.Offer).Value;
        Assert.Equal(23m, offer.Price);
        Assert.Equal("Pick any 2 for $23", offer.SpecialDescription);
        Assert.Equal(PriceScope.Unknown, offer.PriceScope);
        Assert.Equal("1 L", offer.UnitPriceUnit);
        Assert.Null(offer.Location);
        Assert.Equal(Checked, offer.CheckedDate);
    }

    [Fact]
    public async Task Matching_title_and_pack_data_produce_a_known_quantity()
    {
        var product = Success(await Parse(Fixture.Replace("24 x 375mL cans.", "10 x 375mL cans.")));
        Assert.Equal(10, product.Identity.PackQuantity);
    }

    [Fact]
    public async Task Json_ld_only_is_usable_when_embedded_state_is_missing()
    {
        var product = Success(await Parse(Html(Ld())));
        Assert.Equal(10, product.Identity.PackQuantity);
        Assert.Equal(23m, Assert.IsType<ProviderResult<ShopProductOffer>.Success>(product.Offer).Value.Price);
        Assert.Equal(SourceType.StructuredData, product.SourceType);
    }

    [Fact]
    public async Task Embedded_product_is_a_fallback_for_malformed_json_ld()
    {
        var html = "<script type='application/ld+json'>{invalid</script>" + Html(null, Next());
        var product = Success(await Parse(html));
        Assert.Equal("1849307", product.ShopProductCode);
        Assert.Equal(SourceType.RetailerPage, product.SourceType);
        Assert.Equal(23m, Assert.IsType<ProviderResult<ShopProductOffer>.Success>(product.Offer).Value.Price);
    }

    [Fact]
    public async Task Json_ld_graph_ignores_another_product()
    {
        var other = Ld(); other["sku"] = "7365777"; other["@id"] = "https://www.coles.com.au/product/other-7365777";
        var graph = new JsonObject { ["@graph"] = new JsonArray(other, Ld()) };
        Assert.Equal("1849307", Success(await Parse(Html(graph))).ShopProductCode);
    }

    [Fact]
    public async Task Conflicting_price_does_not_destroy_product_identity()
    {
        var next = Next(); next["props"]!["pageProps"]!["product"]!["pricing"]!["now"] = 99;
        var product = Success(await Parse(Html(Ld(), next)));
        Assert.Equal("price_conflict", Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(product.Offer).Error.Code);
    }

    [Fact]
    public async Task Conflicting_gtins_are_left_unresolved()
    {
        var next = Next(); next["props"]!["pageProps"]!["product"]!["gtin"] = "9300675000000";
        Assert.Null(Success(await Parse(Html(Ld(), next))).Identity.GTIN);
    }

    [Fact]
    public async Task Missing_price_is_an_explicit_offer_failure()
    {
        var ld = Ld(); ld.Remove("offers");
        var product = Success(await Parse(Html(ld)));
        Assert.Equal("price_unavailable", Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(product.Offer).Error.Code);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("1,200")]
    public async Task Invalid_price_is_not_treated_as_a_usable_offer(string value)
    {
        var ld = Ld(); ld["offers"]![0]!["price"] = value;
        Assert.Equal("invalid_price", Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(Success(await Parse(Html(ld))).Offer).Error.Code);
    }

    [Fact]
    public async Task Wrong_primary_embedded_product_is_rejected()
    {
        var next = Next(); next["props"]!["pageProps"]!["product"]!["id"] = 7365777;
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await Parse(Html(Ld(), next)));
    }

    [Fact]
    public async Task Duplicate_main_products_are_rejected() =>
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await Parse(Html(new JsonArray(Ld(), Ld()))));

    [Fact]
    public async Task Missing_metadata_remains_optional()
    {
        var ld = Ld(); ld.Remove("brand"); ld.Remove("gtin"); ld.Remove("image");
        var product = Success(await Parse(Html(ld)));
        Assert.Null(product.Identity.Brand);
        Assert.Null(product.Identity.GTIN);
        Assert.Null(product.ImageUrl);
    }

    [Fact]
    public async Task Brand_alone_is_not_a_product_name()
    {
        var next = Next(); next["props"]!["pageProps"]!["product"]!.AsObject().Remove("name");
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await Parse(Html(null, next)));
    }

    [Fact]
    public async Task Access_restriction_is_not_reported_as_product_not_found()
    {
        var failure = Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await Parse("<title>Access Denied</title>"));
        Assert.Equal(ProviderFailureKind.AccessRestricted, failure.Error.Kind);
        Assert.False(failure.Error.IsRetryable);
    }

    [Fact]
    public async Task Parser_respects_cancellation() =>
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ColesProductParser().ParseAsync(
            new(Url, Fixture, Checked), new CancellationToken(true)));

    [Theory]
    [InlineData("/_Incapsula_Resource?incident_id=example")]
    [InlineData("https://www.coles.com.au/_Incapsula_Resource?incident_id=example")]
    public async Task Http_200_challenge_shell_is_access_restricted(string source)
    {
        var html = $"<html><head><meta name='robots' content='noindex,nofollow'></head><body><iframe id='main-iframe' src='{source}'>Request unsuccessful. Incapsula incident ID: example</iframe></body></html>";
        var page = new RetailerPage(new("https://www.coles.com.au/product/coles-kitchen-supreme-pizza-445g-1435461?pid=meals-hub_productlist_oven-faves"), html, Checked);
        var failure = Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await new ColesProductParser().ParseAsync(page, default));
        Assert.Equal(ProviderFailureKind.AccessRestricted, failure.Error.Kind);
        Assert.Equal("retailer_access_restricted", failure.Error.Code);
        Assert.False(failure.Error.IsRetryable);
    }

    [Fact]
    public async Task Security_script_on_real_product_page_is_not_a_block()
    {
        var html = Html(Ld()) + "<script src='/_Incapsula_Resource?script=example'></script>";
        Assert.Equal("1849307", Success(await Parse(html)).ShopProductCode);
    }

    [Fact]
    public async Task Missing_product_without_challenge_keeps_its_original_failure()
    {
        var failure = Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await Parse("<html><body>No product data</body></html>"));
        Assert.Equal("product_not_identified", failure.Error.Code);
    }

    internal static JsonObject Ld() => JsonNode.Parse("""
        {"@type":"Product","@id":"https://www.coles.com.au/product/example-1849307","sku":1849307,
         "name":"Coca-Cola Classic Cans 10 x 375mL","brand":{"name":"Coca-Cola"},"gtin":"9300675014779",
         "offers":[{"price":23,"priceCurrency":"AUD","availability":"https://schema.org/InStock"}]}
        """)!.AsObject();
    internal static JsonObject Next() => JsonNode.Parse("""
        {"props":{"pageProps":{"product":{"id":1849307,"name":"Classic Cans 375ml","brand":"Coca-Cola",
         "size":"10 Pack","gtin":"9300675014779","pricing":{"now":23},"availability":true}}}}
        """)!.AsObject();
    internal static string Html(JsonNode? ld, JsonNode? next = null) =>
        (ld is null ? "" : $"<script type='application/ld+json'>{ld.ToJsonString()}</script>")
        + (next is null ? "" : $"<script id='__NEXT_DATA__' type='application/json'>{next.ToJsonString()}</script>");
    private static Task<ProviderResult<ExtractedShopProduct>> Parse(string html) =>
        new ColesProductParser().ParseAsync(new(Url, html, Checked), CancellationToken.None);
    private static ExtractedShopProduct Success(ProviderResult<ExtractedShopProduct> result) =>
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(result).Value;
}
