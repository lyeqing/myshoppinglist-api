using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Services;

// Conservative suggestions, never evidence of equivalence or dietary suitability.
public sealed class BrandSuggestionMatchingService
{
    private readonly ProductNormalisationService normalisation = new();
    private readonly PackComparisonService packs = new();
    private static readonly HashSet<string> Types = new("chocolate yoghurt coffee tea milk butter cheese cream bread rice pasta flour sugar cereal oats biscuits crackers juice water eggs tuna salmon chicken beef pork soap shampoo detergent".Split(' '));
    private static readonly HashSet<string> Claims = new("vegan vegetarian organic gluten free lactose sugar salt no added unsweetened sweetened plant based decaf caffeinated low reduced light skim full fat protein infant baby formula".Split(' '));
    private static readonly HashSet<string> Flavours = new("milk dark white vanilla strawberry banana blueberry raspberry mango peach coconut soy soya oat caramel mint hazelnut almond orange lemon plain natural original greek".Split(' '));
    private static readonly HashSet<string> Formats = new("powder liquid spray pouch bar block spread chips instant ground beans capsule pods frozen fresh dried sliced grated shredded sparkling still".Split(' '));
    private static readonly HashSet<string> Filler = new("style the and with of a an".Split(' '));

    private string[] Words(ProductIdentity identity)
    {
        var quantity = packs.Read(identity);
        if (quantity is null) return [];
        var brand = normalisation.Text(identity.Brand).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return quantity.FamilyName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !brand.Contains(w) && !Filler.Contains(w)).Select(w => w == "yogurt" ? "yoghurt" : w).Distinct().ToArray();
    }
    public string? Query(ProductIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Brand) || identity.Name.Length > 500) return null;
        var words = Words(identity);
        var types = words.Where(Types.Contains).ToArray();
        // Known product nouns permit dropping marketing/shape terms such as Milk Mouse.
        var retained = types.Length > 0 ? words.Where(w => Types.Contains(w) || Claims.Contains(w) || Flavours.Contains(w) || Formats.Contains(w) || w.Any(char.IsDigit) || words.Contains("free") && w is "dairy" or "nut").ToArray() : words;
        var query = string.Join(' ', retained);
        return retained.Length >= 2 && query.Length <= 160 ? query : null;
    }
    public bool IsSuggestion(ProductIdentity original, ProductIdentity candidate)
    {
        var a = normalisation.Normalise(original); var b = normalisation.Normalise(candidate);
        var x = packs.Read(original); var y = packs.Read(candidate);
        if (Query(original) is null || string.IsNullOrEmpty(b.Brand) || a.Brand == b.Brand
            || x is null || y is null || x.Unit != y.Unit || y.Total / x.Total is < 0.25m or > 4m
            || a.GTIN is not null && a.GTIN == b.GTIN
            || a.ModelNumber != b.ModelNumber
            || !string.Equals(a.Variant, b.Variant, StringComparison.OrdinalIgnoreCase)
            || a.Category is not null && b.Category is not null && normalisation.Text(a.Category) != normalisation.Text(b.Category)
            || a.SubCategory is not null && b.SubCategory is not null && normalisation.Text(a.SubCategory) != normalisation.Text(b.SubCategory)) return false;
        var phrases = new[] { "dairy free", "nut free", "no added sugar", "no added salt" };
        var originalText = " " + normalisation.Text(original.Name) + " ";
        var candidateText = " " + normalisation.Text(candidate.Name) + " ";
        if (phrases.Any(phrase => originalText.Contains(" " + phrase + " ") != candidateText.Contains(" " + phrase + " "))) return false;
        var first = Words(original).ToHashSet(); var second = Words(candidate).ToHashSet();
        if (!first.Where(Types.Contains).ToHashSet().SetEquals(second.Where(Types.Contains))) return false;
        foreach (var group in new[] { Claims, Flavours, Formats })
            if (!first.Where(group.Contains).ToHashSet().SetEquals(second.Where(group.Contains))) return false;
        if (!first.Where(w => w.Any(char.IsDigit)).ToHashSet().SetEquals(second.Where(w => w.Any(char.IsDigit)))) return false;
        if (first.Any(Types.Contains)) return true;
        var meaningful = first.Where(w => !Claims.Contains(w) && !Flavours.Contains(w) && !Formats.Contains(w)).ToArray();
        return meaningful.Length > 0 && meaningful.Count(second.Contains) >= Math.Max(1, (int)Math.Ceiling(meaningful.Length * 0.6));
    }
}
