namespace myshoppinglist_api.Contracts;

public sealed record AccountResponse(long Id, string DisplayName, bool IsTrial, DateTime? ExpiresDate);
public sealed record CurrentSessionResponse(AccountResponse Account, long? ShoppingListId, DateTime SessionExpiresDate);
public sealed record TrialStartResponse(AccountResponse Account, long ShoppingListId, DateTime SessionExpiresDate, bool Reused);
public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);
public sealed record SignInRequest(string? Email, string? Password);
