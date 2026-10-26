namespace myshoppinglist_api.Contracts;

// Aggregate counts only: no account identities, product details, URLs or credentials.
public sealed record LegacyProductMigrationAuditReport(
    DateTimeOffset GeneratedAtUtc,
    string Database,
    string Schema,
    bool ReadOnly,
    string IsolationLevel,
    IReadOnlyDictionary<string, long> Counts,
    string[] Limitations);
