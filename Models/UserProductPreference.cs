namespace myshoppinglist_api.Models;

public sealed class UserProductPreference
{
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public long ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public bool IsFavorite { get; set; }
    public bool IsEssential { get; set; }
    public DateTime UpdatedDate { get; set; }
}
