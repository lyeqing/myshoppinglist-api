namespace myshoppinglist_api.Models;

public sealed class UserPreferredShopLocation
{
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public long ShopLocationId { get; set; }
    public ShopLocation ShopLocation { get; set; } = null!;
    public int DisplayOrder { get; set; }
}
