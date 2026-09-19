namespace myshoppinglist_api.Models;

public class ShoppingListProduct
{
    public long Id { get; set; }
    public long ShoppingListId { get; set; }
    public ShoppingList ShoppingList { get; set; } = null!;
    public long ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }
    public bool IsPurchased { get; set; }
    public DateTime? PurchasedDate { get; set; }
    public bool IsHidden { get; set; }
    public long? PreferredShopId { get; set; }
    public Shop? PreferredShop { get; set; }
    public DateTime AddedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
}

