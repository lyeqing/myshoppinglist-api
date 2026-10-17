using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record ShopLocationDetails([property: JsonRequired] long ShopId, string? StoreCode, string? Name,
    string? Address1, string? Address2, string? Suburb, string? State, string? Postcode,
    [property: JsonRequired] bool IsActive);
public sealed record ShopLocationCreateRequest(ShopLocationDetails? Details, string? SourceReference, string? Evidence);
public sealed record ShopLocationUpdateRequest(ShopLocationDetails? Details, string? SourceReference, string? Evidence,
    [property: JsonRequired] DateTime ExpectedUpdatedDate);
// This customer-facing shape deliberately excludes reviewer identity and private evidence.
public sealed record ShopLocationSummary(long Id, long ShopId, string ShopName, string? StoreCode, string Name,
    string? Address1, string? Address2, string? Suburb, string? State, string? Postcode, DateTime? VerifiedAt,
    bool BranchPricesSupported = false);
public sealed record ShopLocationSearchResult(ShopLocationSummary[] Items, bool HasMore);
public sealed record ShopLocationAdminSummary(ShopLocationSummary Location, bool IsActive, string? SourceReference,
    long? VerifiedByAccountId, DateTime CreatedDate, DateTime UpdatedDate);
public sealed record ShopLocationAdminSearchResult(ShopLocationAdminSummary[] Items, bool HasMore);
public sealed record ShopLocationReviewResponse(long Id, string Action, string Evidence, string SourceReference,
    long ReviewerAccountId, DateTime ReviewedAt, string? BeforeJson, string AfterJson);
public sealed record ShopLocationAdminDetail(ShopLocationAdminSummary Branch, ShopLocationReviewResponse[] Reviews);
