using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record ShoppingListItemProductResponse(long Id, string Name, string? Brand, string? Variant,
    int? PackQuantity, decimal? PackSize, string? PackUnit, string? ImageUrl);
public sealed record ShoppingListItemResponse(long Id, long ShoppingListId, ShoppingListItemProductResponse Product,
    int Quantity, string? Notes, bool IsPurchased, DateTime? PurchasedDate, bool IsHidden,
    long? PreferredShopId, DateTime AddedDate, DateTime UpdatedDate, bool ListArchived = false);

public sealed record ShoppingListCreateRequest(string? Name);
public sealed record ShoppingListActionRequest([property: JsonRequired] DateTime ExpectedUpdatedDate);
public sealed record ShoppingListSummary(long Id, string Name, DateTime CreatedDate, DateTime UpdatedDate);
public sealed record ShoppingListManagement(int Limit, bool IsPaid, ShoppingListSummary[] Lists);
public sealed record ShoppingListHistory(ShoppingListSummary List, ShoppingListItemResponse[] Items);
public sealed record ShoppingListItemPage(IReadOnlyList<ShoppingListItemResponse> Items, long? NextBeforeId);

public sealed record ShoppingListItemDeleteRequest([property: JsonRequired] DateTime ExpectedUpdatedDate);
public sealed record ShoppingListMultibuy(int Quantity, decimal Total, decimal UnitPrice, decimal Savings);
public sealed record ShoppingListQuantityPrice(int Quantity, decimal Total, decimal OrdinaryTotal,
    decimal Savings, int AppliedBundles, int RemainingQuantity);
public sealed record ShoppingListPrice(long ShopId, string ShopName, decimal? Price, bool IncludedInTotal,
    string Status, string? ProductUrl, DateTime? CheckedDate, string? SpecialDescription,
    string? RefreshStatus = null, ShoppingListMultibuy? Multibuy = null, ShoppingListQuantityPrice? QuantityPrice = null);
public sealed record ShoppingListComparison(long JobId, string Status, string? ErrorCode, DateTime? RetryAfter,
    bool CanRetry, ShoppingListRetailerCheck[] Retailers);
public sealed record ShoppingListRetailerCheck(string Name, string Status, string? ErrorCode);
public sealed record ShoppingListPlanningItem(ShoppingListItemResponse Item, ShoppingListPrice[] Prices,
    ShoppingListComparison? Comparison = null);
public sealed record ShoppingListMissingItem(long ItemId, string Name, string Reason);
public sealed record ShoppingListBasket(long? ShopId, string Name, decimal Subtotal, int PricedCount,
    ShoppingListMissingItem[] MissingItems);
public sealed record ShoppingListPlan(long Id, string Name, ShoppingListPlanningItem[] Items,
    ShoppingListBasket Lowest, ShoppingListBasket[] Retailers);

// PUT replaces the editable fields; explicit null clears notes. The timestamp protects against stale edits.
public sealed record ShoppingListItemUpdateRequest(
    [property: JsonRequired] int Quantity,
    [property: JsonRequired] string? Notes,
    [property: JsonRequired] bool IsPurchased,
    [property: JsonRequired] bool IsHidden,
    [property: JsonRequired] DateTime ExpectedUpdatedDate);
