namespace myshoppinglist_api.Contracts;

public sealed record McpCurrentListResult(int Version, string Status, string SelectionVersion, McpListSummary? List);
public sealed record McpProduct(string Id, string Name, string? Brand, string? Variant, int? PackQuantity, decimal? PackSize, string? PackUnit);
public sealed record McpSavedPrice(string StoreId, string Store, string? LocationId, string? Location, decimal? Price,
    string Currency, decimal? UnitPrice, string UnitPriceBasis, DateTime CheckedAt, string Status, string? Conditions,
    string Source, string PriceScope, string? SourceUrl, bool ExactMatch);
public sealed record McpListItem(string Id, McpProduct Product, int Quantity, string QuantityUnit, string? Notes,
    bool Purchased, bool Hidden, DateTime UpdatedAt, McpSavedPrice[] SavedPrices);
public sealed record McpListItemsResult(int Version, string ListId, McpListItem[] Items, string? NextBeforeId);
public sealed record McpSavedProduct(McpProduct Product, bool Favorite, bool Essential, McpSavedPrice[] SavedPrices);
public sealed record McpProductSearchResult(int Version, McpSavedProduct[] Items, int Total, int? NextOffset);
