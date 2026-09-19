namespace myshoppinglist_api.Models;

public class UserSession
{
    public long Id { get; set; }
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public DateTime ExpiresDate { get; set; }
    public DateTime? RevokedDate { get; set; }
}

