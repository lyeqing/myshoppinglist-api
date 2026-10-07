namespace myshoppinglist_api.Contracts;

public sealed record AdminHealthResponse(DateTime CheckedAt, DateTime OutcomesSince, int BlockedWindowMinutes,
    int MaxConcurrentTasks, AdminRetailerHealth[] Retailers);
public sealed record AdminRetailerHealth(string Code, string Name, int Waiting, int Processing, int ExpiredLeases,
    int Failed, DateTime? OldestOutstandingCreatedAt, int CompletedLast24Hours, int FailedLast24Hours,
    decimal? CompletionPercent, int RecentBlockedReports, string WorkloadStatus, DateTime? PausedUntil,
    DateTime? TrialExpiresAt, AdminHealthFailure[] CommonFailures);
public sealed record AdminHealthFailure(string Code, int Count);
