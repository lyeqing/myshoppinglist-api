using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Contracts;

public sealed record ProductImportRequest(string? Url, int Quantity = 1);
public sealed record ProductImportAcceptedResponse(long JobId, ProductImportJobStatus Status, int Quantity, bool Reused, string StatusUrl);
public sealed record ImportProductResponse(long Id, string Name, string? Brand, string? Variant,
    int? PackQuantity, decimal? PackSize, string? PackUnit, string? ImageUrl);
public sealed record ImportPriceResponse(decimal Price, decimal? NormalPrice, decimal? UnitPrice, string Currency,
    long? ShopLocationId, PriceScope PriceScope, SourceType SourceType, string SourceUrl, DateTime CheckedDate,
    bool? InStock, string? SpecialType, string? SpecialDescription, DateTime? SpecialStartDate, DateTime? SpecialEndDate);
public sealed record ImportRetailerResponse(long ShopId, string ShopName, RetailerLookupStatus Status,
    MatchType? MatchType, int? MatchConfidence, bool IsFromCache, DateTime? CheckedDate, string? ErrorCode,
    IReadOnlyList<ImportPriceResponse> Prices);
public sealed record ProductImportStatusResponse(long JobId, long ShoppingListId, ProductImportJobStatus Status,
    ProductImportProgressStage ProgressStage, int Quantity, long? ShoppingListProductId, ImportProductResponse? Product,
    DateTime CreatedDate, DateTime? LastActivityDate, DateTime? CompletedDate, DateTime? NextAttemptDate,
    string? ErrorCode, IReadOnlyList<ImportRetailerResponse> Retailers);
