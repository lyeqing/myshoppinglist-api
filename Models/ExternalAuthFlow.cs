namespace myshoppinglist_api.Models;

public sealed class ExternalAuthFlow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long? UserAccountId { get; set; }
    public long? UserSessionId { get; set; }
    public string Provider { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string? Email { get; set; }
    public string? TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}
