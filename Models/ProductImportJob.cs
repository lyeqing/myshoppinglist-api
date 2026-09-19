namespace myshoppinglist_api.Models;

public class ProductImportJob
{
    public long Id { get; set; }
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public long ShoppingListId { get; set; }
    public ShoppingList ShoppingList { get; set; } = null!;
    public string SourceUrl { get; set; } = string.Empty;
    public string NormalisedSourceUrl { get; set; } = string.Empty;
    public int RequestedQuantity { get; set; } = 1;
    public long? SourceShopId { get; set; }
    public Shop? SourceShop { get; set; }
    public ProductImportJobStatus Status { get; set; } = ProductImportJobStatus.Queued;
    public ProductImportProgressStage ProgressStage { get; set; } = ProductImportProgressStage.Queued;
    public long? ProductId { get; set; }
    public Product? Product { get; set; }
    public long? ShoppingListProductId { get; set; }
    public ShoppingListProduct? ShoppingListProduct { get; set; }
    public int AttemptCount { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? LastActivityDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public DateTime? NextAttemptDate { get; set; }
    // A renewed claim token fences writes from an earlier worker after stale-job recovery.
    public Guid? ClaimToken { get; set; }
    public DateTime? LeaseExpiresDate { get; set; }
}

