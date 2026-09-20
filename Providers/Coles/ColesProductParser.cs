using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers.Coles;

public sealed class ColesProductParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public static string? ProductCode(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != "https" || url.Port != 443 || url.UserInfo.Length != 0
            || url.IdnHost is not ("coles.com.au" or "www.coles.com.au")) return null;
        var match = Regex.Match(url.AbsolutePath, @"^/product/(?:[a-z0-9.-]+-)?(?<id>\d{1,15})/?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        return match.Success ? match.Groups["id"].Value : null;
    }

    public async Task<ProviderResult<ExtractedShopProduct>> ParseAsync(RetailerPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var code = ProductCode(page.Url);
        if (code is null) return Fail("invalid_product_url", "Use a Coles product page URL.", ProviderFailureKind.InvalidUrl);
        // HtmlParser parses the supplied text only. It does not load scripts, images, or external resources.
        using var document = await new HtmlParser().ParseDocumentAsync(page.Html, cancellationToken);
        var title = document.Title ?? string.Empty;
        if (title.Contains("Access Denied", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
            return Fail("retailer_access_restricted", "Coles restricted access to this page.", ProviderFailureKind.AccessRestricted);

        var products = new List<JsonElement>();
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, new JsonDocumentOptions { MaxDepth = 64 });
                products.AddRange(ProductNodes(json.RootElement).Where(node => Matches(node, code)).Select(node => node.Clone()));
            }
            catch (JsonException) { /* Embedded state below can still identify the product. */ }
        }
        if (products.Count > 1) return Fail("ambiguous_product", "The page contains conflicting main-product records.");
        var ld = products.SingleOrDefault();
        JsonElement next = default;
        if (document.QuerySelector("script#__NEXT_DATA__") is { } state)
        {
            try
            {
                using var json = JsonDocument.Parse(state.TextContent, new JsonDocumentOptions { MaxDepth = 64 });
                var props = Get(Get(json.RootElement, "props"), "pageProps");
                if (Get(props, "isRestricted").ValueKind == JsonValueKind.True)
                    return Fail("retailer_access_restricted", "Coles restricted access to this product.", ProviderFailureKind.AccessRestricted);
                var candidate = Get(props, "product");
                if (Text(candidate, "id") is { } actual && actual != code)
                    return Fail("product_identity_conflict", "The returned product does not match the requested product.");
                if (Text(candidate, "id") == code) next = candidate.Clone();
            }
            catch (JsonException) { /* Valid JSON-LD remains usable if embedded application state is malformed. */ }
        }
        if (ld.ValueKind == JsonValueKind.Undefined && next.ValueKind == JsonValueKind.Undefined)
            return Fail("product_not_identified", "The page did not expose a verifiable product identity.");

        var brand = Text(Get(ld, "brand"), "name") ?? String(Get(ld, "brand")) ?? Text(next, "brand");
        var name = Text(ld, "name") ?? (Text(next, "name") is { } nextName ? Join(brand, nextName, Text(next, "size")) : null);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 500)
            return Fail("invalid_product_name", "The product name could not be verified.");
        var description = Text(ld, "description") ?? Text(next, "longDescription");
        var identityTexts = new[] { name, Text(next, "name"), Text(next, "size"), description, Text(next, "longDescription") };
        int? quantity;
        decimal? size;
        string? unit;
        try { (quantity, size, unit) = Pack(identityTexts); }
        catch (RegexMatchTimeoutException) { return Fail("invalid_pack_data", "The product pack information could not be read safely."); }
        var gtins = new[] { Text(ld, "gtin"), Text(ld, "gtin8"), Text(ld, "gtin12"), Text(ld, "gtin13"),
            Text(ld, "gtin14"), Text(next, "gtin") }.Where(value => value is not null).Distinct().ToArray();
        var gtin = gtins.Length == 1 && ValidGtin(gtins[0]!) ? gtins[0] : null;
        var image = String(Get(ld, "image"));
        if (image is null && Get(ld, "image").ValueKind == JsonValueKind.Array)
            image = String(Get(ld, "image").EnumerateArray().FirstOrDefault());
        if (image is null && Get(next, "images").ValueKind == JsonValueKind.Array)
        {
            var path = Text(Get(Get(next, "images").EnumerateArray().FirstOrDefault(), "full"), "path");
            if (path?.StartsWith("/wcsstore/", StringComparison.Ordinal) == true) image = "https://shop.coles.com.au" + path;
        }
        var imageUri = Uri.TryCreate(image, UriKind.Absolute, out var parsedImage) && parsedImage.Scheme == "https"
            && parsedImage.UserInfo.Length == 0 ? parsedImage : null;
        cancellationToken.ThrowIfCancellationRequested();
        return new ProviderResult<ExtractedShopProduct>.Success(new()
        {
            ShopCode = "coles", ShopProductCode = code, Sku = Text(ld, "sku") ?? code,
            ProductUrl = page.Url, ImageUrl = imageUri, Description = Trim(description, 10000),
            Identity = new()
            {
                Name = name, Brand = Trim(brand, 200), GTIN = gtin,
                ManufacturerPartNumber = Trim(Text(ld, "mpn"), 200),
                PackQuantity = quantity, PackSize = size, PackUnit = unit
            },
            SourceType = ld.ValueKind == JsonValueKind.Undefined ? SourceType.RetailerPage : SourceType.StructuredData,
            CheckedDate = page.CheckedDate, Offer = Offer(ld, next, code, page)
        });
    }

    private static ProviderResult<ShopProductOffer> Offer(JsonElement ld, JsonElement next, string code, RetailerPage page)
    {
        var offers = Get(ld, "offers");
        var matching = (offers.ValueKind == JsonValueKind.Array ? offers.EnumerateArray().ToArray() : [offers])
            .Where(value => value.ValueKind == JsonValueKind.Object)
            .Where(value => Text(value, "url") is not { } url || UrlCode(url) == code).ToArray();
        if (matching.Length > 1) return OfferFailure("ambiguous_price", "Multiple offers prevent a reliable single-pack price.");
        var offer = matching.SingleOrDefault();
        var pricing = Get(next, "pricing");
        var ldPrice = Money(Get(offer, "price"));
        var nextPrice = Money(Get(pricing, "now"));
        if (InvalidMoney(Get(offer, "price")) || InvalidMoney(Get(pricing, "now")))
            return OfferFailure("invalid_price", "The retailer price is invalid.");
        if (ldPrice.HasValue && nextPrice.HasValue && ldPrice != nextPrice)
            return OfferFailure("price_conflict", "The page contains conflicting prices.");
        var price = ldPrice ?? nextPrice;
        if (!price.HasValue) return OfferFailure("price_unavailable", "A current price could not be verified.");
        var currency = Text(offer, "priceCurrency");
        // Coles' observed embedded pricing is AUD; JSON-LD explicitly confirms this on the source page.
        if (currency is not null && currency != "AUD") return OfferFailure("invalid_currency", "The price currency could not be verified.");
        if (currency is null && nextPrice is null) return OfferFailure("missing_currency", "The price currency is missing.");
        var availability = Text(offer, "availability");
        bool? inStock = availability switch
        {
            "https://schema.org/InStock" or "http://schema.org/InStock" => true,
            "https://schema.org/OutOfStock" or "http://schema.org/OutOfStock" => false,
            _ => Get(next, "availability").ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        };
        var normal = Money(Get(pricing, "was"));
        if (normal < price) normal = null;
        var unit = Get(pricing, "unit");
        return new ProviderResult<ShopProductOffer>.Success(new()
        {
            ShopCode = "coles", Price = price.Value, NormalPrice = normal, Currency = "AUD",
            UnitPrice = Money(Get(unit, "price")), UnitPriceUnit = Text(unit, "ofMeasureUnits") is { } units
                ? Join(Text(unit, "ofMeasureQuantity"), units) : null,
            SpecialType = Text(pricing, "specialType") ?? Text(pricing, "promotionType"),
            SpecialDescription = Text(pricing, "offerDescription") ?? Text(pricing, "priceDescription"),
            InStock = inStock,
            // The anonymous page's implicit location is not the user's chosen store, nor a national price.
            PriceScope = PriceScope.Unknown, SourceType = nextPrice.HasValue ? SourceType.RetailerPage : SourceType.StructuredData,
            SourceUrl = page.Url, CheckedDate = page.CheckedDate
        });
    }

    private static IEnumerable<JsonElement> ProductNodes(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray()) foreach (var product in ProductNodes(child)) yield return product;
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            var type = Get(node, "@type");
            if (String(type) == "Product" || type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => String(t) == "Product"))
                yield return node;
            if (Get(node, "@graph").ValueKind == JsonValueKind.Array)
                foreach (var product in ProductNodes(Get(node, "@graph"))) yield return product;
        }
    }

    private static bool Matches(JsonElement node, string code)
    {
        var sku = Text(node, "sku");
        var url = Text(node, "url") ?? Text(node, "@id");
        return (sku == code || url is not null && UrlCode(url) == code)
            && (sku is null || sku == code) && (url is null || UrlCode(url) == code);
    }

    private static string? UrlCode(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? ProductCode(uri) : null;
    private static JsonElement Get(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : default;
    private static string? Text(JsonElement node, string key) => String(Get(node, key));
    private static string? String(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(node.GetString()) ? null : node.GetString()!.Trim(),
        JsonValueKind.Number => node.GetRawText(), _ => null
    };
    private static string? Trim(string? value, int maximum) => value is null ? null : value[..Math.Min(value.Length, maximum)];
    private static string Join(params string?[] parts) => string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    private static decimal? Money(JsonElement node) => decimal.TryParse(String(node), NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var value) && value >= 0 && value < 100_000_000m ? value : null;
    private static bool InvalidMoney(JsonElement node) => node.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && Money(node) is null;

    private static bool ValidGtin(string value)
    {
        if (value.Length is not (8 or 12 or 13 or 14) || value.Any(c => c is < '0' or > '9')) return false;
        var sum = 0;
        for (var i = value.Length - 2; i >= 0; i--) sum += (value[i] - '0') * ((value.Length - 2 - i) % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10 == value[^1] - '0';
    }

    private static (int? Quantity, decimal? Size, string? Unit) Pack(IEnumerable<string?> texts)
    {
        var quantities = new HashSet<int>();
        var sizes = new HashSet<(decimal Size, string Unit)>();
        foreach (var text in texts.Where(text => text is not null))
        {
            foreach (Match match in Regex.Matches(text!, @"\b(?<q>\d+)\s*(?:pack\b|[x×]\s*\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                if (int.TryParse(match.Groups["q"].Value, out var value) && value > 0) quantities.Add(value);
            foreach (Match match in Regex.Matches(text!, @"(?<![\d.])(?<s>\d+(?:\.\d+)?)\s*(?<u>ml|kg|g|l)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
            {
                if (!decimal.TryParse(match.Groups["s"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                    || value <= 0 || value > 1_000_000m) continue;
                var unit = match.Groups["u"].Value.ToLowerInvariant();
                sizes.Add((unit is "l" or "kg" ? value * 1000 : value, unit is "l" or "ml" ? "mL" : "g"));
            }
        }
        var size = sizes.Count == 1 ? sizes.Single() : ((decimal Size, string Unit)?)null;
        return (quantities.Count == 1 ? quantities.Single() : null, size?.Size, size?.Unit);
    }

    private static ProviderResult<ExtractedShopProduct> Fail(string code, string message, ProviderFailureKind kind = ProviderFailureKind.ParseError) =>
        new ProviderResult<ExtractedShopProduct>.Failure(new(kind, code, message));
    private static ProviderResult<ShopProductOffer> OfferFailure(string code, string message) =>
        new ProviderResult<ShopProductOffer>.Failure(new(ProviderFailureKind.ParseError, code, message));
}
