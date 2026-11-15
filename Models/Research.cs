namespace myshoppinglist_api.Models;

// Research evidence is private and untrusted. It never writes eligible retailer prices.
public sealed class ResearchJob
{
    public Guid Id { get; set; }
    public long UserAccountId { get; set; }
    public string Query { get; set; } = "";
    public string WorkflowVersion { get; set; } = "";
    public string Status { get; set; } = "AwaitingSelection";
    public DateTime CreatedAt { get; set; }
}

public sealed class ResearchCandidate
{
    public Guid Id { get; set; }
    public long UserAccountId { get; set; }
    public Guid ResearchJobId { get; set; }
    public string Key { get; set; } = "";
    public string ReportJson { get; set; } = "{}";
    public string Validation { get; set; } = "Rejected";
    public string IssuesJson { get; set; } = "[]";
    public long? SelectedProductId { get; set; }
}

public sealed class ResearchIssue
{
    public Guid Id { get; set; }
    public long UserAccountId { get; set; }
    public Guid ResearchJobId { get; set; }
    public string Retailer { get; set; } = "";
    public string Code { get; set; } = "";
    public string Target { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Status { get; set; } = "Open";
    public int Revision { get; set; } = 1;
    public string? Resolution { get; set; }
}
