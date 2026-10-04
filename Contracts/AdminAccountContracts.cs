namespace myshoppinglist_api.Contracts;

public sealed record AdminAccountUpdate(bool IsPaid, bool ContributionBlocked, bool IsActive, string Reason, DateTime ExpectedUpdatedDate);
