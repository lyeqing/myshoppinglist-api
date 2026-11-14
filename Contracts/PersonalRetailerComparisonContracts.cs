namespace myshoppinglist_api.Contracts;

public sealed record PersonalComparisonStart(long CaptureJobId);
public sealed record PersonalComparisonResult(Guid StepToken, string Url, bool Ok,
    ColesEvidence? Evidence = null, string[]? Links = null, bool EmptyConfirmed = false,
    string? ErrorCode = null, DateTimeOffset? ObservedAt = null);
public sealed record PersonalComparisonCandidate(string Name, string Url, string Relationship,
    decimal? Price, string? Currency, DateTimeOffset ObservedAt, string? PriceNotice);
public sealed record PersonalComparisonRetailer(string Code, string Name, string Status,
    string? Message, PersonalComparisonCandidate[] ExactMatches, PersonalComparisonCandidate[] Alternatives);
public sealed record PersonalComparisonWork(Guid StepToken, string Retailer, string Kind, string Url);
public sealed record PersonalComparisonResponse(long Id, long ItemId, long CaptureJobId, string Status,
    PersonalComparisonRetailer[] Retailers, PersonalComparisonWork? Work, string ProductName = "");
