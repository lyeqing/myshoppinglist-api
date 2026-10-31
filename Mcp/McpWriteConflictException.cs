using ModelContextProtocol;

namespace myshoppinglist_api.Mcp;

public sealed record McpWriteConflict(int Version, string Error, bool RefreshRequired, string? ListId = null,
    string? ItemId = null, DateTime? CurrentUpdatedAt = null, string? CurrentSelectionVersion = null);

public sealed class McpWriteConflictException(McpWriteConflict details) : McpException("conflict")
{
    public McpWriteConflict Details { get; } = details;
}
