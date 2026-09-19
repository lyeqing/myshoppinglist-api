namespace myshoppinglist_api.Models;

public class ShopProduct
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public long ShopId { get; set; }
    public Shop Shop { get; set; } = null!;
    public string? ShopProductCode { get; set; }
    public string? ShopSku { get; set; }
    public string NameAtShop { get; set; } = string.Empty;
    public string? DescriptionAtShop { get; set; }
    public string ProductUrl { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string? GTIN { get; set; }
    public MatchType MatchType { get; set; }
    public int? MatchConfidence { get; set; }
    public DateTime FirstFoundDate { get; set; }
    public DateTime LastFoundDate { get; set; }
    public DateTime? LastCheckedDate { get; set; }
    public bool IsActive { get; set; } = true;
}

