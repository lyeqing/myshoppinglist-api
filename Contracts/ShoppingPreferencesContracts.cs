using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record PreferredShopSelection(long ShopId, [property: JsonRequired] bool IsEnabled);
public sealed record PreferredShopLocationSelection(long ShopId, long ShopLocationId);
public sealed record PreferredShopLocationResponse(ShopLocationSummary Location, bool IsAvailable, string? UnavailableReason);
public sealed record ShoppingPreferencesUpdate(
    [property: JsonRequired] long ExpectedAccountId,
    [property: JsonRequired] bool CompareOnlyPreferredShops,
    [property: JsonRequired] bool IncludeDifferentPackSizes,
    [property: JsonRequired] bool IncludeDifferentBrands,
    PreferredShopSelection[]? Shops,
    [property: JsonRequired] DateTime ExpectedUpdatedDate,
    PreferredShopLocationSelection[]? Branches = null);
public sealed record ShoppingPreferenceShop(long Id, string Name, string Code, bool ComparisonSupported,
    string? CapabilityStatus = null, string? CapabilityMessage = null);
public sealed record ShoppingPreferencesResponse(long AccountId, bool CompareOnlyPreferredShops, bool IncludeDifferentPackSizes,
    bool IncludeDifferentBrands, PreferredShopSelection[] Shops, ShoppingPreferenceShop[] AvailableShops, DateTime UpdatedDate,
    PreferredShopLocationResponse[] Branches);
public sealed record ComparisonBranchContext(ShopLocationSummary Location, bool IsAvailable,
    string? UnavailableReason, bool SupermarketEnabled);
public sealed record ComparisonStoreContext(bool PreferredOnly, string[] ShopNames, ComparisonBranchContext[]? Branches = null);
