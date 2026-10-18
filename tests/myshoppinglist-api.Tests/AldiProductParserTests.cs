using System.Text.Json;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Aldi;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Tests;

public class AldiProductParserTests
{
    internal const string Code = "000000000000398449";
    internal const string Url = "https://www.aldi.com.au/product/farmdale-thickened-cream-600ml-" + Code;
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
    internal static ColesEvidence Evidence(string? price = "5.19", string currency = "AUD", string offerUrl = Url) => new(null,
        [JsonSerializer.Serialize(new { type = "Product", name = "Thickened Cream 600ml", brand = new { name = "FARMDALE" }, offers = new { type = "Offer", url = offerUrl, price, priceCurrency = currency, availability = "https://schema.org/InStock" } }).Replace("\"type\"", "\"@type\"")],
        JsonSerializer.Serialize(new { canonicalUrl = Url, name = "Thickened Cream 600ml" }));

    [Fact]
    public void Aldi_identity_price_pack_and_leading_zeroes_are_preserved_without_assuming_national_scope()
    {
        var parsed = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(new AldiProductParser().Parse(new(Url), Evidence(), Now)).Value;
        Assert.Equal(Code, parsed.ShopProductCode); Assert.Equal("FARMDALE", parsed.Identity.Brand);
        Assert.Equal(600, parsed.Identity.PackSize); Assert.Equal("mL", parsed.Identity.PackUnit);
        var offer = Assert.IsType<ProviderResult<ShopProductOffer>.Success>(parsed.Offer).Value;
        Assert.Equal(5.19m, offer.Price); Assert.Equal("AUD", offer.Currency); Assert.Equal(PriceScope.Unknown, offer.PriceScope);
        Assert.Null(offer.Location); Assert.Equal(SourceType.BrowserExtension, offer.SourceType);
    }

    [Theory]
    [InlineData(null, "AUD")]
    [InlineData("-1", "AUD")]
    [InlineData("5.199", "AUD")]
    [InlineData("5.19", "USD")]
    public void Aldi_missing_or_invalid_prices_keep_the_product_without_a_usable_offer(string? price, string currency)
    {
        var parsed = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(new AldiProductParser().Parse(new(Url), Evidence(price, currency), Now)).Value;
        Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(parsed.Offer);
    }

    [Fact]
    public void Aldi_rejects_conflicting_identity_recommendations_duplicate_and_oversized_evidence()
    {
        var parser = new AldiProductParser(); var valid = Evidence();
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(parser.Parse(new(Url), null, Now));
        var badContext = valid with { AldiProductJson = "{\"canonicalUrl\":\"" + Url + "\",\"name\":\"Another product\"}" };
        foreach (var evidence in new[] { badContext, valid with { JsonLd = [valid.JsonLd![0], valid.JsonLd[0]] }, valid with { JsonLd = [new string('x', 2_000_001)] }, Evidence(offerUrl: Url.Replace(Code, "123")) })
            Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(parser.Parse(new(Url), evidence, Now));
    }

    [Theory]
    [InlineData("https://aldi.com.au/product/farmdale-thickened-cream-600ml-000000000000398449", true)]
    [InlineData("https://www.aldi.com.au/products/dairy/k/123", false)]
    [InlineData("https://www.aldi.com.au/product/anything", false)]
    [InlineData("https://www.aldi.com.au.evil.example/product/cream-123", false)]
    [InlineData("http://www.aldi.com.au/product/cream-123", false)]
    [InlineData("https://user@www.aldi.com.au/product/cream-123", false)]
    public void Aldi_product_urls_are_strict(string url, bool valid) => Assert.Equal(valid, AldiProductParser.ProductCode(new(url)) is not null);

    [Fact]
    public void Aldi_future_expired_conditional_and_multiple_offers_are_not_current_single_pack_prices()
    {
        foreach (var extra in new[] { ",\"validFrom\":\"2099-01-01\"", ",\"priceValidUntil\":\"2000-01-01\"", ",\"eligibleQuantity\":{\"value\":2}" })
        {
            var evidence = Evidence();
            evidence = evidence with { JsonLd = [evidence.JsonLd![0].Replace("\"priceCurrency\":\"AUD\"", "\"priceCurrency\":\"AUD\"" + extra)] };
            var product = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(new AldiProductParser().Parse(new(Url), evidence, Now)).Value;
            Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(product.Offer);
        }
    }

    [Theory]
    [InlineData("Bananas per kg")]
    [InlineData("Chicken /kg")]
    [InlineData("Meat approx 500g")]
    public void Aldi_variable_weight_prices_do_not_become_single_package_prices(string name)
    {
        var evidence = Evidence();
        evidence = evidence with { AldiProductJson = null, JsonLd = [evidence.JsonLd![0].Replace("Thickened Cream 600ml", name)] };
        var product = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(new AldiProductParser().Parse(new(Url), evidence, Now)).Value;
        Assert.Equal("variable_weight_price", Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(product.Offer).Error.Code);
    }
}
