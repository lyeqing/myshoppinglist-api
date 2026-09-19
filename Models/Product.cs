namespace myshoppinglist_api.Models;

public class Product
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Description { get; set; }
    public string? ItemDetail { get; set; }
    public string? GTIN { get; set; }
    public string? ManufacturerPartNumber { get; set; }
    public string? ModelNumber { get; set; }
    public string? Variant { get; set; }
    public int? PackQuantity { get; set; }
    public decimal? PackSize { get; set; }
    public string? PackUnit { get; set; }
    public string? Category { get; set; }
    public string? SubCategory { get; set; }
    public string? ImageUrl { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
    public bool IsDeleted { get; set; }
}

