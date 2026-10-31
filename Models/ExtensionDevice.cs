namespace myshoppinglist_api.Models;

public sealed class ExtensionDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public long UserSessionId { get; set; }
    public UserSession UserSession { get; set; } = null!;
    public Guid InstallationId { get; set; }
    public string TokenHash { get; set; } = "";
    public string ExtensionId { get; set; } = "";
    public string ExtensionVersion { get; set; } = "";
    public string[] SupportedRetailers { get; set; } = [];
    public DateTime LinkedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public bool Interactive { get; set; }
    public DateTime? RevokedAt { get; set; }
}
