using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record BrandAlternativeReviewRequest(
    [property: JsonRequired] bool Approved,
    string? Evidence,
    [property: JsonRequired] long ExpectedReviewId,
    [property: JsonRequired] bool SameUseAndFormatConfirmed,
    [property: JsonRequired] bool DietaryAndCompositionChecked,
    [property: JsonRequired] string? ExpectedProductFingerprint,
    [property: JsonRequired] string? ExpectedAlternativeFingerprint);
public sealed record BrandAlternativeReviewResponse(long Id, long ProductId, long AlternativeProductId,
    long? PreviousReviewId, bool Approved, string Evidence, bool SameUseAndFormatConfirmed,
    bool DietaryAndCompositionChecked, long ReviewerAccountId, DateTime ReviewedAt, bool NeedsReview);
public sealed record BrandAlternative(string Relationship, string Label, ShoppingListItemProductResponse Product,
    decimal TotalQuantity, string Unit, decimal QuantityRatio, PackComparisonPrice Offer,
    PackComparisonPrice? Original, decimal? UnitPriceDifferencePercent, decimal? PackagePriceDifference);

// Products are returned in canonical ID order, regardless of route order. Review
// requests echo these fingerprints so changed evidence cannot be approved unseen.
public sealed record BrandAlternativeReviewProduct(ShoppingListItemProductResponse Product, string? Category,
    string? SubCategory, string? Description, string? ItemDetail, string? GTIN, string? ManufacturerPartNumber,
    string? ModelNumber, bool IsDeleted, string Fingerprint);
public sealed record BrandAlternativeReviewContext(BrandAlternativeReviewProduct Product,
    BrandAlternativeReviewProduct AlternativeProduct, BrandAlternativeReviewResponse[] Reviews);

public sealed record AdminAlternativeProductSearchItem(ShoppingListItemProductResponse Product,
    string? Category, string? SubCategory, bool IsDeleted);
public sealed record AdminAlternativeProductSearchResult(AdminAlternativeProductSearchItem[] Items, bool HasMore);
