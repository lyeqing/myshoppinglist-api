namespace myshoppinglist_api.Models;

// Append-only review events. The latest event for an ordered pair controls publication.
public class ProductAlternativeRelationship
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public long AlternativeProductId { get; set; }
    public Product AlternativeProduct { get; set; } = null!;
    public long? PreviousReviewId { get; set; }
    public bool Approved { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public bool SameUseAndFormatConfirmed { get; set; }
    public bool DietaryAndCompositionChecked { get; set; }
    public string ProductFingerprint { get; set; } = string.Empty;
    public string AlternativeFingerprint { get; set; } = string.Empty;
    public long ReviewerAccountId { get; set; }
    public DateTime ReviewedAt { get; set; }
}
