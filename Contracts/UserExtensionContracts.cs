namespace myshoppinglist_api.Contracts;

public sealed record PersonalProductCaptureRequest(long ListId, string Url, int Quantity, ColesEvidence Evidence,
    Guid RequestId, DateTimeOffset ObservedAt);
public sealed record PersonalProductCaptureResponse(long JobId, long ProductId, long ShoppingListProductId,
    int Quantity, bool PriceSaved, bool AlreadyOnList, bool Reused, DateTimeOffset ObservedAt)
{
    public string Status => "Completed";
}

public sealed record UserExtensionStart(long ListId, string Url, int Quantity, ColesEvidence Evidence, Guid RequestId);
public sealed record UserExtensionResult(Guid StepToken, string Url, bool Ok, ColesEvidence? Evidence = null,
    string[]? Links = null, bool EmptyConfirmed = false, string? ErrorCode = null);
public sealed record UserExtensionWork(long JobId, string Status, bool SourceSaved, string Stage,
    Guid StepToken, string? Url, string? Query, string? ErrorCode);
