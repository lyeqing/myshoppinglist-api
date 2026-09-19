namespace myshoppinglist_api.Models;

public class ShoppingList
{
    public long Id { get; set; }
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public DateTime? ExpiresDate { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
}

