using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services;
namespace myshoppinglist_api.Tests;
public class BrandSuggestionMatchingTests
{
    private static ProductIdentity Product(string name, string brand = "Acme", decimal size = 500) => new()
        { Name = name, Brand = brand, PackSize = size, PackUnit = "g", PackQuantity = 1 };
    [Theory]
    [InlineData("Acme Greek Style Natural Yoghurt 500g", "Other Greek Style Natural Yoghurt 500g", true)]
    [InlineData("Acme Greek Style Natural Yoghurt 500g", "Other Vanilla Yoghurt 500g", false)]
    [InlineData("Acme Milk Mouse Milk Chocolate 500g", "Other Milk Chocolate 500g", true)]
    [InlineData("Acme Milk Chocolate 500g", "Other Dairy Milk Chocolate 500g", true)]
    [InlineData("Acme Milk Chocolate 500g", "Other Soy Milk Chocolate 500g", false)]
    [InlineData("Acme Dairy Free Milk Chocolate 500g", "Other Milk Chocolate 500g", false)]
    [InlineData("Acme No Added Sugar Chocolate 500g", "Other Sugar Chocolate 500g", false)]
    [InlineData("Acme Milk Chocolate 500g", "Other Dark Chocolate 500g", false)]
    [InlineData("Acme Gluten Free Bread 500g", "Other Bread 500g", false)]
    [InlineData("Acme Decaf Ground Coffee 500g", "Other Ground Coffee 500g", false)]
    [InlineData("Acme Instant Coffee 500g", "Other Ground Coffee 500g", false)]
    [InlineData("Acme Original Coffee 500g", "Other Original Coffee Pods 500g", false)]
    [InlineData("Acme Stage 1 Formula 500g", "Other Stage 2 Formula 500g", false)]
    public void Suggestions_preserve_product_type_claims_flavour_and_format(string original, string candidate, bool expected)
        => Assert.Equal(expected, new BrandSuggestionMatchingService().IsSuggestion(Product(original), Product(candidate, "Other")));
    [Fact]
    public void Queries_remove_brand_measurements_and_marketing_shape_but_retain_type_and_claims()
    {
        var matcher = new BrandSuggestionMatchingService();
        Assert.Equal("milk chocolate", matcher.Query(Product("Acme Milk Mouse Milk Chocolate 500g")));
        Assert.Equal("greek natural yoghurt", matcher.Query(Product("Acme Greek Style Natural Yoghurt 500g")));
        Assert.Contains("gluten free", matcher.Query(Product("Acme Gluten Free Bread 500g"))!);
        Assert.Null(matcher.Query(Product("Acme Coffee 500g") with { Brand = null }));
    }
    [Fact]
    public void Same_brand_missing_pack_incompatible_units_or_extreme_sizes_are_excluded()
    {
        var matcher = new BrandSuggestionMatchingService(); var original = Product("Acme Original Coffee 500g");
        Assert.False(matcher.IsSuggestion(original, original));
        Assert.False(matcher.IsSuggestion(original, Product("Other Original Coffee", "Other") with { PackSize = null }));
        Assert.False(matcher.IsSuggestion(original, Product("Other Original Coffee 500ml", "Other") with { PackUnit = "ml" }));
        Assert.False(matcher.IsSuggestion(original, Product("Other Original Coffee 10000g", "Other", 10000)));
    }
}
