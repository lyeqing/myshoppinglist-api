namespace myshoppinglist_api.Models;

public sealed class FeedbackTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Category { get; set; } = "other";
    public string Message { get; set; } = "";
    public int? Rating { get; set; }
    public string Component { get; set; } = "website";
    public string Release { get; set; } = "";
    public byte[]? Screenshot { get; set; }
    public string Status { get; set; } = "new";
    public string? Response { get; set; }
    public string? IssueReference { get; set; }
    public long Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OperationalEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; }
    public string Component { get; set; } = "";
    public string Name { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Release { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? TraceId { get; set; }
    public long? UserAccountId { get; set; }
    public UserAccount? UserAccount { get; set; }
    public Guid? VisitorId { get; set; }
    public Guid? SessionId { get; set; }
    public string? Attribution { get; set; }
    public string? ClientId { get; set; }
    public int DurationMs { get; set; }
    public int StatusCode { get; set; }
}

public sealed class DailyOperationalSummary
{
    public DateOnly Day { get; set; }
    public string Component { get; set; } = "";
    public string Name { get; set; } = "";
    public string Outcome { get; set; } = "";
    public long Events { get; set; }
    public long VisitorEstimate { get; set; }
    public long Sessions { get; set; }
    public long Accounts { get; set; }
    public long TotalDurationMs { get; set; }
}

public sealed class OperationalIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Fingerprint { get; set; } = "";
    public string Severity { get; set; } = "P2";
    public string Component { get; set; } = "";
    public string Status { get; set; } = "open";
    public long Version { get; set; } = 1;
    public DateTime OpenedAt { get; set; }
    public DateTime LastObservedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public sealed class AlertDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public OperationalIncident Incident { get; set; } = null!;
    public long IncidentVersion { get; set; }
    public string Kind { get; set; } = "open";
    public string Status { get; set; } = "queued";
    public int Attempts { get; set; }
    public int RecipientCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

public sealed class OperationsAdminAudit
{
    public long Id { get; set; }
    public long AdministratorId { get; set; }
    public UserAccount Administrator { get; set; } = null!;
    public string Action { get; set; } = "";
    public string? Subject { get; set; }
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
