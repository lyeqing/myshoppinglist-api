namespace myshoppinglist_api.Models;

// A durable receipt for one account's copy; never a shared product record.
public sealed class LegacyProductMigrationMap
{
    public long UserAccountId { get; set; }
    public long LegacyProductId { get; set; }
    public long PrivateProductId { get; set; }
    public DateTime CreatedAt { get; set; }
}
