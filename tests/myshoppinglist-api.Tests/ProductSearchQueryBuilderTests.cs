using System.Globalization;
using myshoppinglist_api.Providers;

namespace myshoppinglist_api.Tests;

public class ProductSearchQueryBuilderTests
{
    [Fact]
    public void Coles_fallback_preserves_brand_variant_and_size_without_mutating_identity()
    {
        var product = new myshoppinglist_api.Providers.Models.ProductIdentity {
            Name = "Palmolive Body Wash Shower Gel Naturals Milk & Honey 1L", Brand = "Palmolive", Variant = "Milk Honey", PackSize = 1000, PackUnit = "mL" };
        Assert.Equal(new[] { "palmolive body wash shower gel naturals milk honey 1l", "palmolive naturals milk honey 1l" },
            ProductSearchQueryBuilder.BuildColesSearches(product));
        Assert.Contains("Shower Gel", product.Name);
        Assert.Equal(new[] { "mccain superfries frozen potato chips shoestring 900g", "mccain superfries shoestring 900g" },
            ProductSearchQueryBuilder.BuildColesSearches(new() { Name = "McCain Superfries Frozen Potato Chips Shoestring 900g", Brand = "McCain" }));
    }
    [Fact]
    public void Fallback_does_not_reduce_to_brand_only_or_discard_explicit_variant()
    {
        Assert.Single(ProductSearchQueryBuilder.BuildColesSearches(new() { Name = "Brand Body Wash 1L", Brand = "Brand" }));
        Assert.Single(ProductSearchQueryBuilder.BuildColesSearches(new() { Name = "Body Wash Milk Honey 1L" }));
        Assert.Contains("zero sugar", ProductSearchQueryBuilder.BuildColesSearches(new() {
            Name = "Brand Soft Drink Zero Sugar Bottle 1L", Brand = "Brand" }).Last());
        Assert.Empty(ProductSearchQueryBuilder.BuildColesSearches(new() { Name = " " }));
    }

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
