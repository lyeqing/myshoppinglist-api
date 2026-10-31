namespace myshoppinglist_api.Models;

public sealed class UserCurrentList
{
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public long ShoppingListId { get; set; }
    public ShoppingList ShoppingList { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime UpdatedDate { get; set; }
}
