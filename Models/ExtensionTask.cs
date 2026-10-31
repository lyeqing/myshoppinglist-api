namespace myshoppinglist_api.Models;

public sealed class ExtensionTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public Guid AiConnectionId { get; set; }
    public AiConnection AiConnection { get; set; } = null!;
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Type { get; set; } = "capture";
    public string Retailer { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Query { get; set; }
    // Historical target: list deletion must not erase task outcomes or block deleting a list.
    public long? ShoppingListId { get; set; }
    public int Quantity { get; set; } = 1;
    public string Status { get; set; } = "queued";
    public Guid? DeviceId { get; set; }
    public ExtensionDevice? Device { get; set; }
    public string? ClaimHash { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ResultJson { get; set; }
    public string? CompletionHash { get; set; }
}
