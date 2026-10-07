using System.Globalization;
using myshoppinglist_api.Providers;

namespace myshoppinglist_api.Tests;

public class ProductSearchQueryBuilderTests
{
    [Theory]
    [InlineData("Mango", "150g")]
    [InlineData("Vanilla", "500g")]
    [InlineData("Strawberry", "1kg")]
    [InlineData("Blueberry", "200g")]
    public void Search_sequence_is_bounded_distinct_and_preserves_original_identity(string flavour, string size)
    {
        var product = new myshoppinglist_api.Providers.Models.ProductIdentity { Name = $"YoPro High Protein Yoghurt Pouch No Added Sugar {flavour} {size}", Brand = "YoPro" };
        var original = product with { };
        var searches = ProductSearchQueryBuilder.BuildSearches(product);
        Assert.Equal(3, searches.Count);
        Assert.Equal(searches.Count, searches.Distinct().Count());
        Assert.All(searches, q => Assert.InRange(q.Length, 1, 160));
        Assert.Contains(size, searches[1]);
        Assert.Equal($"yopro {flavour.ToLowerInvariant()}", searches[^1]);
        Assert.Equal(original, product);
        foreach (var retailer in new[] { "coles", "woolworths" })
            Assert.All(searches, q => Assert.Contains(Uri.EscapeDataString(q), ProductSearchQueryBuilder.SearchUrl(retailer, q).AbsoluteUri));
    }

    [Theory]
    [InlineData("YoPro Yoghurt Mango Peach 150g")]
    [InlineData("YoPro Yoghurt 150g")]
    public void Unknown_or_multiple_flavours_do_not_reduce_to_a_single_flavour(string name) =>
        Assert.Single(ProductSearchQueryBuilder.BuildSearches(new() { Name = name, Brand = "YoPro" }));

    [Fact]
    public void Yoghurt_search_progresses_from_full_identity_to_brand_and_flavour()
    {
        Assert.Equal(new[] { "yopro high protein yoghurt pouch no added sugar mango 150g", "yopro mango yoghurt pouch 150g", "yopro mango" },
            ProductSearchQueryBuilder.BuildSearches(new() { Name = "YoPro High Protein Yoghurt Pouch No Added Sugar Mango 150g", Brand = "YoPro" }));
        Assert.Single(ProductSearchQueryBuilder.BuildSearches(new() { Name = "Yoghurt 150g" }));
    }
    [Fact]
    public void Coles_fallback_preserves_brand_variant_and_size_without_mutating_identity()
    {
        var product = new myshoppinglist_api.Providers.Models.ProductIdentity
        {
            Name = "Palmolive Body Wash Shower Gel Naturals Milk & Honey 1L",
            Brand = "Palmolive",
            Variant = "Milk Honey",
            PackSize = 1000,
            PackUnit = "mL"
        };
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
        Assert.Contains("zero sugar", ProductSearchQueryBuilder.BuildColesSearches(new()
        {
            Name = "Brand Soft Drink Zero Sugar Bottle 1L",
            Brand = "Brand"
        }).Last());
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
