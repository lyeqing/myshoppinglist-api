namespace myshoppinglist_api.Models;

public sealed class ExternalIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public string Provider { get; set; } = "";
    public string Subject { get; set; } = "";
    // Display/collision hint only. Never a local credential or an account-linking proof.
    public string? ProviderEmail { get; set; }
    public DateTime CreatedAt { get; set; }
}
