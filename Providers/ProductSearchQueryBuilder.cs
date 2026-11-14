using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers;

public static class ProductSearchQueryBuilder
{
    private static readonly TimeSpan ComparisonTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly HashSet<string> PackagingWords = ["box", "boxed", "share", "pack", "snack", "snacks", "x"];
    private static readonly HashSet<string> IceBlockWords = ["ice", "water", "block", "blocks", "mix", "flavour", "flavours", "flavor", "flavors", "tube", "tubes"];
    private const string FlavourCountPattern = @"\b(?<count>\d+)\s+(?=(?:[\p{L}]+\s+){0,2}flavou?rs?\b)";

    public static IReadOnlyList<string> BuildComparisonSearches(ProductIdentity product)
    {
        var full = Build(product);
        if (full is null) return [];
        var brand = Build(new ProductIdentity { Name = product.Brand ?? "" });
        if (brand is null) return [full];
        var terms = ComparisonTerms(product);
        var brandWords = brand.Split(' ').ToHashSet(StringComparer.Ordinal);
        var concise = string.Join(' ', terms);
        // Search broadly, but never reduce a comparison to a brand-only query.
        // The original barcode, variety and pack still decide candidate identity.
        return concise.Length is > 0 and <= 160 && concise != full
            && terms.Any(t => !brandWords.Contains(t)) ? [full, concise] : [full];
    }

    internal static IReadOnlyList<string> ComparisonTerms(ProductIdentity product)
    {
        if (string.IsNullOrWhiteSpace(product.Name) || product.Name.Length > 500
            || product.Brand?.Length > 200 || product.Variant?.Length > 200) return [];
        var text = string.Join(' ', new[] { product.Brand, product.Name, product.Variant }.OfType<string>())
            .Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        // Remove stated pack measurements/counts, preserving formulation numbers and claims.
        text = Regex.Replace(text, @"\b\d+(?:\.\d+)?\s*(?:ml|kg|g|l)\b", " ", RegexOptions.CultureInvariant, ComparisonTimeout);
        text = Regex.Replace(text, @"\b\d+\s*(?:pack\b|[x×])|[x×]\s*\d+\b", " ", RegexOptions.CultureInvariant, ComparisonTimeout);
        text = Regex.Replace(text, FlavourCountPattern, "", RegexOptions.CultureInvariant, ComparisonTimeout);
        var words = Regex.Matches(text, @"[\p{L}\p{N}]+(?:[.'’-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant, ComparisonTimeout)
            .Select(m => m.Value).ToArray();
        var protectedWords = Regex.Matches(string.Join(' ', product.Brand, product.Variant).ToLowerInvariant(),
            @"[\p{L}\p{N}]+(?:[.'’-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant, ComparisonTimeout)
            .Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var ice = words.Contains("ice");
        return words.Where(w => protectedWords.Contains(w) || !PackagingWords.Contains(w) && !(ice && IceBlockWords.Contains(w)))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static string? FlavourCount(ProductIdentity product) => product.Name.Length <= 500
        && Regex.Match(product.Name, FlavourCountPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, ComparisonTimeout)
            is { Success: true } match ? match.Groups["count"].Value : null;

    public static IReadOnlyList<string> BuildSearches(ProductIdentity product)
    {
        var full = Build(product);
        var flavour = YoghurtFlavour(full ?? "");
        var brand = Build(new ProductIdentity { Name = product.Brand ?? "" });
        if (full is null || flavour is null || brand is null) return BuildColesSearches(product);
        var size = Regex.Match(full, @"\b\d+(?:\.\d+)?(?:g|kg)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Value;
        var format = full.Split(' ').Contains("pouch") ? " pouch" : "";
        var concise = $"{brand} {flavour} yoghurt{format} {size}".Trim();
        return new[] { full, concise, $"{brand} {flavour}" }.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static string? YoghurtFlavour(string name)
    {
        var words = name.Split(' ').ToHashSet(StringComparer.Ordinal);
        if (!words.Contains("yoghurt") && !words.Contains("yogurt")) return null;
        var flavours = new[] { "mango", "vanilla", "strawberry", "banana", "blueberry", "raspberry", "peach", "coconut" }.Where(words.Contains).ToArray();
        return flavours.Length == 1 ? flavours[0] : null;
    }
    public static IReadOnlyList<string> BuildColesSearches(ProductIdentity product)
    {
        var full = Build(product);
        if (full is null) return [];
        if (string.IsNullOrWhiteSpace(product.Brand)) return [full];
        // Broaden discovery only; matching still uses the original identity, barcode and pack.
        var descriptive = new HashSet<string>(["body", "wash", "shower", "gel", "soft", "drink", "bottle", "frozen", "potato", "chips"]);
        var protectedWords = (Build(new ProductIdentity { Name = product.Brand, Variant = product.Variant }) ?? "")
            .Split(' ').ToHashSet(StringComparer.Ordinal);
        var brandWords = (Build(new ProductIdentity { Name = product.Brand }) ?? "").Split(' ').ToHashSet(StringComparer.Ordinal);
        var words = full.Split(' ');
        var retained = words.Where(w => !descriptive.Contains(w) || protectedWords.Contains(w)).ToArray();
        var distinctive = retained.Count(w => !brandWords.Contains(w) && !w.Any(char.IsDigit));
        var shorter = string.Join(' ', retained);
        return shorter != full && distinctive >= 2 ? [full, shorter] : [full];
    }

    public static string? Build(ProductIdentity product)
    {
        if (string.IsNullOrWhiteSpace(product.Name) || product.Name.Length > 500) return null;
        var parts = new List<string?> { product.Brand, product.Name, product.Variant };
        if (product.PackSize is > 0 && product.PackUnit is { Length: > 0 and <= 10 }
            && !Regex.IsMatch(product.Name, @"\d\s*(?:ml|kg|g|l)\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            parts.Add(product.PackSize.Value.ToString("0.####", CultureInfo.InvariantCulture) + product.PackUnit);
        if (product.PackQuantity is > 1 && !Regex.IsMatch(product.Name, @"\d\s*(?:pack\b|[x×])", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            parts.Add(product.PackQuantity + " pack");
        // Keep variant/pack terms. Do not use fuzzy matching or strip digits from identity.
        var text = string.Join(" ", parts.OfType<string>().Select(p => p[..Math.Min(p.Length, 500)]))
            .Normalize(NormalizationForm.FormKC);
        var words = Regex.Matches(text, @"[\p{L}\p{N}]+(?:[.'’-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)).Select(m => m.Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var result = new StringBuilder();
        foreach (var word in words)
        {
            if (result.Length + word.Length + (result.Length == 0 ? 0 : 1) > 160) break;
            if (result.Length > 0) result.Append(' ');
            result.Append(word);
        }
        return result.Length == 0 ? null : result.ToString();
    }

    public static Uri SearchUrl(string shopCode, string query) => shopCode switch
    {
        "coles" => new("https://www.coles.com.au/search/products?q=" + Uri.EscapeDataString(query)),
        "woolworths" => new("https://www.woolworths.com.au/shop/search/products?searchTerm=" + Uri.EscapeDataString(query)),
        "aldi" => new("https://www.aldi.com.au/results?q=" + Uri.EscapeDataString(query)),
        _ => throw new ArgumentOutOfRangeException(nameof(shopCode))
    };
}
