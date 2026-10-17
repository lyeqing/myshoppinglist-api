using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Services;

public sealed class ProductFamilyMatchingService
{
    private readonly ProductNormalisationService normalisation = new();
    private readonly PackComparisonService packs = new();

    public bool IsDifferentSize(ProductIdentity first, ProductIdentity second)
    {
        var a = normalisation.Normalise(first);
        var b = normalisation.Normalise(second);
        if (string.IsNullOrEmpty(a.Brand) || a.Brand != b.Brand || a.Variant != b.Variant
            || a.ModelNumber != b.ModelNumber
            || a.Category is not null && b.Category is not null && a.Category != b.Category
            || a.SubCategory is not null && b.SubCategory is not null && a.SubCategory != b.SubCategory) return false;
        var x = packs.Read(first);
        var y = packs.Read(second);
        // Full residual wording retains dietary claims, flavours, numeric formulation
        // and format. Similarity alone cannot establish a product family.
        return x is not null && y is not null && x.Unit == y.Unit && x.Total != y.Total
            && x.FamilyName == y.FamilyName
            && !(a.GTIN is not null && a.GTIN == b.GTIN);
    }
}
