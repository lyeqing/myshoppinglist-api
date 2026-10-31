namespace myshoppinglist_api.Contracts;

public sealed record McpMutationResult(int Version, string Status, string RequestId, bool Replayed,
    string? ListId = null, string? ItemId = null, DateTime? UpdatedAt = null, string? SelectionVersion = null,
    bool ListArchived = false);
