namespace myshoppinglist_api.Contracts;

public sealed record RecoveryRequest(string? Email);
public sealed record ResetPasswordRequest(string? Token, string? NewPassword);
public sealed record ChangePasswordRequest(long ExpectedAccountId, string? CurrentPassword, string? NewPassword);
public sealed record AccountProfileView(long AccountId, string DisplayName, string? Email, DateTime UpdatedDate,
    bool HasPassword, bool EmailChangeAvailable, Guid? PendingEmailId, string? PendingEmail, DateTime? PendingEmailExpiresAt);
public sealed record UpdateProfileRequest(long ExpectedAccountId, DateTime ExpectedUpdatedDate, string? DisplayName);
public sealed record RequestEmailChange(long ExpectedAccountId, string? ExpectedEmail, string? Email, string? CurrentPassword);
public sealed record CompleteEmailChange(long ExpectedAccountId, string? Token);
public sealed record CancelEmailChange(long ExpectedAccountId, Guid PendingEmailId);
