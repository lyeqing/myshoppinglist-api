namespace myshoppinglist_api.Contracts;

public sealed record ContributionPreference(bool Enabled);
public sealed record ContributionResult(Guid ClaimToken, string Url, bool Ok, ColesEvidence? Evidence = null,
    string[]? Links = null, bool EmptyConfirmed = false, string? ErrorCode = null, string? ExtensionVersion = null);
