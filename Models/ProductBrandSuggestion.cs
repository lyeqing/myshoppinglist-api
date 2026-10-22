namespace myshoppinglist_api.Models;

// Automatically found suggestions are never administrator-approved relationships.
public sealed class ProductBrandSuggestion
{
    public long Id { get; set; }
    public long BrandDiscoveryJobId { get; set; }
    public BrandDiscoveryJob BrandDiscoveryJob { get; set; } = null!;
    public long AlternativeProductId { get; set; }
    public Product AlternativeProduct { get; set; } = null!;
    public long ShopProductId { get; set; }
    public ShopProduct ShopProduct { get; set; } = null!;
    public DateTime DiscoveredDate { get; set; }
}
