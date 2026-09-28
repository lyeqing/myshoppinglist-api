namespace myshoppinglist_api.Contracts;

public sealed record UserExtensionStart(long ListId, string Url, int Quantity, ColesEvidence Evidence, Guid RequestId);
public sealed record UserExtensionResult(Guid StepToken, string Url, bool Ok, ColesEvidence? Evidence = null,
    string[]? Links = null, bool EmptyConfirmed = false);
public sealed record UserExtensionWork(long JobId, string Status, bool SourceSaved, string Stage,
    Guid StepToken, string? Url, string? Query, string? ErrorCode);
