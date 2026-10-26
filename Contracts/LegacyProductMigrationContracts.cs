namespace myshoppinglist_api.Contracts;

// Preview contains no account identities, product details or credentials.
public sealed record LegacyProductMigrationPlan(string Database, string Schema, bool ReadOnly, bool SchemaReady,
    string PlanHash, int AccountProductPairs, int ProductCopies, int RetailerMappingCopies,
    int CurrentPriceCopies, int HistoryRowCopies, IReadOnlyDictionary<string, long> ReferenceChanges,
    string[] Conflicts, string[] Notes);

public sealed record LegacyProductMigrationResult(bool NoChanges, int ProductCopies, int RetailerMappingCopies,
    int CurrentPriceCopies, int HistoryRowCopies, long ReferencesChanged);
