namespace myshoppinglist_api.Models;

public class ProductImportRetailerResult
{
    public long Id { get; set; }
    public long ProductImportJobId { get; set; }
    public ProductImportJob ProductImportJob { get; set; } = null!;
    public long ShopId { get; set; }
    public Shop Shop { get; set; } = null!;
    public long? ShopProductId { get; set; }
    public ShopProduct? ShopProduct { get; set; }
    public RetailerLookupStatus Status { get; set; } = RetailerLookupStatus.Pending;
    public MatchType? MatchType { get; set; }
    public int? MatchConfidence { get; set; }
    public bool IsFromCache { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? CheckedDate { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
}

