namespace myshoppinglist_api.Contracts;

public sealed record McpListReadResult(int Version, IReadOnlyList<McpListSummary> Lists);

// String IDs preserve PostgreSQL bigint identity without JSON number precision loss.
public sealed record McpListSummary(string Id, string Name, DateTime CreatedAt, DateTime UpdatedAt);
