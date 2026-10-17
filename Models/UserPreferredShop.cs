namespace myshoppinglist_api.Models;

public sealed class UserPreferredShop
{
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public long ShopId { get; set; }
    public Shop Shop { get; set; } = null!;
    public bool IsEnabled { get; set; } = true;
    public int DisplayOrder { get; set; }
}
