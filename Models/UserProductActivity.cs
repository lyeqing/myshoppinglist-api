namespace myshoppinglist_api.Models;

// The optional item reference is cleared by deletion; the account's product history survives.
public sealed class UserProductActivity
{
    public long Id { get; set; }
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public long ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public long? ShoppingListProductId { get; set; }
    public ShoppingListProduct? ShoppingListProduct { get; set; }
    public DateTime AddedDate { get; set; }
    public DateTime? PurchasedDate { get; set; }
    public int Quantity { get; set; } = 1;
}
