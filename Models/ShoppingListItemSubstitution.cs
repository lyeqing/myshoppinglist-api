namespace myshoppinglist_api.Models;

// A deliberate choice for one retailer, preserving the original list item.
public sealed class ShoppingListItemSubstitution
{
    public long Id { get; set; }
    public long UserAccountId { get; set; }
    public long ShoppingListId { get; set; }
    public ShoppingList ShoppingList { get; set; } = null!;
    public long ShoppingListProductId { get; set; }
    public ShoppingListProduct ShoppingListProduct { get; set; } = null!;
    public long ProductId { get; set; }
    public long ShopId { get; set; }
    public Shop Shop { get; set; } = null!;
    public long ShopProductId { get; set; }
    public ShopProduct ShopProduct { get; set; } = null!;
    public int Quantity { get; set; }
    public string OriginalFingerprint { get; set; } = "";
    public string AlternativeFingerprint { get; set; } = "";
    public DateTime UpdatedDate { get; set; }
}
