namespace myshoppinglist_api.Models;

public sealed class AiConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public string AuthorizationId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string GrantedScopes { get; set; } = "";
    public DateTime CreatedDate { get; set; }
    public DateTime? LastUsedDate { get; set; }
    public DateTime? RevokedDate { get; set; }
}
