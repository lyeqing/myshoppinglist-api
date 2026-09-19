namespace myshoppinglist_api.Services.Models;

public sealed record ProductImportClaim(long JobId, long UserAccountId, long ShoppingListId, Guid Token);
