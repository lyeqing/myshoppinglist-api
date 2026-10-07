using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public class ProductMatchingTests
{
    [Fact]
    public void Yopro_retailer_titles_match_but_flavour_format_pack_and_barcode_conflicts_do_not()
    {
        var a = new ProductIdentity { Name = "YoPro High Protein Yoghurt Pouch No Added Sugar Mango 150g", Brand = "YoPro", PackQuantity = 1, PackSize = 150, PackUnit = "g" };
        var b = a with { Name = "YoPRO Protein Yoghurt Pouch Mango 150g" };
        Assert.Equal(MatchType.Exact, _matcher.Match(a, b).Type);
        Assert.Equal(MatchType.Exact, _matcher.Match(a with { PackQuantity = null }, b with { PackQuantity = null }).Type);
        foreach (var wrong in new[] { b with { Name = b.Name.Replace("Mango", "Vanilla") }, b with { Name = b.Name.Replace("Pouch", "Tub") }, b with { PackSize = 160 }, b with { PackQuantity = 4 }, b with { PackQuantity = null, Name = b.Name + " multipack" }, b with { Name = b.Name.Replace("Mango", "Mango Peach") } })
            Assert.NotEqual(MatchType.Exact, _matcher.Match(a, wrong).Type);
        Assert.True(_matcher.Match(a with { GTIN = "9300675014779" }, b with { GTIN = "4006381333931" }).Contradiction);
    }
    private readonly ProductMatchingService _matcher = new(new());
    private static ProductIdentity Coke => new()
    {
        Name = "Coca-Cola Classic Cans 24 x 375mL",
        Brand = "Coca-Cola",
        Variant = "Classic",
        GTIN = "9300675014779",
        PackQuantity = 24,
        PackSize = 375,
        PackUnit = "mL"
    };

    [Fact]
    public void Equal_valid_barcode_tolerates_retailer_brand_labels_but_not_pack_or_variant_conflicts()
    {
        var a = new ProductIdentity { Name = "Pickers Cheesy Garlic Bread Dippers 230g", Brand = "Pickers", GTIN = "8710438132533", PackSize = 230, PackUnit = "g" };
        var b = a with { Brand = "McCain", GTIN = "08710438132533" };
        Assert.Equal(MatchType.Exact, _matcher.Match(a, b).Type);
        Assert.True(_matcher.Match(a, b with { PackSize = 500 }).Contradiction);
        Assert.True(_matcher.Match(a with { PackQuantity = 1 }, b with { PackQuantity = 2 }).Contradiction);
        Assert.True(_matcher.Match(a with { Variant = "original" }, b with { Variant = "cherry" }).Contradiction);
        Assert.True(_matcher.Match(a with { GTIN = null }, b).Contradiction);
    }

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
            Coke with { PackSize = 250 }, Coke with { PackUnit = "g" }
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
