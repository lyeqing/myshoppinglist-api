using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record PreferredShopSelection(long ShopId, [property: JsonRequired] bool IsEnabled);
public sealed record ShoppingPreferencesUpdate(
    [property: JsonRequired] long ExpectedAccountId,
    [property: JsonRequired] bool CompareOnlyPreferredShops,
    [property: JsonRequired] bool IncludeDifferentPackSizes,
    [property: JsonRequired] bool IncludeDifferentBrands,
    PreferredShopSelection[]? Shops,
    [property: JsonRequired] DateTime ExpectedUpdatedDate);
public sealed record ShoppingPreferenceShop(long Id, string Name, string Code, bool ComparisonSupported);
public sealed record ShoppingPreferencesResponse(long AccountId, bool CompareOnlyPreferredShops, bool IncludeDifferentPackSizes,
    bool IncludeDifferentBrands, PreferredShopSelection[] Shops, ShoppingPreferenceShop[] AvailableShops, DateTime UpdatedDate);
public sealed record ComparisonStoreContext(bool PreferredOnly, string[] ShopNames);
