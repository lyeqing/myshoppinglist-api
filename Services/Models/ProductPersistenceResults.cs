using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services.Models;

public enum ProductPersistenceStatus
{
    Success, NotFound, AccessDenied, ExpiredAccount, ListUnavailable, LostClaim,
    InvalidData, IdentityConflict, PersistenceConflict
}

public sealed record ProductPersistenceResult(ProductPersistenceStatus Status, string? ErrorCode = null,
    long? ProductId = null, long? ShopProductId = null, long? ShoppingListProductId = null,
    bool ExistingListItem = false, bool PriceSaved = false);

public sealed record ProductMatchResult(MatchType Type, int Confidence, bool Contradiction, string Reason);

public sealed record ProductResolution(ProductPersistenceStatus Status, Product? Product = null,
    string? ErrorCode = null, int MatchConfidence = 100);

public enum PriceUpdateStatus { Saved, Unchanged, OlderObservation, InvalidOffer }
public sealed record PriceUpdateResult(PriceUpdateStatus Status, bool HistoryCreated = false, string? ErrorCode = null);
