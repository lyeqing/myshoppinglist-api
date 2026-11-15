using myshoppinglist_api.Contracts;
using myshoppinglist_api.Providers.Aldi;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public sealed class ResearchEvidenceValidator(ProductUrlValidator urls, ProductNormalisationService normalisation,
    TimeProvider clock)
{
    public static string? ProductCode(string retailer, Uri url) => retailer switch
    {
        "coles" => ColesProductParser.ProductCode(url),
        "woolworths" => WoolworthsProductParser.ProductCode(url),
        "aldi" => AldiProductParser.ProductCode(url),
        _ => null
    };

    public string[] Validate(ResearchCandidateInput r)
    {
        var errors = new List<string>();
        var url = urls.Validate(r.Url);
        if (!url.IsValid || url.Retailer!.Code != r.Retailer || ProductCode(r.Retailer, url.ProductUrl!) is null)
            errors.Add("invalid_product_url");
        var p = r.Identity;
        if (p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 500 || p.Brand?.Length > 200
            || p.Variant?.Length > 200 || p.PackQuantity is <= 0 or > 10000 || p.PackSize is <= 0 or > 1000000
            || p.PackUnit?.Length > 20 || p.Category?.Length > 200 || p.SubCategory?.Length > 200
            || p.ManufacturerPartNumber?.Length > 200 || p.ModelNumber?.Length > 200
            || p.PackSize is { } size && decimal.Round(size, 4) != size)
            errors.Add("invalid_identity");
        if (p?.GTIN is not null && normalisation.Gtin(p.GTIN) is null) errors.Add("invalid_gtin");
        if (string.IsNullOrWhiteSpace(r.Evidence) || r.Evidence.Length > 4000) errors.Add("missing_or_oversized_evidence");
        if (r.ObservedAt == default || r.ObservedAt > clock.GetUtcNow()) errors.Add("invalid_observation_time");
        if (r.Price is < 0 or > 100000 || r.Price is { } price && decimal.Round(price, 4) != price
            || r.Currency != "AUD" || r.Promotion?.Length > 1000) errors.Add("invalid_reported_price");
        if (r.PriceScope is not ("Unknown" or "Online" or "National" or "StoreSpecific")
            || r.PriceScope == "StoreSpecific" && r.ShopLocationId is null) errors.Add("invalid_price_scope");
        if (r.Match is not ("exact" or "different_package" or "alternative" or "uncertain")
            || r.AiConfidence is < 0 or > 100) errors.Add("invalid_match_claim");
        if (r.Tissue is { } t && (t.Rolls is <= 0 or > 1000 || t.SheetsPerRoll is <= 0 or > 10000
            || t.Ply is <= 0 or > 10 || t.SheetWidthMm is <= 0 or > 1000 || t.SheetLengthMm is <= 0 or > 1000))
            errors.Add("invalid_tissue_attributes");
        return errors.ToArray();
    }

    // Reports remain unverified even when their arithmetic is well formed.
    public static (decimal? Value, string? Basis) UnitPrice(ResearchCandidateInput r)
    {
        if (r.Price is not { } price || price is < 0 or > 100000) return (null, null);
        if (r.Tissue is { Rolls: > 0, SheetsPerRoll: > 0, Ply: > 0 } tissue)
            return (decimal.Round(price / ((decimal)tissue.Rolls.Value * tissue.SheetsPerRoll.Value) * 100, 4),
                $"100 sheets; {tissue.Ply} ply; dimensions {tissue.SheetWidthMm?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} x {tissue.SheetLengthMm?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} mm");
        var quantity = new PackComparisonService().Read(r.Identity);
        return quantity is null ? (null, null) : (decimal.Round(PackComparisonService.UnitPrice(price, quantity), 4), "100 " + quantity.Unit);
    }
}
