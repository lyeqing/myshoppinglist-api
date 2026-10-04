namespace myshoppinglist_api.Models;

public sealed class ContributionObservation
{
    public long Id { get; set; }
    public long? UserAccountId { get; set; }
    public long? TaskId { get; set; }
    public long? ImportJobId { get; set; }
    public string Source { get; set; } = "DedicatedWorker";
    public string Url { get; set; } = "";
    public string ExtensionVersion { get; set; } = "unknown";
    public DateTime CollectedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Outcome { get; set; } = "Pending";
}
