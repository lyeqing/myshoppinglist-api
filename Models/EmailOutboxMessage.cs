namespace myshoppinglist_api.Models;

public sealed class AccountCredentialChange
{
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public DateTime ChangedAt { get; set; }
}

public sealed class EmailOutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "";
    public string? RecipientHash { get; set; }
    public string? ProtectedPayload { get; set; }
    public string Status { get; set; } = "queued";
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
}
