using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record ShoppingListItemProductResponse(long Id, string Name, string? Brand, string? Variant,
    int? PackQuantity, decimal? PackSize, string? PackUnit, string? ImageUrl);
public sealed record ShoppingListItemResponse(long Id, long ShoppingListId, ShoppingListItemProductResponse Product,
    int Quantity, string? Notes, bool IsPurchased, DateTime? PurchasedDate, bool IsHidden,
    long? PreferredShopId, DateTime AddedDate, DateTime UpdatedDate);
public sealed record ShoppingListItemPage(IReadOnlyList<ShoppingListItemResponse> Items, long? NextBeforeId);

// PUT replaces the editable fields; explicit null clears notes. The timestamp protects against stale edits.
public sealed record ShoppingListItemUpdateRequest(
    [property: JsonRequired] int Quantity,
    [property: JsonRequired] string? Notes,
    [property: JsonRequired] bool IsPurchased,
    [property: JsonRequired] bool IsHidden,
    [property: JsonRequired] DateTime ExpectedUpdatedDate);
