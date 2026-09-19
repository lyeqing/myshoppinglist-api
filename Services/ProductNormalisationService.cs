using System.Globalization;
using System.Text;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Services;

public sealed class ProductNormalisationService
{
    public string Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder();
        var separator = false;
        foreach (var character in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separator && result.Length > 0) result.Append(' ');
                result.Append(character);
                separator = false;
            }
            else separator = true;
        }
        return result.ToString();
    }

    public string? Gtin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = value.Trim();
        if (digits.Length is not (8 or 12 or 13 or 14) || digits.Any(c => c is < '0' or > '9')) return null;
        var sum = 0;
        for (var i = digits.Length - 2; i >= 0; i--) sum += (digits[i] - '0') * ((digits.Length - 2 - i) % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10 == digits[^1] - '0' ? digits.PadLeft(14, '0') : null;
    }

    public (decimal? Size, string? Unit) Pack(decimal? size, string? unit)
    {
        if (size is null || size <= 0 || size > 1_000_000m) return (null, null);
        return Text(unit) switch
        {
            "ml" or "millilitre" or "millilitres" => (size, "mL"),
            "l" or "litre" or "litres" => (size * 1000, "mL"),
            "g" or "gram" or "grams" => (size, "g"),
            "kg" or "kilogram" or "kilograms" => (size * 1000, "g"),
            _ => (size, string.IsNullOrWhiteSpace(unit) ? null : Text(unit))
        };
    }

    public (decimal? Size, string? Unit) ParsePackSize(string value)
    {
        var text = value.Trim();
        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;
        return decimal.TryParse(text[..end], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)
            ? Pack(size, text[end..]) : (null, null);
    }

    public ProductIdentity Normalise(ProductIdentity identity)
    {
        var pack = Pack(identity.PackSize, identity.PackUnit);
        return identity with
        {
            Name = Text(identity.Name), Brand = Optional(identity.Brand), Variant = Optional(identity.Variant),
            GTIN = Gtin(identity.GTIN), ManufacturerPartNumber = Identifier(identity.ManufacturerPartNumber),
            ModelNumber = Identifier(identity.ModelNumber), PackSize = pack.Size, PackUnit = pack.Unit
        };
    }

    // Punctuation in manufacturer identifiers may be significant; normalise casing and spacing only.
    private static string? Identifier(string? value) => string.IsNullOrWhiteSpace(value) ? null
        : value.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private string? Optional(string? value) => Text(value) is { Length: > 0 } text ? text : null;
}
