namespace myshoppinglist_api.Models;

// Retained independently when a selected list is deleted, so versions cannot be reused.
public sealed class UserCurrentListRevision
{
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public long Version { get; set; }
    public DateTime UpdatedDate { get; set; }
}
