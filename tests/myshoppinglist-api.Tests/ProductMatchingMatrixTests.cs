using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public class ProductMatchingMatrixTests
{
    public static IEnumerable<object[]> Cases()
    {
        var flavours = new[] { "Mango", "Vanilla", "Strawberry", "Banana", "Blueberry", "Raspberry", "Peach", "Coconut" };
        foreach (var flavour in flavours)
            foreach (var size in new[] { 100, 150, 200, 500 })
                foreach (var spelling in new[] { "Yoghurt", "Yogurt" })
                    foreach (var count in new[] { 1, 4 })
                    {
                        var source = new ProductIdentity
                        {
                            Name = $"YoPro High Protein {spelling} Pouch No Added Sugar {flavour} {size}g",
                            Brand = "YoPro",
                            PackQuantity = count,
                            PackSize = size,
                            PackUnit = "g"
                        };
                        var same = source with { Name = $"YoPRO Protein Yoghurt Pouch {flavour} {size}g" };
                        var label = $"{flavour}-{size}-{spelling}-{count}";
                        yield return [label + "-same", source, same, true];
                        yield return [label + "-case", source, same with { Name = same.Name.ToUpperInvariant(), Brand = "YOPRO" }, true];
                        yield return [label + "-units", source, same with { PackSize = size / 1000m, PackUnit = "kg" }, true];
                        yield return [label + "-size", source, same with { PackSize = size + 50 }, false];
                        yield return [label + "-count", source, same with { PackQuantity = count + 1 }, false];
                        yield return [label + "-unit", source, same with { PackUnit = "mL" }, false];
                        yield return [label + "-brand", source, same with { Brand = "OtherBrand" }, false];
                        yield return [label + "-format", source, same with { Name = same.Name.Replace("Pouch", "Tub") }, false];
                        yield return [label + "-unknown-size", source, same with { PackSize = null }, false];
                        yield return [label + "-unknown-brand", source, same with { Brand = null }, false];
                        foreach (var other in flavours.Where(f => f != flavour))
                            yield return [label + "-flavour-" + other, source, same with { Name = same.Name.Replace(flavour, other) }, false];
                    }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Grocery_identity_matrix(string scenario, ProductIdentity source, ProductIdentity candidate, bool exact)
    {
        var matcher = new ProductMatchingService(new());
        var forward = matcher.Match(source, candidate);
        var reverse = matcher.Match(candidate, source);
        Assert.True((forward.Type == MatchType.Exact) == exact, $"{scenario}: expected exact={exact}, got {forward}");
        Assert.Equal(forward, reverse);
    }

    [Theory]
    [InlineData("Mango", "Mango Peach")]
    [InlineData("Mango", "Mango Passionfruit")]
    [InlineData("Vanilla", "Vanilla Chocolate")]
    public void Shared_barcode_does_not_override_explicit_flavour_conflict(string first, string second)
    {
        var a = new ProductIdentity { Name = $"YoPro Protein Yoghurt Pouch {first} 150g", Brand = "YoPro", PackSize = 150, PackUnit = "g", PackQuantity = 1, GTIN = "9300675014779" };
        Assert.NotEqual(MatchType.Exact, new ProductMatchingService(new()).Match(a, a with { Name = a.Name.Replace(first, second) }).Type);
    }

    [Fact]
    public void Equivalent_explicit_and_title_flavour_evidence_matches()
    {
        var a = new ProductIdentity { Name = "YoPro Protein Yoghurt Pouch Mango 150g", Brand = "YoPro", PackSize = 150, PackUnit = "g", PackQuantity = 1 };
        Assert.Equal(MatchType.Exact, new ProductMatchingService(new()).Match(a, a with { Variant = "Mango" }).Type);
    }

    [Theory]
    [InlineData("Mango", "Mango Passionfruit")]
    [InlineData("Mango", "Mango Lactose Free")]
    [InlineData("Mango", "Mango Dairy Free")]
    public void Additional_flavour_or_dietary_variant_is_not_assumed_equivalent(string first, string second)
    {
        var a = new ProductIdentity { Name = $"YoPro Protein Yoghurt Pouch {first} 150g", Brand = "YoPro", PackSize = 150, PackUnit = "g", PackQuantity = 1 };
        Assert.NotEqual(MatchType.Exact, new ProductMatchingService(new()).Match(a, a with { Name = a.Name.Replace(first, second) }).Type);
    }

    [Theory]
    [InlineData("Mango Peach", "Peach Mango")]
    [InlineData("Mango Passionfruit", "Passionfruit Mango")]
    [InlineData("Vanilla Chocolate", "Chocolate Vanilla")]
    public void Matching_blends_allow_reordered_and_repeated_field_evidence(string title, string variant)
    {
        var a = Yoghurt(title);
        var b = a with { Name = a.Name.Replace(title, variant), Variant = variant };
        AssertSymmetric(a, b, MatchType.Exact);
    }

    [Fact]
    public void Passion_fruit_spelling_is_equivalent()
    {
        var a = Yoghurt("Mango Passionfruit");
        AssertSymmetric(a, a with { Name = a.Name.Replace("Passionfruit", "Passion Fruit"), Variant = "Passion Fruit Mango" }, MatchType.Exact);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("9300675014779")]
    public void Contradictory_title_and_variant_field_never_match(string? barcode)
    {
        var a = Yoghurt("Mango") with { GTIN = barcode };
        var b = a with { Variant = "Vanilla" };
        AssertSymmetric(a, b, MatchType.NoMatch);
        Assert.True(new ProductMatchingService(new()).Match(a, b).Contradiction);
    }

    [Theory]
    [InlineData("Lactose Free")]
    [InlineData("Dairy Free")]
    [InlineData("Vegan")]
    public void Barcode_cannot_override_explicit_dietary_variant(string extra)
    {
        var a = Yoghurt("Mango") with { GTIN = "9300675014779" };
        AssertSymmetric(a, a with { Name = a.Name + " " + extra }, MatchType.NoMatch);
    }

    [Fact]
    public void Unrecognised_extra_flavour_requires_more_evidence_than_similar_words()
    {
        var a = Yoghurt("Mango");
        var b = a with { Name = a.Name.Replace("Mango", "Mango Dragonfruit") };
        Assert.NotEqual(MatchType.Exact, new ProductMatchingService(new()).Match(a, b).Type);
        Assert.NotEqual(MatchType.Exact, new ProductMatchingService(new()).Match(b, a).Type);
    }

    private static ProductIdentity Yoghurt(string flavour) => new()
    {
        Name = $"YoPro Protein Yoghurt Pouch {flavour} 150g",
        Brand = "YoPro",
        PackSize = 150,
        PackUnit = "g",
        PackQuantity = 1
    };

    private static void AssertSymmetric(ProductIdentity a, ProductIdentity b, MatchType expected)
    {
        var matcher = new ProductMatchingService(new());
        Assert.Equal(expected, matcher.Match(a, b).Type);
        Assert.Equal(matcher.Match(a, b), matcher.Match(b, a));
    }
}
