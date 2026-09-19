using myshoppinglist_api.Models;

namespace myshoppinglist_api.Providers.Models;

public abstract record ProviderResult<T>
{
    private ProviderResult() { }
    public sealed record Success(T Value) : ProviderResult<T>;
    public sealed record Failure(ProviderFailure Error) : ProviderResult<T>;
}

public enum ProviderFailureKind
{
    InvalidUrl,
    NotSupported,
    NotFound,
    InvalidProduct,
    AccessRestricted,
    Timeout,
    NetworkError,
    RateLimited,
    RemoteServerError,
    ParseError,
    UnexpectedError
}

public sealed record ProviderFailure(ProviderFailureKind Kind, string Code, string Message)
{
    public TimeSpan? RetryAfter { get; init; }
    public bool IsRetryable => Kind is ProviderFailureKind.Timeout or ProviderFailureKind.NetworkError
        or ProviderFailureKind.RateLimited or ProviderFailureKind.RemoteServerError;

    public RetailerLookupStatus Status => Kind switch
    {
        ProviderFailureKind.NotSupported => RetailerLookupStatus.NotSupported,
        ProviderFailureKind.NotFound => RetailerLookupStatus.NotFound,
        ProviderFailureKind.AccessRestricted or ProviderFailureKind.Timeout or ProviderFailureKind.NetworkError
            or ProviderFailureKind.RateLimited or ProviderFailureKind.RemoteServerError => RetailerLookupStatus.Unavailable,
        _ => RetailerLookupStatus.CheckFailed
    };
}

public sealed record ExtractedShopProduct
{
    public required string ShopCode { get; init; }
    public required Uri ProductUrl { get; init; }
    public required ProductIdentity Identity { get; init; }
    public string? ShopProductCode { get; init; }
    public string? Sku { get; init; }
    public string? Description { get; init; }
    public string? ItemDetail { get; init; }
    public Uri? ImageUrl { get; init; }
    public required SourceType SourceType { get; init; }
    public required DateTimeOffset CheckedDate { get; init; }
    // Identifying a product may succeed even when its current price cannot be verified.
    public ProviderResult<ShopProductOffer>? Offer { get; init; }
}

// Search candidates are evidence for the matching service, not an assertion of an exact match.
public sealed record ShopProductSearchResult(ExtractedShopProduct Product);

public sealed record ShopProductOffer
{
    public required string ShopCode { get; init; }
    public ShopLocationContext? Location { get; init; }
    public required decimal Price { get; init; }
    public decimal? NormalPrice { get; init; }
    public decimal? UnitPrice { get; init; }
    public required string Currency { get; init; }
    public string? UnitPriceUnit { get; init; }
    public string? SpecialType { get; init; }
    public string? SpecialDescription { get; init; }
    public DateTimeOffset? SpecialStartDate { get; init; }
    public DateTimeOffset? SpecialEndDate { get; init; }
    public bool? InStock { get; init; }
    public required PriceScope PriceScope { get; init; }
    public required SourceType SourceType { get; init; }
    public required Uri SourceUrl { get; init; }
    public required DateTimeOffset CheckedDate { get; init; }
}
