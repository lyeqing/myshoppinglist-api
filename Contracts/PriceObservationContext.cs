using myshoppinglist_api.Models;

namespace myshoppinglist_api.Contracts;

// Deliberately excludes observation IDs, contributor identity and private evidence.
public sealed record PriceObservationContext(SourceType SourceType, PriceScope PriceScope, string? SourceUrl);
