namespace myshoppinglist_api.Models;

public class ShopProductPriceHistory
{
    public long Id { get; set; }
    public long ShopProductId { get; set; }
    public ShopProduct ShopProduct { get; set; } = null!;
    public long? ShopLocationId { get; set; }
    public ShopLocation? ShopLocation { get; set; }
    public decimal Price { get; set; }
    public decimal? NormalPrice { get; set; }
    public decimal? UnitPrice { get; set; }
    public string Currency { get; set; } = "AUD";
    public string? SpecialType { get; set; }
    public string? SpecialDescription { get; set; }
    public DateTime? SpecialStartDate { get; set; }
    public DateTime? SpecialEndDate { get; set; }
    public PriceScope PriceScope { get; set; } = PriceScope.Unknown;
    public SourceType SourceType { get; set; } = SourceType.Other;
    public string SourceUrl { get; set; } = string.Empty;
    public DateTime CheckedDate { get; set; }
    public DateTime CreatedDate { get; set; }
}

