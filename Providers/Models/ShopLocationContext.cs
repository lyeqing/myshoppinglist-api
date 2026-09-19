namespace myshoppinglist_api.Providers.Models;

public sealed record ShopLocationContext
{
    public required string ShopCode { get; init; }
    public long? ShopLocationId { get; init; }
    public string? StoreCode { get; init; }
    public string? Suburb { get; init; }
    public string? State { get; init; }
    public string? Postcode { get; init; }
}
