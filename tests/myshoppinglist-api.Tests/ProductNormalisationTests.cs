using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public class ProductNormalisationTests
{
    private readonly ProductNormalisationService _service = new();
    [Theory]
    [InlineData("Coca-Cola", "coca cola")]
    [InlineData(" COCA  COLA ", "coca cola")]
    [InlineData("Ｃｏｃａ－Ｃｏｌａ", "coca cola")]
    public void Comparison_text_normalises_without_changing_display_values(string input, string expected) =>
        Assert.Equal(expected, _service.Text(input));

    [Theory]
    [InlineData("375 ml", 375, "mL")]
    [InlineData("375mL", 375, "mL")]
    [InlineData("0.375 L", 375, "mL")]
    [InlineData("1 kg", 1000, "g")]
    public void Equivalent_units_are_standardised(string input, int size, string unit) =>
        Assert.Equal(((decimal?)size, unit), _service.ParsePackSize(input));

    [Fact]
    public void Gtin_forms_are_equivalent_and_checksum_is_validated()
    {
        Assert.Equal("09300675014779", _service.Gtin("9300675014779"));
        Assert.Equal("09300675014779", _service.Gtin("09300675014779"));
        Assert.Null(_service.Gtin("9300675014778"));
        Assert.Null(_service.Gtin("abc"));
    }

    [Fact]
    public void Unresolved_pack_quantity_is_not_reconstructed_from_title()
    {
        var original = new myshoppinglist_api.Providers.Models.ProductIdentity { Name = "Coca-Cola 24 x 375ml", PackQuantity = null };
        Assert.Null(_service.Normalise(original).PackQuantity);
        Assert.Equal("Coca-Cola 24 x 375ml", original.Name);
    }
}
