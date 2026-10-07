using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services.Models;
using myshoppinglist_api.Providers;
using System.Text.RegularExpressions;

namespace myshoppinglist_api.Services;

public sealed class ProductMatchingService(ProductNormalisationService normalisation)
{
    private static readonly string[] VariantMarkers = ["no sugar", "zero sugar", "diet", "classic", "original", "vanilla", "cherry", "caffeine free"];
    private static readonly HashSet<string> SupportingWords = ["soft", "drink", "pack", "x", "ml", "l", "g", "kg"];

    public ProductMatchResult Match(ProductIdentity first, ProductIdentity second)
    {
        var a = normalisation.Normalise(first);
        var b = normalisation.Normalise(second);
        var variantA = Variant(a);
        var variantB = Variant(b);
        var yoghurt = ProductSearchQueryBuilder.YoghurtFlavour(a.Name) is { } flavour
            && flavour == ProductSearchQueryBuilder.YoghurtFlavour(b.Name);
        if (yoghurt)
        {
            a = a with { PackQuantity = YoghurtQuantity(a) };
            b = b with { PackQuantity = YoghurtQuantity(b) };
        }
        if (yoghurt && a.Name.Split(' ').Contains("pouch") != b.Name.Split(' ').Contains("pouch"))
            return new(MatchType.NoMatch, 0, true, "different_yoghurt_format");
        if (Different(a.GTIN, b.GTIN) || !Same(a.GTIN, b.GTIN) && Different(a.Brand, b.Brand) || Different(a.Variant, b.Variant)
            || Different(variantA, variantB) || Different(a.ManufacturerPartNumber, b.ManufacturerPartNumber)
            || Different(a.ModelNumber, b.ModelNumber)
            || a.PackQuantity.HasValue && b.PackQuantity.HasValue && a.PackQuantity != b.PackQuantity
            || a.PackSize.HasValue && b.PackSize.HasValue && a.PackSize != b.PackSize
            || Different(a.PackUnit, b.PackUnit))
            return new(MatchType.NoMatch, 0, true, "contradictory_identity");

        if (Same(a.GTIN, b.GTIN)) return new(MatchType.Exact, 100, false, "gtin");
        if (Same(a.Brand, b.Brand) && (Same(a.ManufacturerPartNumber, b.ManufacturerPartNumber) || Same(a.ModelNumber, b.ModelNumber)))
            return new(MatchType.Exact, 98, false, "manufacturer_identity");
        var similarity = Similarity(a.Name, b.Name);
        // Retailer titles may omit these claims. Only accept otherwise identical yoghurt
        // wording with an explicit shared flavour, format and complete pack evidence.
        static string YoghurtName(string name) => name.Replace("yogurt", "yoghurt")
            .Replace("high protein", "protein").Replace("no added sugar", "");
        if (yoghurt && Similarity(YoghurtName(a.Name), YoghurtName(b.Name)) == 1)
            similarity = 1;
        if (Same(a.Brand, b.Brand) && Same(variantA, variantB)
            && a.PackQuantity is > 0 && a.PackQuantity == b.PackQuantity
            && a.PackSize is > 0 && a.PackSize == b.PackSize && Same(a.PackUnit, b.PackUnit) && similarity >= 0.75)
            return new(MatchType.Exact, 95, false, "complete_grocery_identity");
        return Same(a.Brand, b.Brand) && similarity >= 0.75
            ? new(MatchType.Likely, 70, false, "incomplete_identity")
            : new(MatchType.NoMatch, 0, false, "insufficient_identity");
    }

    public static ProductIdentity Identity(Product product) => new()
    {
        Name = product.Name,
        Brand = product.Brand,
        Variant = product.Variant,
        GTIN = product.GTIN,
        ManufacturerPartNumber = product.ManufacturerPartNumber,
        ModelNumber = product.ModelNumber,
        PackQuantity = product.PackQuantity,
        PackSize = product.PackSize,
        PackUnit = product.PackUnit,
        Category = product.Category,
        SubCategory = product.SubCategory
    };

    private static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) && a == b;
    private static int? YoghurtQuantity(ProductIdentity product)
    {
        if (product.PackQuantity.HasValue) return product.PackQuantity;
        if (product.PackSize is not > 0 || product.PackUnit != "g" || !product.Name.Split(' ').Contains("pouch")) return null;
        // A singular pouch with only its weight is one unit; never infer a count
        // from multipack wording or additional numeric quantity claims.
        var withoutWeight = Regex.Replace(product.Name, @"\b\d+\s*g\b", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return withoutWeight != product.Name && !withoutWeight.Any(char.IsDigit)
            && !Regex.IsMatch(withoutWeight, @"\b(?:pack|multipack|pouches|x)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ? 1 : null;
    }
    private static bool Different(string? a, string? b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a != b;
    private static string? Variant(ProductIdentity identity)
    {
        if (ProductSearchQueryBuilder.YoghurtFlavour(identity.Name) is { } flavour)
            return identity.Variant is null ? flavour : identity.Variant + "|" + flavour;
        var markers = VariantMarkers.Where(marker => (" " + identity.Name + " ").Contains(" " + marker + " ", StringComparison.Ordinal)).ToArray();
        // Title markers catch explicit contradictions even when providers leave the variant field empty.
        return markers.Length > 0 ? string.Join("|", markers) : identity.Variant;
    }
    private static double Similarity(string a, string b)
    {
        static HashSet<string> Tokens(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !SupportingWords.Contains(token) && !token.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        var x = Tokens(a);
        var y = Tokens(b);
        return x.Count == 0 || y.Count == 0 ? 0 : (double)x.Intersect(y).Count() / x.Union(y).Count();
    }
}
