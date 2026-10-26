namespace myshoppinglist_api.Models;

public sealed class LegacyShopProductMigrationMap
{
    public long UserAccountId { get; set; }
    public long LegacyShopProductId { get; set; }
    public long LegacyProductId { get; set; }
    public long PrivateShopProductId { get; set; }
    public DateTime CreatedAt { get; set; }
}
