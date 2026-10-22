namespace myshoppinglist_api.Models;

public sealed class BrandDiscoveryJob
{
    public long Id { get; set; }
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public long ShoppingListId { get; set; }
    public ShoppingList ShoppingList { get; set; } = null!;
    public long ShoppingListProductId { get; set; }
    public ShoppingListProduct ShoppingListProduct { get; set; } = null!;
    public long ProductId { get; set; }
    public long ShopId { get; set; }
    public Shop Shop { get; set; } = null!;
    public string Status { get; set; } = "Queued";
    public int Attempts { get; set; }
    public int FoundCount { get; set; }
    public string? ErrorCode { get; set; }
    public long? FailedTaskId { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
    public DateTime? NextAttemptDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTime? LeaseExpiresDate { get; set; }
}
