using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers.Aldi;

// Validates evidence from an explicitly opened product tab; never fetches ALDI pages.
public sealed class AldiProductParser
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    public static string? ProductCode(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != "https" || url.Port != 443 || url.UserInfo.Length != 0
            || url.IdnHost is not ("aldi.com.au" or "www.aldi.com.au")) return null;
        var match = Regex.Match(url.AbsolutePath, @"^/product/[a-z0-9]+(?:[a-z0-9-]*[a-z0-9])?-(?<id>\d{1,20})/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        return match.Success ? match.Groups["id"].Value : null;
    }

    public ProviderResult<ExtractedShopProduct> Parse(Uri url, ColesEvidence? evidence, DateTimeOffset checkedDate)
    {
        var code = ProductCode(url);
        if (code is null) return Fail("invalid_product_url", "Open an ALDI product page.");
        if (evidence?.JsonLd is not { Length: > 0 and <= 20 } || evidence.NextProductJson is not null
            || evidence.JsonLd.Any(s => s is null) || evidence.JsonLd.Sum(s => (long)s.Length) + (evidence.AldiProductJson?.Length ?? 0) > 2_000_000)
            return Fail("invalid_product_evidence", "ALDI product evidence is missing or too large.");
        try
        {
            var records = new List<JsonElement>();
            foreach (var raw in evidence.JsonLd)
            {
                using var json = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
                records.AddRange(Products(json.RootElement).Where(p => Matches(p, code)).Select(p => p.Clone()));
            }
            if (records.Count != 1) return Fail(records.Count > 1 ? "ambiguous_product" : "product_not_identified", "The main ALDI product could not be identified unambiguously.");
            var product = records[0];
            var name = Text(product, "name");
            var brand = Text(Get(product, "brand"), "name") ?? String(Get(product, "brand"));
            if (name is null || name.Length > 500 || brand?.Length > 200) return Fail("invalid_product_name", "The product name or brand is invalid.");
            if (evidence.AldiProductJson is { } context)
            {
                using var json = JsonDocument.Parse(context, new JsonDocumentOptions { MaxDepth = 8 });
                var canonical = Text(json.RootElement, "canonicalUrl");
                var heading = Text(json.RootElement, "name");
                if (canonical is null || UrlCode(canonical) != code || heading is null || !Normalize(heading).Equals(Normalize(name), StringComparison.OrdinalIgnoreCase))
                    return Fail("product_identity_conflict", "The ALDI heading and product evidence do not match.");
            }
            var gtins = new[] { Text(product, "gtin"), Text(product, "gtin8"), Text(product, "gtin12"), Text(product, "gtin13"), Text(product, "gtin14") }.Where(s => s is not null).ToArray();
            var normalizer = new Services.ProductNormalisationService();
            if (gtins.Any(s => normalizer.Gtin(s!) is null) || gtins.Select(s => normalizer.Gtin(s!)!).Distinct().Count() > 1)
                return Fail("product_identity_conflict", "The ALDI product barcode is invalid or conflicting.");
            var sizes = Regex.Matches(name, @"(?<![\d.])(?<size>\d+(?:\.\d+)?)\s*(?<unit>ml|kg|g|l)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)
                .Select(m => (Size: decimal.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture), Unit: m.Groups["unit"].Value.ToLowerInvariant())).Distinct().ToArray();
            var pack = Regex.Match(name, @"\b(?<count>[1-9]\d{0,4})\s*(?:pack\b|[x×]\s*\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
            var image = Get(product, "image");
            var imageText = image.ValueKind == JsonValueKind.Array ? String(image.EnumerateArray().FirstOrDefault()) : String(image);
            var imageUri = Uri.TryCreate(imageText, UriKind.Absolute, out var parsedImage) && parsedImage.Scheme == "https" && parsedImage.UserInfo.Length == 0 && parsedImage.AbsoluteUri.Length <= 2048 ? parsedImage : null;
            var canonicalUrl = new UriBuilder(url) { Query = "", Fragment = "" }.Uri;
            return new ProviderResult<ExtractedShopProduct>.Success(new()
            {
                ShopCode = "aldi", ProductUrl = canonicalUrl,
                ShopProductCode = code, Sku = Text(product, "sku"), SourceType = SourceType.BrowserExtension, CheckedDate = checkedDate,
                ImageUrl = imageUri,
                Identity = new() { Name = name, Brand = brand, GTIN = gtins.FirstOrDefault(),
                    PackQuantity = pack.Success ? int.Parse(pack.Groups["count"].Value, CultureInfo.InvariantCulture) : null,
                    PackSize = sizes.Length == 1 && sizes[0].Size is > 0 and <= 1_000_000m ? sizes[0].Size : null,
                    PackUnit = sizes.Length == 1 ? sizes[0].Unit == "ml" ? "mL" : sizes[0].Unit == "l" ? "L" : sizes[0].Unit : null },
                Offer = Offer(product, canonicalUrl, checkedDate)
            });
        }
        catch (Exception e) when (e is JsonException or RegexMatchTimeoutException or FormatException or OverflowException or ArgumentException)
        { return Fail("invalid_product_evidence", "The ALDI product evidence could not be validated."); }
    }

    private static ProviderResult<ShopProductOffer> Offer(JsonElement product, Uri url, DateTimeOffset now)
    {
        var offer = Get(product, "offers");
        if (Regex.IsMatch(Text(product, "name") ?? "", @"(?:\bper\s*|/\s*)(?:kg|g|100\s*g|100\s*ml|l|ml)\b|\b(?:variable\s*weight|approx(?:imately)?|avg\.?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout))
            return OfferFailure("variable_weight_price", "This ALDI price is per weight or an approximate pack, not a verified single-package price.");
        if (offer.ValueKind == JsonValueKind.Array) return OfferFailure("ambiguous_price", "Multiple ALDI offers require a verified store context.");
        if (Text(offer, "@type") != "Offer" || Text(offer, "priceCurrency") != "AUD"
            || !decimal.TryParse(Text(offer, "price"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price) || price < 0 || price >= 100_000_000m || decimal.Round(price, 2) != price)
            return OfferFailure("price_unavailable", "An unconditional AUD single-package price is unavailable.");
        if (Text(offer, "url") is { } offerUrl && UrlCode(offerUrl) != ProductCode(url)) return OfferFailure("price_identity_conflict", "This price belongs to another product.");
        foreach (var field in new[] { "validFrom", "priceValidUntil", "validThrough" })
            if (Text(offer, field) is { } raw && (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                || (field == "validFrom" ? date > now : date < now)))
                return OfferFailure("price_not_current", "This advertised ALDI price is not verified as current.");
        var availability = Text(offer, "availability");
        if (availability is not null && !availability.EndsWith("/InStock", StringComparison.Ordinal) && !availability.EndsWith("/OutOfStock", StringComparison.Ordinal))
            return OfferFailure("availability_unverified", "This ALDI offer is not verified as currently available.");
        if (Get(offer, "priceSpecification").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            || Get(offer, "eligibleQuantity").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            return OfferFailure("unsupported_price_context", "Conditional ALDI prices are not treated as single-package prices.");
        return new ProviderResult<ShopProductOffer>.Success(new() { ShopCode = "aldi", Price = price, Currency = "AUD", PriceScope = PriceScope.Unknown,
            SourceType = SourceType.BrowserExtension, SourceUrl = url, CheckedDate = now,
            InStock = availability is null ? null : availability.EndsWith("/InStock", StringComparison.Ordinal) });
    }
    private static bool Matches(JsonElement p, string code)
    {
        var links = new[] { Text(p, "url"), Text(p, "@id"), Text(Get(p, "offers"), "url") }.Where(s => s is not null).ToArray();
        var sku = Text(p, "sku");
        return (links.Any(s => UrlCode(s!) == code) || sku == code) && links.All(s => UrlCode(s!) == code) && (sku is null || sku == code);
    }
    private static IEnumerable<JsonElement> Products(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) foreach (var p in Products(child)) yield return p;
        if (node.ValueKind != JsonValueKind.Object) yield break;
        if (Text(node, "@type") == "Product") yield return node;
        if (Get(node, "@graph") is { ValueKind: JsonValueKind.Array } graph) foreach (var p in Products(graph)) yield return p;
    }
    private static string Normalize(string s) => Regex.Replace(s, @"\s+", " ", RegexOptions.CultureInvariant, Timeout).Trim();
    private static string? UrlCode(string value) => Uri.TryCreate(value, UriKind.Absolute, out var url) ? ProductCode(url) : null;
    private static JsonElement Get(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : default;
    private static string? Text(JsonElement node, string key) => String(Get(node, key));
    private static string? String(JsonElement value) => value.ValueKind == JsonValueKind.String ? string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    private static ProviderResult<ExtractedShopProduct> Fail(string code, string message) => new ProviderResult<ExtractedShopProduct>.Failure(new(ProviderFailureKind.ParseError, code, message));
    private static ProviderResult<ShopProductOffer> OfferFailure(string code, string message) => new ProviderResult<ShopProductOffer>.Failure(new(ProviderFailureKind.ParseError, code, message));
}
