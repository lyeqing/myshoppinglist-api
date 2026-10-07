using System.Text.Json;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

public class RetailerRegressionCollectionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateTimeOffset Checked = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Regression", "products.json")));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.True(cases.Length >= 30);
        Assert.Equal(cases.Length, cases.Select(c => c.GetProperty("id").GetString()).Distinct().Count());
        foreach (var item in cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("reason").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("provenance").GetString()));
            yield return [item.GetProperty("id").GetString()!, item.GetRawText()];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Saved_case(string id, string json)
    {
        using var document = JsonDocument.Parse(json);
        var row = document.RootElement;
        Assert.Equal(id, row.GetProperty("id").GetString());
        var expected = row.GetProperty("expected");
        switch (row.GetProperty("kind").GetString())
        {
            case "product":
                var page = new RetailerPage(new(row.GetProperty("url").GetString()!), row.GetProperty("html").GetString()!, Checked);
                var result = row.GetProperty("retailer").GetString() == "coles"
                    ? await new ColesProductParser().ParseAsync(page, default)
                    : await new WoolworthsProductParser().ParseAsync(page, default);
                if (expected.TryGetProperty("error", out var error))
                {
                    Assert.Equal(error.GetString(), Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(result).Error.Code);
                    break;
                }
                var product = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(result).Value;
                Assert.Equal(expected.GetProperty("code").GetString(), product.ShopProductCode);
                if (expected.TryGetProperty("name", out var name)) Assert.Equal(name.GetString(), product.Identity.Name);
                if (expected.TryGetProperty("offerError", out var offerError)) Assert.Equal(offerError.GetString(), Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(product.Offer).Error.Code);
                else if (expected.TryGetProperty("priceMissing", out _)) Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(product.Offer);
                else Assert.Equal(expected.GetProperty("price").GetDecimal(), Assert.IsType<ProviderResult<ShopProductOffer>.Success>(product.Offer).Value.Price);
                break;
            case "match":
                var source = row.GetProperty("source").Deserialize<ProductIdentity>(JsonOptions)!;
                var candidate = row.GetProperty("candidate").Deserialize<ProductIdentity>(JsonOptions)!;
                var matcher = new ProductMatchingService(new());
                var match = matcher.Match(source, candidate);
                Assert.Equal(expected.GetProperty("exact").GetBoolean(), match.Type == MatchType.Exact);
                Assert.Equal(match, matcher.Match(candidate, source));
                break;
            case "promotion":
                var price = new ShopProductPrice
                {
                    Price = 30,
                    Currency = "AUD",
                    SourceType = SourceType.RetailerPage,
                    SourceUrl = "https://www.coles.com.au/product/1115507",
                    SpecialType = "MULTI_SAVE",
                    SpecialDescription = row.GetProperty("description").GetString(),
                    CheckedDate = Checked.AddDays(row.GetProperty("stale").GetBoolean() ? -8 : 0).UtcDateTime
                };
                var status = InStoreShoppingService.PriceStatus(price, Checked, new());
                Assert.Equal(expected.GetProperty("status").GetString(), status);
                decimal? total = status == "Fresh" ? PromotionCalculationService.Calculate(price.Price,
                    row.GetProperty("quantity").GetInt32(), InStoreShoppingService.SinglePriceMultibuy(price)).Total : null;
                Assert.Equal(expected.GetProperty("total").ValueKind == JsonValueKind.Null ? null : expected.GetProperty("total").GetDecimal(), total);
                break;
            default: Assert.Fail("Unknown regression case kind"); break;
        }
    }
}
