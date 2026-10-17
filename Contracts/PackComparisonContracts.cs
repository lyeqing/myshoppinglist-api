namespace myshoppinglist_api.Contracts;

// Relationship is separate from ShopProduct.MatchType, which records identity certainty.
public sealed record PackComparisonPrice(long ShopId, string ShopName, decimal? Price, decimal? UnitPrice,
    string Status, DateTime? CheckedDate, string? ProductUrl, PriceObservationContext? Observation = null);
public sealed record PackAlternative(string Relationship, ShoppingListItemProductResponse Product,
    decimal TotalQuantity, string Unit, decimal QuantityRatio, PackComparisonPrice Offer,
    PackComparisonPrice? Original, decimal? UnitPriceDifferencePercent, decimal? PackagePriceDifference);
