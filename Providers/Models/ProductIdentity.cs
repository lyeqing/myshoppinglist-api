namespace myshoppinglist_api.Providers.Models;

// Display values are preserved here; matching will normalise comparison values separately.
public sealed record ProductIdentity
{
    public required string Name { get; init; }
    public string? GTIN { get; init; }
    public string? Brand { get; init; }
    public string? Variant { get; init; }
    public string? ManufacturerPartNumber { get; init; }
    public string? ModelNumber { get; init; }
    public int? PackQuantity { get; init; }
    public decimal? PackSize { get; init; }
    public string? PackUnit { get; init; }
    public string? Category { get; init; }
    public string? SubCategory { get; init; }
}
