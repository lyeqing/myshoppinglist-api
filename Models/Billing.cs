namespace myshoppinglist_api.Models;

public sealed class BillingCustomer
{
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public string CustomerId { get; set; } = "";
    public DateTime? ReconciledAt { get; set; }
    public string SnapshotJson { get; set; } = "{}";
}

public sealed class BillingWebhookEvent
{
    public string Id { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string Status { get; set; } = "pending";
}

public sealed class BillingCheckoutAttempt
{
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public Guid RequestId { get; set; }
    public string Plan { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? Url { get; set; }
}
