using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers;

public static class ProductSearchQueryBuilder
{
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
        _ => throw new ArgumentOutOfRangeException(nameof(shopCode))
    };
}
