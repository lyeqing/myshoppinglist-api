namespace myshoppinglist_api.Contracts;

public sealed record RecoveryRequest(string? Email);
public sealed record ResetPasswordRequest(string? Token, string? NewPassword);
public sealed record ChangePasswordRequest(long ExpectedAccountId, string? CurrentPassword, string? NewPassword);
