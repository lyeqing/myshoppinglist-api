using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Providers.Woolworths;

namespace myshoppinglist_api.Tests;

public class WoolworthsProductParserTests
{
    internal static readonly Uri Url = new("https://www.woolworths.com.au/shop/productdetails/32731/coca-cola-classic-soft-drink-bottle");
    internal static readonly DateTimeOffset Checked = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static Task<ProviderResult<ExtractedShopProduct>> Parse(string html) => new WoolworthsProductParser().ParseAsync(new(Url, html, Checked), CancellationToken.None);
    private static ExtractedShopProduct Success(ProviderResult<ExtractedShopProduct> result) => Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(result).Value;
    private static string Change(Action<JsonObject, JsonObject> change, bool state = true)
    {
        using var doc = new HtmlParser().ParseDocument(WoolworthsFixture.Html);
        var ld = JsonNode.Parse(doc.QuerySelector("script[type='application/ld+json']")!.TextContent)!.AsObject();
        var next = JsonNode.Parse(doc.QuerySelector("script#__NEXT_DATA__")!.TextContent)!.AsObject();
        change(ld, next["props"]!["pageProps"]!["pdDetails"]!["Product"]!.AsObject());
        return $"<script type='application/ld+json'>{ld.ToJsonString()}</script>" + (state ? $"<script id='__NEXT_DATA__'>{next.ToJsonString()}</script>" : "");
    }
    [Fact]
    public async Task Observed_page_preserves_gtin_price_and_explicit_unit_measure()
    {
        var product = Success(await Parse(WoolworthsFixture.Html));
        Assert.Equal("32731", product.ShopProductCode); Assert.Equal("9300675001113", product.Identity.GTIN);
        Assert.Equal("Coca-Cola", product.Identity.Brand); Assert.Equal(1250, product.Identity.PackSize);
        var offer = Assert.IsType<ProviderResult<ShopProductOffer>.Success>(product.Offer).Value;
        Assert.Equal(2.25m, offer.Price); Assert.Equal(4.5m, offer.NormalPrice); Assert.Equal(1.8m, offer.UnitPrice);
        Assert.Equal("1L", offer.UnitPriceUnit); Assert.Equal(PriceScope.Unknown, offer.PriceScope); Assert.Null(offer.Location);
        Assert.Equal(Checked, offer.CheckedDate);
    }
    [Theory]
    [InlineData("Stockcode", "999")]
    [InlineData("Barcode", "9300675014779")]
    public async Task Conflicting_identity_is_rejected(string key, string value)
    {
        var result = await Parse(Change((_, p) => p[key] = value));
        Assert.Equal("product_identity_conflict", Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(result).Error.Code);
    }
    [Theory]
    [InlineData("Price", "99")]
    [InlineData("Price", "-1")]
    [InlineData("Unit", "Kg")]
    public async Task Unreliable_prices_do_not_destroy_product_identity(string key, string value)
    {
        var product = Success(await Parse(Change((_, p) => p[key] = value)));
        Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(product.Offer);
    }
    [Fact]
    public async Task Json_ld_only_does_not_mislabel_package_size_as_unit_price_measure()
    {
        var product = Success(await Parse(Change((_, _) => { }, false)));
        var offer = Assert.IsType<ProviderResult<ShopProductOffer>.Success>(product.Offer).Value;
        Assert.Null(offer.UnitPrice); Assert.Null(offer.UnitPriceUnit);
    }
    [Fact]
    public async Task Multipack_and_multibuy_remain_separate()
    {
        var product = Success(await Parse(Change((ld, p) => {
            ld["name"] = "Coca-Cola Classic Cans 375mL x 10 pack";
            p["DisplayName"] = "Coca-Cola Classic Cans 375mL x 10 pack"; p["PackageSize"] = "375mL x 10 pack";
            p["CentreTag"]!["TagContentText"] = "2 for $4";
        })));
        Assert.Equal(10, product.Identity.PackQuantity); Assert.Equal(375, product.Identity.PackSize);
        var offer = Assert.IsType<ProviderResult<ShopProductOffer>.Success>(product.Offer).Value;
        Assert.Equal(2.25m, offer.Price); Assert.Equal("2 for $4", offer.SpecialDescription);
    }
    [Theory]
    [InlineData("<title>Access Denied</title>", ProviderFailureKind.AccessRestricted)]
    [InlineData("<h2>We've looked everywhere for this page</h2>", ProviderFailureKind.NotFound)]
    [InlineData("<script type='application/ld+json'>{bad</script>", ProviderFailureKind.ParseError)]
    public async Task Missing_or_restricted_pages_are_honest_failures(string html, ProviderFailureKind kind) =>
        Assert.Equal(kind, Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await Parse(html)).Error.Kind);
    [Fact]
    public async Task Marketplace_is_not_reported_as_a_Woolworths_offer() =>
        Assert.Equal(ProviderFailureKind.NotSupported, Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(
            await Parse(Change((_, p) => p["IsMarketProduct"] = true))).Error.Kind);
    [Fact]
    public async Task Another_seller_or_product_offer_is_not_used()
    {
        foreach (var key in new[] { "url", "seller" })
        {
            var product = Success(await Parse(Change((ld, _) => {
                if (key == "url") ld["offers"]![key] = "https://www.woolworths.com.au/shop/productdetails/999/other";
                else ld["offers"]![key] = new JsonObject { ["name"] = "Another seller" };
            })));
            Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(product.Offer);
        }
    }
    [Theory]
    [InlineData("https://www.woolworths.com.au/shop/productdetails/32731", "32731")]
    [InlineData("https://www.woolworths.com.au/shop/productdetails/32731/name", "32731")]
    [InlineData("https://www.woolworths.com.au/shop/search/products", null)]
    [InlineData("https://evil.example/shop/productdetails/32731", null)]
    public void Product_urls_are_restricted(string url, string? expected) => Assert.Equal(expected, WoolworthsProductParser.ProductCode(new(url)));
}
