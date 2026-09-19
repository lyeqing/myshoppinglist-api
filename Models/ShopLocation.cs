namespace myshoppinglist_api.Models;

public class ShopLocation
{
    public long Id { get; set; }
    public long ShopId { get; set; }
    public Shop Shop { get; set; } = null!;
    public string? StoreCode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Suburb { get; set; }
    public string? State { get; set; }
    public string? Postcode { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
}

