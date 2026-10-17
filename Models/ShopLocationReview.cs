namespace myshoppinglist_api.Models;

public sealed class ShopLocationReview
{
    public long Id { get; set; }
    public long ShopLocationId { get; set; }
    public long ReviewerAccountId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;
    public string? BeforeJson { get; set; }
    public string AfterJson { get; set; } = string.Empty;
    public DateTime ReviewedAt { get; set; }
}
