using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers.Woolworths;

public sealed class WoolworthsProductParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    public static string? ProductCode(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != "https" || url.Port != 443 || url.UserInfo.Length != 0
            || url.IdnHost is not ("woolworths.com.au" or "www.woolworths.com.au")) return null;
        var match = Regex.Match(url.AbsolutePath, @"^/shop/productdetails/(?<id>[1-9]\d{0,14})(?:/[a-z0-9-]+)?/?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        return match.Success ? match.Groups["id"].Value : null;
    }

    public async Task<ProviderResult<ExtractedShopProduct>> ParseAsync(RetailerPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var code = ProductCode(page.Url);
        if (code is null) return Fail("invalid_product_url", "Use a Woolworths product page URL.", ProviderFailureKind.InvalidUrl);
        using var document = await new HtmlParser().ParseDocumentAsync(page.Html, cancellationToken);
        if (new[] { "Access Denied", "Just a moment", "Robot or human" }.Any(t => (document.Title ?? string.Empty).Contains(t, StringComparison.OrdinalIgnoreCase)))
            return Fail("retailer_access_restricted", "Woolworths restricted access to this page.", ProviderFailureKind.AccessRestricted);
        var products = new List<JsonElement>();
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, new JsonDocumentOptions { MaxDepth = 64 });
                products.AddRange(ProductNodes(json.RootElement).Where(n => Matches(n, code)).Select(n => n.Clone()));
            }
            catch (JsonException) { }
        }
        if (products.Count > 1) return Fail("ambiguous_product", "The page contains conflicting main-product records.");
        var ld = products.SingleOrDefault();
        JsonElement product = default;
        if (document.QuerySelector("script#__NEXT_DATA__") is { } state)
        {
            try
            {
                using var json = JsonDocument.Parse(state.TextContent, new JsonDocumentOptions { MaxDepth = 64 });
                var props = Get(Get(json.RootElement, "props"), "pageProps");
                if (Get(props, "isRestrictedProduct").ValueKind == JsonValueKind.True)
                    return Fail("retailer_access_restricted", "This product is restricted.", ProviderFailureKind.AccessRestricted);
                var candidate = Get(Get(props, "pdDetails"), "Product");
                if (Text(candidate, "Stockcode") is { } actual && actual != code)
                    return Fail("product_identity_conflict", "The returned product does not match the requested product.");
                if (Text(candidate, "Stockcode") == code) product = candidate.Clone();
            }
            catch (JsonException) { }
        }
        if (ld.ValueKind == JsonValueKind.Undefined && product.ValueKind == JsonValueKind.Undefined)
        {
            var missing = document.QuerySelectorAll("h1,h2,h3").Any(h => h.TextContent.Contains("We've looked everywhere for this page", StringComparison.OrdinalIgnoreCase));
            return Fail(missing ? "product_not_found" : "product_not_identified", "The page did not expose a verifiable product identity.",
                missing ? ProviderFailureKind.NotFound : ProviderFailureKind.ParseError);
        }
        if (Get(product, "IsMarketProduct").ValueKind == JsonValueKind.True)
            return Fail("marketplace_not_supported", "Marketplace sellers are not supported.", ProviderFailureKind.NotSupported);
        var name = Text(ld, "name") ?? Text(product, "DisplayName");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 500) return Fail("invalid_product_name", "The product name could not be verified.");
        var gtins = new[] { Text(ld, "gtin"), Text(ld, "gtin8"), Text(ld, "gtin12"), Text(ld, "gtin13"), Text(ld, "gtin14"), Text(product, "Barcode") }
            .Where(v => v is not null).Select(v => v!).ToArray();
        if (gtins.Where(ValidGtin).Select(v => v.PadLeft(14, '0')).Distinct().Count() > 1)
            return Fail("product_identity_conflict", "The page contains conflicting barcodes.");
        var gtin = gtins.FirstOrDefault(ValidGtin);
        var brand = Text(Get(ld, "brand"), "name") ?? String(Get(ld, "brand")) ?? Text(product, "Brand");
        var description = Text(ld, "description") ?? Text(product, "RichDescription");
        int? quantity; decimal? size; string? unit;
        try { (quantity, size, unit) = Pack([name, Text(product, "DisplayName"), Text(product, "PackageSize")]); }
        catch (RegexMatchTimeoutException) { return Fail("invalid_pack_data", "The product pack data could not be read safely."); }
        var image = String(Get(ld, "image")) ?? Text(product, "LargeImageFile");
        var imageUri = Uri.TryCreate(image, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" && parsed.UserInfo.Length == 0 ? parsed : null;
        cancellationToken.ThrowIfCancellationRequested();
        return new ProviderResult<ExtractedShopProduct>.Success(new()
        {
            ShopCode = "woolworths", ShopProductCode = code, Sku = code, ProductUrl = page.Url, ImageUrl = imageUri,
            Description = Trim(description, 10000),
            Identity = new() { Name = name, Brand = Trim(brand, 200), GTIN = gtin,
                PackQuantity = quantity, PackSize = size, PackUnit = unit, ManufacturerPartNumber = Trim(Text(ld, "mpn"), 200) },
            SourceType = ld.ValueKind == JsonValueKind.Undefined ? SourceType.RetailerPage : SourceType.StructuredData,
            CheckedDate = page.CheckedDate, Offer = Offer(ld, product, code, page)
        });
    }

    private static ProviderResult<ShopProductOffer> Offer(JsonElement ld, JsonElement product, string code, RetailerPage page)
    {
        var offers = Get(ld, "offers");
        var candidates = (offers.ValueKind == JsonValueKind.Array ? offers.EnumerateArray().ToArray() : [offers])
            .Where(o => o.ValueKind == JsonValueKind.Object).ToArray();
        if (candidates.Length > 1) return OfferFailure("ambiguous_price", "Multiple offers prevent a reliable single-pack price.");
        var offer = candidates.SingleOrDefault();
        if (Text(offer, "url") is { } url && UrlCode(url) != code)
            return OfferFailure("offer_identity_conflict", "The offer belongs to another product.");
        if (Text(Get(offer, "seller"), "name") is { } seller && !seller.Equals("Woolworths", StringComparison.OrdinalIgnoreCase))
            return OfferFailure("unsupported_seller", "The offer is from another seller.");
        // Variable-weight and member-only prices are not an unconditional single-pack quote.
        if (Text(product, "Unit") is { } sellUnit && !sellUnit.Equals("Each", StringComparison.OrdinalIgnoreCase)
            || Get(product, "IsEdrSpecial").ValueKind == JsonValueKind.True)
            return OfferFailure("unsupported_price_context", "An unconditional single-pack price could not be verified.");
        if (InvalidMoney(Get(offer, "price")) || InvalidMoney(Get(product, "Price")))
            return OfferFailure("invalid_price", "The retailer price is invalid.");
        var ldPrice = Money(Get(offer, "price")); var statePrice = Money(Get(product, "Price"));
        if (ldPrice.HasValue && statePrice.HasValue && ldPrice != statePrice)
            return OfferFailure("price_conflict", "The page contains conflicting prices.");
        var price = ldPrice ?? statePrice;
        if (!price.HasValue || price <= 0) return OfferFailure("price_unavailable", "A current price could not be verified.");
        // Require the explicit currency; never infer it from a dollar sign.
        if (Text(offer, "priceCurrency") != "AUD") return OfferFailure("missing_currency", "The price currency could not be verified.");
        var normal = Money(Get(product, "WasPrice"));
        if (normal < price) normal = null;
        bool? stock = Get(product, "IsInStock").ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => Text(offer, "availability") switch
            { "https://schema.org/InStock" or "http://schema.org/InStock" => true,
              "https://schema.org/OutOfStock" or "http://schema.org/OutOfStock" => false, _ => null }
        };
        return new ProviderResult<ShopProductOffer>.Success(new()
        {
            ShopCode = "woolworths", Price = price.Value, NormalPrice = normal, Currency = "AUD", InStock = stock,
            // JSON-LD unitText contains package size on observed pages, not the unit-price denominator.
            UnitPrice = Text(product, "CupMeasure") is null ? null : Money(Get(product, "CupPrice")),
            UnitPriceUnit = Trim(Text(product, "CupMeasure"), 100),
            SpecialType = Get(product, "IsOnSpecial").ValueKind == JsonValueKind.True ? "Special" : null,
            SpecialDescription = Trim(Text(Get(product, "CentreTag"), "TagContentText") ?? Text(Get(product, "HeaderTag"), "Content"), 2000),
            PriceScope = PriceScope.Unknown, SourceType = product.ValueKind == JsonValueKind.Undefined ? SourceType.StructuredData : SourceType.RetailerPage, SourceUrl = page.Url, CheckedDate = page.CheckedDate
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
