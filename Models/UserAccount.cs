namespace myshoppinglist_api.Models;

public class UserAccount
{
    public long Id { get; set; }
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsTrial { get; set; }
    public DateTime? ExpiresDate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
}

