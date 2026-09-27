namespace myshoppinglist_api.Contracts;

public sealed record ColesTaskSummary(long Id, string Kind, string Url, string? Query, string Status, int Attempts, string? ErrorCode);
public sealed record ColesTaskClaim(long Id, string Kind, string Url, string? Query, Guid ClaimToken, DateTime LeaseExpiresAt);
public sealed record ColesWorkerRequest(string WorkerId);
public sealed record ColesEvidence(string? NextProductJson, string[]? JsonLd);
public sealed record ColesTaskSubmission(string WorkerId, Guid ClaimToken, string Url, bool Ok,
    ColesEvidence? Evidence = null, string[]? Links = null, bool EmptyConfirmed = false, string? ErrorCode = null);
