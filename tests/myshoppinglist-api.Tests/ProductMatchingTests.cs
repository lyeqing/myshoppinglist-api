using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public class ProductMatchingTests
{
    private readonly ProductMatchingService _matcher = new(new());
    private static ProductIdentity Coke => new()
    {
        Name = "Coca-Cola Classic Cans 24 x 375mL", Brand = "Coca-Cola", Variant = "Classic",
        GTIN = "9300675014779", PackQuantity = 24, PackSize = 375, PackUnit = "mL"
    };

    [Fact]
    public void Exact_gtin_receives_full_confidence()
    {
        var match = _matcher.Match(Coke, Coke with { GTIN = "09300675014779" });
        Assert.Equal(MatchType.Exact, match.Type);
        Assert.Equal(100, match.Confidence);
    }

    [Fact]
    public void Complete_grocery_identity_matches_without_gtin()
    {
        var match = _matcher.Match(Coke with { GTIN = null }, Coke with
        { GTIN = null, Name = "Coca Cola Classic Soft Drink Cans 24 Pack 375ml", PackSize = 0.375m, PackUnit = "L" });
        Assert.Equal(MatchType.Exact, match.Type);
        Assert.Equal(95, match.Confidence);
    }

    [Fact]
    public void Same_gtin_does_not_override_contradictory_metadata()
    {
        foreach (var candidate in new[]
        {
            Coke with { Variant = "No sugar" }, Coke with { PackQuantity = 30 },
            Coke with { PackSize = 250 }, Coke with { Brand = "Other" }, Coke with { PackUnit = "g" }
        }) Assert.True(_matcher.Match(Coke, candidate).Contradiction);
    }

    [Fact]
    public void Title_variant_conflict_is_detected_when_variant_fields_are_missing() =>
        Assert.True(_matcher.Match(Coke with { Variant = null }, Coke with
        { Variant = null, Name = "Coca-Cola No Sugar Cans 24 x 375mL" }).Contradiction);

    [Fact]
    public void Similar_names_with_missing_pack_evidence_are_not_exact() =>
        Assert.NotEqual(MatchType.Exact, _matcher.Match(Coke with { GTIN = null }, Coke with { GTIN = null, PackQuantity = null }).Type);

    [Fact]
    public void Manufacturer_identity_requires_brand_and_preserves_identifier_punctuation()
    {
        var first = new ProductIdentity { Name = "Drill", Brand = "Brand", ManufacturerPartNumber = "AB-12" };
        Assert.Equal(MatchType.Exact, _matcher.Match(first, first with { Name = "Cordless Drill", ManufacturerPartNumber = "ab-12" }).Type);
        Assert.True(_matcher.Match(first, first with { ManufacturerPartNumber = "AB12" }).Contradiction);
        Assert.NotEqual(MatchType.Exact, _matcher.Match(first, first with { Brand = null }).Type);
    }
}
