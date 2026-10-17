using System.Globalization;
using System.Text.RegularExpressions;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Services;

public sealed class PackComparisonService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private const string Measure = @"(?<![\d.])(?<size>\d+(?:\.\d+)?)\s*(?<unit>ml|millilitres?|kg|kilograms?|g|grams?|l|litres?)\b";
    private readonly ProductNormalisationService normalisation = new();
    public sealed record Quantity(decimal Total, string Unit, string FamilyName);

    public Quantity? Read(ProductIdentity product)
    {
        if (product.Name.Length > 500 || product.PackQuantity is <= 0 or > 10000) return null;
        var pack = normalisation.Pack(product.PackSize, product.PackUnit);
        if (pack.Size is not > 0 || pack.Unit is not ("g" or "mL")) return null;
        var title = product.Name.ToLowerInvariant();
        var measures = Regex.Matches(title, Measure, RegexOptions.CultureInvariant, Timeout);
        // Every title measurement must agree with the stored per-unit quantity.
        foreach (Match match in measures)
        {
            if (!decimal.TryParse(match.Groups["size"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)) return null;
            var found = normalisation.Pack(size, match.Groups["unit"].Value);
            if (found != pack) return null;
        }
        var count = product.PackQuantity ?? 1;
        if (count > 1)
        {
            // A pack count plus a weight alone can describe the total weight. Require
            // explicit count × per-unit size so we cannot multiply the total twice.
            var prefix = Regex.Match(title, @"\b(?<count>\d+)\s*[x×]\s*(?=\d+(?:\.\d+)?\s*(?:ml|kg|g|l)\b)", RegexOptions.CultureInvariant, Timeout);
            if (!prefix.Success || !int.TryParse(prefix.Groups["count"].Value, out var stated) || stated != count) return null;
            title = title.Remove(prefix.Index, prefix.Length);
            title = Regex.Replace(title, @"\b" + count + @"\s*pack\b", " ", RegexOptions.CultureInvariant, Timeout);
        }
        // Leftover count/pack wording is ambiguous; never infer its quantity.
        title = Regex.Replace(title, Measure, " ", RegexOptions.CultureInvariant, Timeout);
        if (Regex.IsMatch(title, @"\b(?:pack|packs|multipack|x)\b|×|\d\s*x\b", RegexOptions.CultureInvariant, Timeout)) return null;
        var family = normalisation.Text(title);
        return family.Length == 0 ? null : new(pack.Size.Value * count, pack.Unit, family);
    }

    public static decimal UnitPrice(decimal price, Quantity quantity) => price / quantity.Total * 100m;
    public static decimal? DifferencePercent(decimal original, decimal alternative)
    {
        if (original <= 0) return null;
        try { return (alternative - original) / original * 100m; }
        catch (OverflowException) { return null; }
    }
}
