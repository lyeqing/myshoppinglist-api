namespace myshoppinglist_api.Models;

// One private, resumable comparison per committed capture receipt.
public sealed class PersonalRetailerComparison
{
    public long Id { get; set; }
    public long UserAccountId { get; set; }
    public long CaptureJobId { get; set; }
    public ProductImportJob CaptureJob { get; set; } = null!;
    public string StateJson { get; set; } = "{}";
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
}
