namespace myshoppinglist_api.Models;

public sealed class ColesExtensionTask
{
    public long Id { get; set; }
    public long? ContributorAccountId { get; set; }
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "product";
    public string Url { get; set; } = "";
    public string? Query { get; set; }
    public string Status { get; set; } = "Waiting";
    public int Attempts { get; set; }
    public int Priority { get; set; } = 1;
    public DateTime? RefreshNotBefore { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public Guid? ClaimToken { get; set; }
    public string? WorkerId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? SubmissionHash { get; set; }
    public string? ErrorCode { get; set; }
}
