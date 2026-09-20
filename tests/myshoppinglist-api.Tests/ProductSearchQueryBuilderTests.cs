using System.Globalization;
using myshoppinglist_api.Providers;

namespace myshoppinglist_api.Tests;

public class ProductSearchQueryBuilderTests
{
    [Fact]
    public void Query_preserves_variant_and_pack_without_duplicating_brand_or_units()
    {
        var query = ProductSearchQueryBuilder.Build(new() { Name = "Coca-Cola Zero Sugar 1.25L", Brand = "Coca-Cola", PackSize = 1250, PackUnit = "mL" });
        Assert.Equal("coca-cola zero sugar 1.25l", query);
    }
    [Fact]
    public void Missing_pack_is_added_with_invariant_decimal_and_url_encoding()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var query = ProductSearchQueryBuilder.Build(new() { Name = "Brand & Drink", PackSize = 1.25m, PackUnit = "L", PackQuantity = 6 });
            Assert.Equal("brand drink 1.25l 6 pack", query);
            Assert.Contains("q=brand%20drink", ProductSearchQueryBuilder.SearchUrl("coles", query!).AbsoluteUri);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("&&&")]
    public void Empty_identity_never_creates_a_broad_search(string name) => Assert.Null(ProductSearchQueryBuilder.Build(new() { Name = name }));
    [Fact]
    public void Query_length_is_bounded() => Assert.InRange(ProductSearchQueryBuilder.Build(new() { Name = string.Join(' ', Enumerable.Repeat("word", 90)) })!.Length, 1, 160);
}
