using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record ProductPreferenceResponse(long ProductId, bool IsFavorite, bool IsEssential);
// Update one badge at a time, so independent changes never overwrite the other badge.
public sealed record ProductPreferenceUpdate([property: JsonRequired] long ExpectedAccountId,
    [property: JsonRequired] string Kind, [property: JsonRequired] bool Selected);
public sealed record ProductLibraryItem(ShoppingListItemProductResponse Product, bool IsFavorite,
    bool IsEssential, DateTime? LastAddedDate, DateTime? LastPurchasedDate, int TimesAdded);
public sealed record ProductLibraryPage(long AccountId, ProductLibraryItem[] Items, int Total, int? NextOffset);
public sealed record ProductLibraryAddRequest([property: JsonRequired] long ExpectedAccountId);
public sealed record ProductLibraryAddResponse(long ItemId, bool AlreadyOnList);
