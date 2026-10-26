using System.Data;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;

namespace myshoppinglist_api.Services;

public sealed class LegacyProductMigrationAuditService(MyShoppingListDbContext db, TimeProvider clock)
{
    public async Task<LegacyProductMigrationAuditReport> ReadAsync(CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("The audit requires its own read-only transaction.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var state = await db.Database.SqlQueryRaw<DatabaseState>("""
            SELECT current_database() AS "Database", current_schema() AS "Schema",
                current_setting('transaction_read_only') AS "ReadOnly",
                current_setting('transaction_isolation') AS "Isolation"
            """).SingleAsync(ct);
        if (state.ReadOnly != "on" || state.Isolation != "repeatable read")
            throw new InvalidOperationException("A read-only repeatable-read snapshot is required.");

        var counts = await db.Database.SqlQueryRaw<AuditCount>(CountsSql).ToListAsync(ct);
        // Roll back even this read-only transaction; there is never a data commit.
        await transaction.RollbackAsync(ct);
        return new(clock.GetUtcNow(), state.Database, state.Schema, true, state.Isolation,
            counts.ToDictionary(row => row.Name, row => row.Value, StringComparer.Ordinal),
            [
                "This is an aggregate snapshot, not an approved conversion or a guarantee that records are transferable.",
                "Account-product candidates come only from existing account-owned lists, preferences, activities, import references and chosen substitutions. They include archived, expired and inactive-account records; retention policy is still required.",
                "An attributable current-price candidate requires an accepted PersonalAdd observation, the same account's import and list, the same product, exact URL agreement and the original observation date. SharedCustomer, dedicated-worker and server observations are not personal captures.",
                "Legacy price history has no observation reference. Matching a price or timestamp is not proof of its owner; none is classified as attributable history.",
                "Identity overlaps and retailer URL/code collisions require review. Existing private records must not be overwritten or silently merged.",
                "Reference and conflict categories can overlap. Counts must not be summed as distinct records. No legacy alternative relationship is approved for automatic private copying.",
                "The audit starts no collection workers, performs no extraction or migration, and changes no feature settings. Other processes may change the database after this snapshot."
            ]);
    }

    private sealed class DatabaseState
    {
        public string Database { get; set; } = "";
        public string Schema { get; set; } = "";
        public string ReadOnly { get; set; } = "";
        public string Isolation { get; set; } = "";
    }

    private sealed class AuditCount
    {
        public string Name { get; set; } = "";
        public long Value { get; set; }
    }

    private const string CountsSql = """
        WITH account_references AS (
            SELECT l."UserAccountId" AS owner, i."ProductId" AS product
            FROM "ShoppingListProducts" i JOIN "ShoppingLists" l ON l."Id" = i."ShoppingListId"
            UNION SELECT "UserAccountId", "ProductId" FROM "UserProductPreferences"
            UNION SELECT "UserAccountId", "ProductId" FROM "UserProductActivities"
            UNION SELECT j."UserAccountId", j."ProductId" FROM "ProductImportJobs" j
                JOIN "ShoppingLists" l ON l."Id" = j."ShoppingListId" AND l."UserAccountId" = j."UserAccountId"
                WHERE j."ProductId" IS NOT NULL
            UNION SELECT s."UserAccountId", m."ProductId" FROM "ShoppingListItemSubstitutions" s
                JOIN "ShopProducts" m ON m."Id" = s."ShopProductId"
        ), candidates AS (
            SELECT DISTINCT r.owner, r.product FROM account_references r
            JOIN "UserAccounts" a ON a."Id" = r.owner
            JOIN "Products" p ON p."Id" = r.product AND p."UserAccountId" IS NULL
        ), attributable AS (
            SELECT p."Id", o."UserAccountId" AS owner FROM "ShopProductPrices" p
            JOIN "ShopProducts" m ON m."Id" = p."ShopProductId" AND m."UserAccountId" IS NULL
            JOIN "Products" product ON product."Id" = m."ProductId" AND product."UserAccountId" IS NULL
            JOIN "ContributionObservations" o ON o."Id" = p."ContributionObservationId"
            JOIN "UserAccounts" a ON a."Id" = o."UserAccountId"
            JOIN "ProductImportJobs" j ON j."Id" = o."ImportJobId" AND j."UserAccountId" = o."UserAccountId"
                AND j."ProductId" = m."ProductId"
            JOIN "ShoppingLists" l ON l."Id" = j."ShoppingListId" AND l."UserAccountId" = j."UserAccountId"
            WHERE p."UserAccountId" IS NULL AND o."Source" = 'PersonalAdd' AND o."Outcome" = 'Accepted'
                AND o."TaskId" IS NULL AND o."CollectedAt" = p."CheckedDate"
                AND o."Url" = m."ProductUrl" AND p."SourceUrl" = m."ProductUrl"
        ), personal_evidence AS (
            SELECT o."Outcome", o."CollectedAt", p."CheckedDate", o."TaskId",
                o."Url" AS observation_url, p."SourceUrl" AS price_url, m."ProductUrl" AS mapping_url,
                a."Id" AS account_exists, j."Id" AS import_exists, o."UserAccountId" AS owner,
                j."UserAccountId" AS import_owner, l."UserAccountId" AS list_owner,
                j."ProductId" AS import_product, m."ProductId" AS mapping_product
            FROM "ShopProductPrices" p
            JOIN "ContributionObservations" o ON o."Id" = p."ContributionObservationId" AND o."Source" = 'PersonalAdd'
            JOIN "ShopProducts" m ON m."Id" = p."ShopProductId"
            LEFT JOIN "UserAccounts" a ON a."Id" = o."UserAccountId"
            LEFT JOIN "ProductImportJobs" j ON j."Id" = o."ImportJobId"
            LEFT JOIN "ShoppingLists" l ON l."Id" = j."ShoppingListId"
            WHERE p."UserAccountId" IS NULL
        ), candidate_mappings AS (
            SELECT c.owner, m."Id", m."ShopId", m."ShopProductCode", m."ProductUrl"
            FROM candidates c JOIN "ShopProducts" m ON m."ProductId" = c.product AND m."UserAccountId" IS NULL
        )
        SELECT 'legacyProducts' AS "Name", count(*) AS "Value" FROM "Products" WHERE "UserAccountId" IS NULL
        UNION ALL SELECT 'privateProducts', count(*) FROM "Products" WHERE "UserAccountId" IS NOT NULL
        UNION ALL SELECT 'legacyRetailerMappings', count(*) FROM "ShopProducts" WHERE "UserAccountId" IS NULL
        UNION ALL SELECT 'legacyCurrentPrices', count(*) FROM "ShopProductPrices" WHERE "UserAccountId" IS NULL
        UNION ALL SELECT 'privateCurrentPrices', count(*) FROM "ShopProductPrices" WHERE "UserAccountId" IS NOT NULL
        UNION ALL SELECT 'legacyPriceHistoryWithoutAttribution', count(*) FROM "ShopProductPriceHistory" WHERE "UserAccountId" IS NULL
        UNION ALL SELECT 'legacyListItems', count(*) FROM "ShoppingListProducts" i JOIN "Products" p ON p."Id" = i."ProductId" WHERE p."UserAccountId" IS NULL
        UNION ALL SELECT 'legacyFavoritePreferences', count(*) FROM "UserProductPreferences" f JOIN "Products" p ON p."Id" = f."ProductId" WHERE p."UserAccountId" IS NULL AND f."IsFavorite"
        UNION ALL SELECT 'legacyEssentialPreferences', count(*) FROM "UserProductPreferences" f JOIN "Products" p ON p."Id" = f."ProductId" WHERE p."UserAccountId" IS NULL AND f."IsEssential"
        UNION ALL SELECT 'legacyActivityRows', count(*) FROM "UserProductActivities" f JOIN "Products" p ON p."Id" = f."ProductId" WHERE p."UserAccountId" IS NULL
        UNION ALL SELECT 'legacyImportReferences', count(*) FROM "ProductImportJobs" f JOIN "Products" p ON p."Id" = f."ProductId" WHERE p."UserAccountId" IS NULL
        UNION ALL SELECT 'legacyChosenAlternativeReferences', count(*) FROM "ShoppingListItemSubstitutions" f JOIN "ShopProducts" m ON m."Id" = f."ShopProductId" WHERE m."UserAccountId" IS NULL
        UNION ALL SELECT 'candidateAccountProductPairs', count(*) FROM candidates
        UNION ALL SELECT 'candidateAccounts', count(DISTINCT owner) FROM candidates
        UNION ALL SELECT 'linkedLegacyProducts', count(DISTINCT product) FROM candidates
        UNION ALL SELECT 'unlinkedLegacyProducts', count(*) FROM "Products" p WHERE p."UserAccountId" IS NULL AND NOT EXISTS (SELECT 1 FROM candidates c WHERE c.product = p."Id")
        UNION ALL SELECT 'legacyProductsLinkedToMultipleAccounts', count(*) FROM (SELECT product FROM candidates GROUP BY product HAVING count(*) > 1) shared
        UNION ALL SELECT 'candidatePairsForInactiveOrExpiredAccounts', count(*) FROM candidates c JOIN "UserAccounts" a ON a."Id" = c.owner WHERE NOT a."IsActive" OR (a."IsTrial" AND (a."ExpiresDate" IS NULL OR a."ExpiresDate" <= CURRENT_TIMESTAMP))
        UNION ALL SELECT 'attributableCurrentPriceCandidates', count(*) FROM attributable
        UNION ALL SELECT 'unattributedLegacyCurrentPrices', (SELECT count(*) FROM "ShopProductPrices" WHERE "UserAccountId" IS NULL) - (SELECT count(*) FROM attributable)
        UNION ALL SELECT 'legacyPricesWithSharedCustomerObservations', count(*) FROM "ShopProductPrices" p JOIN "ContributionObservations" o ON o."Id" = p."ContributionObservationId" WHERE p."UserAccountId" IS NULL AND o."Source" = 'SharedCustomer'
        UNION ALL SELECT 'legacyPricesWithMissingObservation', count(*) FROM "ShopProductPrices" p LEFT JOIN "ContributionObservations" o ON o."Id" = p."ContributionObservationId" WHERE p."UserAccountId" IS NULL AND o."Id" IS NULL
        UNION ALL SELECT 'legacyPricesWithRejectedOrInconsistentPersonalEvidence', count(*) FROM "ShopProductPrices" p JOIN "ContributionObservations" o ON o."Id" = p."ContributionObservationId" WHERE p."UserAccountId" IS NULL AND o."Source" = 'PersonalAdd' AND NOT EXISTS (SELECT 1 FROM attributable a WHERE a."Id" = p."Id")
        UNION ALL SELECT 'personalEvidenceNotAccepted', count(*) FROM personal_evidence WHERE "Outcome" <> 'Accepted'
        UNION ALL SELECT 'personalEvidenceMissingAccountOrImport', count(*) FROM personal_evidence WHERE account_exists IS NULL OR import_exists IS NULL
        UNION ALL SELECT 'personalEvidenceAccountListOrProductMismatch', count(*) FROM personal_evidence WHERE account_exists IS NOT NULL AND import_exists IS NOT NULL AND (owner IS DISTINCT FROM import_owner OR owner IS DISTINCT FROM list_owner OR import_product IS DISTINCT FROM mapping_product)
        UNION ALL SELECT 'personalEvidenceUrlMismatch', count(*) FROM personal_evidence WHERE observation_url IS DISTINCT FROM mapping_url OR price_url IS DISTINCT FROM mapping_url
        UNION ALL SELECT 'personalEvidenceObservationDateMismatch', count(*) FROM personal_evidence WHERE "CollectedAt" IS DISTINCT FROM "CheckedDate"
        UNION ALL SELECT 'personalEvidenceLinkedToSharedTask', count(*) FROM personal_evidence WHERE "TaskId" IS NOT NULL
        UNION ALL SELECT 'candidateMappingsWithPrivateUrlOrCodeCollision', count(*) FROM candidate_mappings c WHERE EXISTS (SELECT 1 FROM "ShopProducts" m WHERE m."UserAccountId" = c.owner AND m."ShopId" = c."ShopId" AND (m."ProductUrl" = c."ProductUrl" OR c."ShopProductCode" IS NOT NULL AND m."ShopProductCode" = c."ShopProductCode"))
        UNION ALL SELECT 'candidatePairsWithPrivateGtinOverlap', count(*) FROM candidates c JOIN "Products" p ON p."Id" = c.product WHERE p."GTIN" IS NOT NULL AND p."GTIN" <> '' AND EXISTS (SELECT 1 FROM "Products" own WHERE own."UserAccountId" = c.owner AND own."GTIN" = p."GTIN")
        UNION ALL SELECT 'retailerMappingOwnerConflicts', count(*) FROM "ShopProducts" m JOIN "Products" p ON p."Id" = m."ProductId" WHERE m."UserAccountId" IS DISTINCT FROM p."UserAccountId"
        UNION ALL SELECT 'currentPriceOwnerConflicts', count(*) FROM "ShopProductPrices" p JOIN "ShopProducts" m ON m."Id" = p."ShopProductId" WHERE p."UserAccountId" IS DISTINCT FROM m."UserAccountId"
        UNION ALL SELECT 'priceHistoryOwnerConflicts', count(*) FROM "ShopProductPriceHistory" p JOIN "ShopProducts" m ON m."Id" = p."ShopProductId" WHERE p."UserAccountId" IS DISTINCT FROM m."UserAccountId"
        UNION ALL SELECT 'listItemOwnerConflicts', count(*) FROM "ShoppingListProducts" i JOIN "ShoppingLists" l ON l."Id" = i."ShoppingListId" JOIN "Products" p ON p."Id" = i."ProductId" WHERE p."UserAccountId" IS NOT NULL AND p."UserAccountId" <> l."UserAccountId"
        UNION ALL SELECT 'preferenceOwnerConflicts', count(*) FROM "UserProductPreferences" f JOIN "Products" p ON p."Id" = f."ProductId" WHERE p."UserAccountId" IS NOT NULL AND p."UserAccountId" <> f."UserAccountId"
        UNION ALL SELECT 'activityOwnerConflicts', count(*) FROM "UserProductActivities" f JOIN "Products" p ON p."Id" = f."ProductId" LEFT JOIN "ShoppingListProducts" i ON i."Id" = f."ShoppingListProductId" LEFT JOIN "ShoppingLists" l ON l."Id" = i."ShoppingListId" WHERE (p."UserAccountId" IS NOT NULL AND p."UserAccountId" <> f."UserAccountId") OR (f."ShoppingListProductId" IS NOT NULL AND (i."ProductId" <> f."ProductId" OR l."UserAccountId" <> f."UserAccountId"))
        UNION ALL SELECT 'importOwnerOrItemConflicts', count(*) FROM "ProductImportJobs" j JOIN "ShoppingLists" l ON l."Id" = j."ShoppingListId" LEFT JOIN "Products" p ON p."Id" = j."ProductId" LEFT JOIN "ShoppingListProducts" i ON i."Id" = j."ShoppingListProductId" WHERE l."UserAccountId" <> j."UserAccountId" OR (p."UserAccountId" IS NOT NULL AND p."UserAccountId" <> j."UserAccountId") OR (i."Id" IS NOT NULL AND (i."ShoppingListId" <> j."ShoppingListId" OR i."ProductId" IS DISTINCT FROM j."ProductId"))
        UNION ALL SELECT 'chosenAlternativeOwnerConflicts', count(*) FROM "ShoppingListItemSubstitutions" s JOIN "ShopProducts" m ON m."Id" = s."ShopProductId" JOIN "ShoppingListProducts" i ON i."Id" = s."ShoppingListProductId" JOIN "Products" original ON original."Id" = i."ProductId" WHERE (m."UserAccountId" IS NOT NULL AND m."UserAccountId" <> s."UserAccountId") OR (original."UserAccountId" IS NOT NULL AND original."UserAccountId" <> s."UserAccountId")
        UNION ALL SELECT 'legacyAlternativeReviewRows', count(*) FROM "ProductAlternativeRelationships" r JOIN "Products" p ON p."Id" = r."ProductId" JOIN "Products" a ON a."Id" = r."AlternativeProductId" WHERE p."UserAccountId" IS NULL OR a."UserAccountId" IS NULL
        UNION ALL SELECT 'legacyAutomaticBrandSuggestionRows', count(*) FROM "ProductBrandSuggestions" s JOIN "Products" p ON p."Id" = s."AlternativeProductId" WHERE p."UserAccountId" IS NULL
        """;
}
