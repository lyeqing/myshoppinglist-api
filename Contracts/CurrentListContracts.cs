using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record CurrentListResponse(long SelectionVersion, ShoppingListSummary? List, string Status);
public sealed record CurrentListUpdateRequest([property: JsonRequired] long ExpectedAccountId,
    [property: JsonRequired] long ShoppingListId, [property: JsonRequired] long ExpectedSelectionVersion);
