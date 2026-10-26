using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using Npgsql;

namespace myshoppinglist_api.Services;

// No HTTP endpoint exists. Preview is read-only; conversion requires a reviewed
// hash and a single, explicit schema. The caller must arrange backup/maintenance.
public sealed class LegacyProductMigrationService(MyShoppingListDbContext db, TimeProvider clock)
{
    private static readonly string[] Tables = ["Products", "ShopProducts", "ShopProductPrices", "ShopProductPriceHistory",
        "ShoppingLists", "ShoppingListProducts", "UserProductPreferences", "UserProductActivities", "ProductImportJobs",
        "ProductImportRetailerResults", "ShoppingListItemSubstitutions", "PackDiscoveryJobs", "BrandDiscoveryJobs",
        "ProductAlternativeRelationships", "ProductBrandSuggestions", "ContributionObservations", "UserExtensionImportTasks",
        "ColesExtensionTasks", "Shops", "UserAccounts", "LegacyProductMigrationMaps", "LegacyShopProductMigrationMaps"];

    public async Task<LegacyProductMigrationPlan> PreviewAsync(CancellationToken ct = default)
    {
        EnsureOwnTransaction();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var state = await ReadAsync(ct);
        var plan = await PlanAsync(state, ct);
        await transaction.RollbackAsync(ct);
        return plan;
    }

    public Task<LegacyProductMigrationResult> RehearseAsync(string expectedPlanHash, CancellationToken ct = default)
        => ConvertAsync(expectedPlanHash, null, null, ct);

    public Task<LegacyProductMigrationResult> ApplyAsync(string expectedDatabase, string expectedSchema,
        string expectedPlanHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedDatabase) || string.IsNullOrWhiteSpace(expectedSchema))
            throw new InvalidOperationException("Supply the reviewed database and schema.");
        return ConvertAsync(expectedPlanHash, expectedDatabase, expectedSchema, ct);
    }

    private async Task<LegacyProductMigrationResult> ConvertAsync(string expectedPlanHash, string? expectedDatabase,
        string? expectedSchema, CancellationToken ct)
    {
        EnsureOwnTransaction();
        var schema = await db.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync(ct);
        var connection = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        if (connection.SearchPath != Quote(schema) && connection.SearchPath != schema)
            throw new InvalidOperationException("Conversion requires a single explicit schema without a search-path fallback.");
        if (expectedDatabase is null && !schema.StartsWith("codex_migration_", StringComparison.Ordinal))
            throw new InvalidOperationException("Rehearsal requires an isolated codex_migration_ schema.");
        if (expectedDatabase is not null && (expectedDatabase != await db.Database.SqlQueryRaw<string>(
                "SELECT current_database() AS \"Value\"").SingleAsync(ct) || expectedSchema != schema))
            throw new InvalidOperationException("The connected database/schema differs from the reviewed target.");
        if (string.IsNullOrWhiteSpace(expectedPlanHash) || expectedPlanHash.Length != 64 || !expectedPlanHash.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Supply the reviewed preview hash.");
        var present = await PresentTablesAsync(ct);
        if (!present.Contains("LegacyProductMigrationMaps") || !present.Contains("LegacyShopProductMigrationMaps"))
            throw new InvalidOperationException("Deploy the prepared bookkeeping migration to the target schema first.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // Bound lock acquisition rather than waiting indefinitely on a writer.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);
            var lockSql = "LOCK TABLE " + string.Join(", ", Tables.Select(Quote)) + " IN SHARE ROW EXCLUSIVE MODE";
            await db.Database.ExecuteSqlRawAsync(lockSql, ct);
            var state = await ReadAsync(ct);
            var plan = await PlanAsync(state, ct);
            if (!plan.SchemaReady) throw new InvalidOperationException("The target schema or deferred reference constraints are not ready.");
            if (plan.Conflicts.Length != 0) throw new InvalidOperationException("Resolve migration conflicts before conversion: " + string.Join("; ", plan.Conflicts));
            // Apply always requires the current preview, including a no-op rerun.
            if (expectedDatabase is not null && !string.Equals(plan.PlanHash, expectedPlanHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The migration preview is stale. Read and review a new preview.");
            if (state.Pairs.Count == 0 && state.ProductMaps.Count > 0)
            {
                await transaction.RollbackAsync(ct);
                return new(true, 0, 0, 0, 0, 0);
            }
            if (!string.Equals(plan.PlanHash, expectedPlanHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The migration preview is stale. Read and review a new preview.");
            await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS ALL DEFERRED", ct);
            var productMaps = state.ProductMaps.ToDictionary(m => new Pair(m.UserAccountId, m.LegacyProductId));
            var shopMaps = state.ShopMaps.ToDictionary(m => (m.UserAccountId, m.LegacyShopProductId));
            var unchanged = await UnchangedFingerprintAsync(ct);
            var copies = new List<(string Table, object Entity)>();
            foreach (var pair in state.Pairs.OrderBy(p => p.Owner).ThenBy(p => p.Product))
            {
                if (productMaps.ContainsKey(pair)) continue; // Never overwrite a user's later edits or prices.
                var product = Copy(state.Products[pair.Product]);
                product.Id = 0; product.UserAccountId = pair.Owner;
                db.Products.Add(product); await db.SaveChangesAsync(ct);
                copies.Add(("Products", product));
                var receipt = new LegacyProductMigrationMap { UserAccountId = pair.Owner, LegacyProductId = pair.Product,
                    PrivateProductId = product.Id, CreatedAt = clock.GetUtcNow().UtcDateTime };
                db.Add(receipt); await db.SaveChangesAsync(ct); productMaps.Add(pair, receipt);
                copies.Add(("LegacyProductMigrationMaps", receipt));
                foreach (var source in state.Mappings.Values.Where(m => m.ProductId == pair.Product && m.UserAccountId is null))
                {
                    var mapping = Copy(source); mapping.Id = 0; mapping.UserAccountId = pair.Owner; mapping.ProductId = product.Id;
                    db.ShopProducts.Add(mapping); await db.SaveChangesAsync(ct);
                    copies.Add(("ShopProducts", mapping));
                    var mapped = new LegacyShopProductMigrationMap { UserAccountId = pair.Owner, LegacyProductId = pair.Product,
                        LegacyShopProductId = source.Id, PrivateShopProductId = mapping.Id, CreatedAt = receipt.CreatedAt };
                    db.Add(mapped); await db.SaveChangesAsync(ct); shopMaps.Add((pair.Owner, source.Id), mapped);
                    copies.Add(("LegacyShopProductMigrationMaps", mapped));
                    foreach (var price in state.Prices.Where(p => p.ShopProductId == source.Id && p.UserAccountId is null
                        && PersonalOwner(state, source, p.ContributionObservationId, p.SourceUrl) == pair.Owner))
                    {
                        var copy = Copy(price); copy.Id = 0; copy.UserAccountId = pair.Owner; copy.ShopProductId = mapping.Id;
                        db.ShopProductPrices.Add(copy);
                        copies.Add(("ShopProductPrices", copy));
                    }
                    foreach (var history in state.History.Where(p => p.ShopProductId == source.Id && p.UserAccountId is null
                        && PersonalOwner(state, source, p.ContributionObservationId, p.SourceUrl) == pair.Owner))
                    {
                        var copy = Copy(history); copy.Id = 0; copy.UserAccountId = pair.Owner; copy.ShopProductId = mapping.Id;
                        db.ShopProductPriceHistory.Add(copy);
                        copies.Add(("ShopProductPriceHistory", copy));
                    }
                    await db.SaveChangesAsync(ct);
                }
            }

            long changed = 0;
            foreach (var pair in state.Pairs)
            {
                var target = productMaps[pair].PrivateProductId;
                changed += await db.ShoppingListProducts.Where(i => i.ShoppingList.UserAccountId == pair.Owner && i.ProductId == pair.Product)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.ProductId, target), ct);
                changed += await db.UserProductPreferences.Where(i => i.UserAccountId == pair.Owner && i.ProductId == pair.Product)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.ProductId, target), ct);
                changed += await db.UserProductActivities.Where(i => i.UserAccountId == pair.Owner && i.ProductId == pair.Product)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.ProductId, target), ct);
                changed += await db.ProductImportJobs.Where(i => i.UserAccountId == pair.Owner && i.ProductId == pair.Product)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.ProductId, target), ct);
                changed += await db.PackDiscoveryJobs.Where(i => i.UserAccountId == pair.Owner && i.ProductId == pair.Product)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.ProductId, target), ct);
                changed += await db.BrandDiscoveryJobs.Where(i => i.UserAccountId == pair.Owner && i.ProductId == pair.Product)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.ProductId, target), ct);
                changed += await db.ShoppingListItemSubstitutions.Where(i => i.UserAccountId == pair.Owner && i.ProductId == pair.Product)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.ProductId, target), ct);
            }
            foreach (var pair in state.Pairs)
                foreach (var source in state.Mappings.Values.Where(m => m.ProductId == pair.Product && m.UserAccountId is null))
                {
                    var target = shopMaps[(pair.Owner, source.Id)].PrivateShopProductId;
                    changed += await db.ProductImportRetailerResults.Where(r => r.ProductImportJob.UserAccountId == pair.Owner && r.ShopProductId == source.Id)
                        .ExecuteUpdateAsync(u => u.SetProperty(r => r.ShopProductId, target), ct);
                    changed += await db.ShoppingListItemSubstitutions.Where(r => r.UserAccountId == pair.Owner && r.ShopProductId == source.Id)
                        .ExecuteUpdateAsync(u => u.SetProperty(r => r.ShopProductId, target), ct);
                }
            await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS ALL IMMEDIATE", ct);
            var expected = Rows(state, productMaps, shopMaps);
            foreach (var copy in copies) expected[copy.Table].Add(Row(copy.Entity));
            var after = await ReadAsync(ct);
            var actual = Rows(after);
            if (changed != plan.ReferenceChanges.Values.Sum() || after.Pairs.Count != 0
                || (await PlanAsync(after, ct)).Conflicts.Length != 0
                || unchanged != await UnchangedFingerprintAsync(ct)
                || expected.Any(table => !table.Value.Order(StringComparer.Ordinal)
                    .SequenceEqual(actual[table.Key].Order(StringComparer.Ordinal))))
                throw new InvalidOperationException("Post-conversion integrity validation failed; conversion will be rolled back.");
            await transaction.CommitAsync(ct);
            return new(changed == 0 && plan.ProductCopies == 0, plan.ProductCopies, plan.RetailerMappingCopies,
                plan.CurrentPriceCopies, plan.HistoryRowCopies, changed);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private void EnsureOwnTransaction()
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Use a dedicated migration context without a caller transaction or pending edits.");
    }

    private T Copy<T>(T source) where T : class => (T)db.Entry(source).CurrentValues.ToObject();

    // Compare every mapped scalar, including dates, notes, offer scope and states.
    // Navigation properties are deliberately excluded. New PostgreSQL timestamps
    // have microsecond precision, so normalize only sub-microsecond CLR ticks.
    private string Row(object entity, params (string Name, object? Value)[] overrides)
    {
        var values = db.Entry(entity).CurrentValues;
        var row = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in values.Properties)
        {
            var value = values[property];
            row[property.Name] = value is DateTime date ? new DateTime(date.Ticks / 10 * 10, date.Kind) : value;
        }
        foreach (var (name, value) in overrides) row[name] = value;
        return System.Text.Json.JsonSerializer.Serialize(row);
    }

    private Dictionary<string, List<string>> Rows(Snapshot s,
        Dictionary<Pair, LegacyProductMigrationMap>? products = null,
        Dictionary<(long, long), LegacyShopProductMigrationMap>? shops = null)
    {
        long ProductId(long owner, long id) => products is not null && products.TryGetValue(new(owner, id), out var map) ? map.PrivateProductId : id;
        long? ShopId(long owner, long? id) => id is { } key && shops is not null && shops.TryGetValue((owner, key), out var map) ? map.PrivateShopProductId : id;
        return new(StringComparer.Ordinal)
        {
            ["Products"] = s.Products.Values.Select(p => Row(p)).ToList(),
            ["ShopProducts"] = s.Mappings.Values.Select(p => Row(p)).ToList(),
            ["ShopProductPrices"] = s.Prices.Select(p => Row(p)).ToList(),
            ["ShopProductPriceHistory"] = s.History.Select(p => Row(p)).ToList(),
            ["LegacyProductMigrationMaps"] = s.ProductMaps.Select(p => Row(p)).ToList(),
            ["LegacyShopProductMigrationMaps"] = s.ShopMaps.Select(p => Row(p)).ToList(),
            ["ShoppingListProducts"] = s.Items.Select(i => Row(i, ("ProductId", ProductId(s.Lists[i.ShoppingListId].UserAccountId, i.ProductId)))).ToList(),
            ["UserProductPreferences"] = s.Preferences.Select(i => Row(i, ("ProductId", ProductId(i.UserAccountId, i.ProductId)))).ToList(),
            ["UserProductActivities"] = s.Activities.Select(i => Row(i, ("ProductId", ProductId(i.UserAccountId, i.ProductId)))).ToList(),
            ["ProductImportJobs"] = s.Jobs.Values.Select(i => Row(i, ("ProductId", i.ProductId is { } id ? ProductId(i.UserAccountId, id) : null))).ToList(),
            ["PackDiscoveryJobs"] = s.PackJobs.Select(i => Row(i, ("ProductId", ProductId(i.UserAccountId, i.ProductId)))).ToList(),
            ["BrandDiscoveryJobs"] = s.BrandJobs.Select(i => Row(i, ("ProductId", ProductId(i.UserAccountId, i.ProductId)))).ToList(),
            ["ProductImportRetailerResults"] = s.Results.Select(i => Row(i, ("ShopProductId", ShopId(s.Jobs[i.ProductImportJobId].UserAccountId, i.ShopProductId)))).ToList(),
            ["ShoppingListItemSubstitutions"] = s.Choices.Select(i => Row(i, ("ProductId", ProductId(i.UserAccountId, i.ProductId)), ("ShopProductId", ShopId(i.UserAccountId, i.ShopProductId)))).ToList()
        };
    }

    private async Task<string> UnchangedFingerprintAsync(CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // These tables are never changed by conversion, including accounts,
        // lists, observations, shared suggestions and archived queue payloads.
        foreach (var table in new[] { "ShoppingLists", "UserAccounts", "Shops", "ContributionObservations",
            "ProductAlternativeRelationships", "ProductBrandSuggestions", "UserExtensionImportTasks", "ColesExtensionTasks" })
        {
            hash.AppendData(Encoding.UTF8.GetBytes(table + "\n"));
            var sql = "SELECT to_jsonb(t)::text AS \"Value\" FROM " + Quote(table) + " t ORDER BY \"Value\"";
            await foreach (var row in db.Database.SqlQueryRaw<string>(sql).AsAsyncEnumerable().WithCancellation(ct))
                hash.AppendData(Encoding.UTF8.GetBytes(row + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private async Task<HashSet<string>> PresentTablesAsync(CancellationToken ct) => (await db.Database.SqlQueryRaw<string>(
        "SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = current_schema()").ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    private async Task<Snapshot> ReadAsync(CancellationToken ct)
    {
        var tables = await PresentTablesAsync(ct);
        var state = new Snapshot { Tables = tables,
            Database = await db.Database.SqlQueryRaw<string>("SELECT current_database() AS \"Value\"").SingleAsync(ct),
            Schema = await db.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync(ct),
            Accounts = (await db.UserAccounts.AsNoTracking().Select(a => a.Id).ToListAsync(ct)).ToHashSet(),
            Products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, ct),
            Mappings = await db.ShopProducts.AsNoTracking().ToDictionaryAsync(p => p.Id, ct),
            Lists = await db.ShoppingLists.AsNoTracking().ToDictionaryAsync(p => p.Id, ct),
            Items = await db.ShoppingListProducts.AsNoTracking().ToListAsync(ct),
            Preferences = await db.UserProductPreferences.AsNoTracking().ToListAsync(ct),
            Activities = await db.UserProductActivities.AsNoTracking().ToListAsync(ct),
            Jobs = await db.ProductImportJobs.AsNoTracking().ToDictionaryAsync(p => p.Id, ct),
            Results = await db.ProductImportRetailerResults.AsNoTracking().ToListAsync(ct),
            Choices = await db.ShoppingListItemSubstitutions.AsNoTracking().ToListAsync(ct),
            PackJobs = await db.PackDiscoveryJobs.AsNoTracking().ToListAsync(ct),
            BrandJobs = await db.BrandDiscoveryJobs.AsNoTracking().ToListAsync(ct),
            Prices = await db.ShopProductPrices.AsNoTracking().ToListAsync(ct),
            History = await db.ShopProductPriceHistory.AsNoTracking().ToListAsync(ct),
            Observations = await db.ContributionObservations.AsNoTracking().ToDictionaryAsync(p => p.Id, ct) };
        if (tables.Contains("LegacyProductMigrationMaps")) state.ProductMaps = await db.LegacyProductMigrationMaps.AsNoTracking().ToListAsync(ct);
        if (tables.Contains("LegacyShopProductMigrationMaps")) state.ShopMaps = await db.LegacyShopProductMigrationMaps.AsNoTracking().ToListAsync(ct);
        void Link(long owner, long? product)
        {
            if (product is { } id && state.Products.TryGetValue(id, out var p) && p.UserAccountId is null)
                state.Pairs.Add(new(owner, id));
        }
        foreach (var item in state.Items) Link(state.Lists[item.ShoppingListId].UserAccountId, item.ProductId);
        foreach (var p in state.Preferences) Link(p.UserAccountId, p.ProductId);
        foreach (var p in state.Activities) Link(p.UserAccountId, p.ProductId);
        foreach (var j in state.Jobs.Values) if (state.Lists[j.ShoppingListId].UserAccountId == j.UserAccountId) Link(j.UserAccountId, j.ProductId);
        foreach (var choice in state.Choices) Link(choice.UserAccountId, state.Mappings[choice.ShopProductId].ProductId);
        return state;
    }

    private async Task<LegacyProductMigrationPlan> PlanAsync(Snapshot s, CancellationToken ct)
    {
        var conflicts = new List<string>();
        void Check(string code, int count) { if (count != 0) conflicts.Add(code + ": " + count); }
        var referenceTables = await db.Database.SqlQueryRaw<string>("""
            SELECT DISTINCT r.relname::text AS "Value" FROM pg_constraint c
            JOIN pg_class r ON r.oid = c.conrelid JOIN pg_namespace n ON n.oid = r.relnamespace
            JOIN pg_class p ON p.oid = c.confrelid JOIN pg_namespace pn ON pn.oid = p.relnamespace
            WHERE c.contype = 'f' AND n.nspname = current_schema() AND pn.nspname = n.nspname
                AND p.relname IN ('Products', 'ShopProducts', 'ShoppingListProducts')
            """).ToListAsync(ct);
        Check("unreviewed product-reference tables", referenceTables.Count(name => !Tables.Contains(name, StringComparer.Ordinal)));
        bool WrongOwner(long owner, long product) => s.Products[product].UserAccountId is { } actual && actual != owner;
        Check("unknown candidate accounts", s.Pairs.Count(p => !s.Accounts.Contains(p.Owner)));
        Check("list item ownership conflicts", s.Items.Count(i => WrongOwner(s.Lists[i.ShoppingListId].UserAccountId, i.ProductId)));
        Check("preference ownership conflicts", s.Preferences.Count(i => WrongOwner(i.UserAccountId, i.ProductId)));
        Check("activity ownership conflicts", s.Activities.Count(i => WrongOwner(i.UserAccountId, i.ProductId)
            || i.ShoppingListProductId is { } id && s.Items.Any(item => item.Id == id && (item.ProductId != i.ProductId || s.Lists[item.ShoppingListId].UserAccountId != i.UserAccountId))));
        Check("import ownership conflicts", s.Jobs.Values.Count(j => s.Lists[j.ShoppingListId].UserAccountId != j.UserAccountId
            || j.ProductId is { } id && WrongOwner(j.UserAccountId, id)));
        Check("retailer mapping ownership conflicts", s.Mappings.Values.Count(m => m.UserAccountId != s.Products[m.ProductId].UserAccountId));
        Check("price ownership conflicts", s.Prices.Count(p => p.UserAccountId != s.Mappings[p.ShopProductId].UserAccountId));
        Check("history ownership conflicts", s.History.Count(p => p.UserAccountId != s.Mappings[p.ShopProductId].UserAccountId));
        Check("chosen alternative ownership conflicts", s.Choices.Count(c => WrongOwner(c.UserAccountId, c.ProductId)
            || WrongOwner(c.UserAccountId, s.Mappings[c.ShopProductId].ProductId)));
        Check("discovery ownership conflicts", s.PackJobs.Count(j => WrongOwner(j.UserAccountId, j.ProductId))
            + s.BrandJobs.Count(j => WrongOwner(j.UserAccountId, j.ProductId)));

        var productMaps = s.ProductMaps.ToDictionary(m => new Pair(m.UserAccountId, m.LegacyProductId));
        Check("invalid product migration receipts", s.ProductMaps.Count(m => !s.Products.TryGetValue(m.LegacyProductId, out var old) || old.UserAccountId is not null
            || !s.Products.TryGetValue(m.PrivateProductId, out var own) || own.UserAccountId != m.UserAccountId));
        Check("invalid retailer migration receipts", s.ShopMaps.Count(m => !s.Mappings.TryGetValue(m.LegacyShopProductId, out var old) || old.UserAccountId is not null || old.ProductId != m.LegacyProductId
            || !s.Mappings.TryGetValue(m.PrivateShopProductId, out var own) || own.UserAccountId != m.UserAccountId
            || !productMaps.TryGetValue(new(m.UserAccountId, m.LegacyProductId), out var parent) || own.ProductId != parent.PrivateProductId));
        var newPairs = s.Pairs.Where(p => !productMaps.ContainsKey(p)).ToHashSet();
        var newMappings = (from pair in newPairs from m in s.Mappings.Values
                           where m.ProductId == pair.Product && m.UserAccountId is null select (Pair: pair, Mapping: m)).ToList();
        Check("candidate retailer URL/code collisions", newMappings.GroupBy(m => (m.Pair.Owner, m.Mapping.ShopId, m.Mapping.ProductUrl)).Count(g => g.Count() > 1)
            + newMappings.Where(m => m.Mapping.ShopProductCode is not null).GroupBy(m => (m.Pair.Owner, m.Mapping.ShopId, m.Mapping.ShopProductCode)).Count(g => g.Count() > 1));
        Check("existing private retailer URL/code collisions", newMappings.Count(c => s.Mappings.Values.Any(m => m.UserAccountId == c.Pair.Owner && m.ShopId == c.Mapping.ShopId
            && (m.ProductUrl == c.Mapping.ProductUrl || c.Mapping.ShopProductCode is not null && m.ShopProductCode == c.Mapping.ShopProductCode))));
        Check("existing private product identity overlaps", newPairs.Count(pair => s.Products.Values.Any(p => p.UserAccountId == pair.Owner
            && (!string.IsNullOrWhiteSpace(p.GTIN) && p.GTIN == s.Products[pair.Product].GTIN || SameIdentity(p, s.Products[pair.Product])))));
        Check("unsafe or missing retailer URLs", newMappings.Count(c => !Uri.TryCreate(c.Mapping.ProductUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0));
        Check("missing retailer receipts for existing copies", s.Pairs.Where(productMaps.ContainsKey).Sum(pair => s.Mappings.Values.Count(m => m.ProductId == pair.Product && m.UserAccountId is null
            && !s.ShopMaps.Any(map => map.UserAccountId == pair.Owner && map.LegacyShopProductId == m.Id))));
        Check("import retailer result product disagreements", s.Results.Count(r => r.ShopProductId is { } id && s.Jobs[r.ProductImportJobId].ProductId is { } product
            && s.Products[product].UserAccountId is null && s.Mappings[id].ProductId != product));
        Check("duplicate items after remapping", s.Items.GroupBy(i => (i.ShoppingListId,
            Product: productMaps.TryGetValue(new(s.Lists[i.ShoppingListId].UserAccountId, i.ProductId), out var map) ? map.PrivateProductId : i.ProductId)).Count(g => g.Count() > 1));
        Check("duplicate preferences after remapping", s.Preferences.GroupBy(i => (i.UserAccountId,
            Product: productMaps.TryGetValue(new(i.UserAccountId, i.ProductId), out var map) ? map.PrivateProductId : i.ProductId)).Count(g => g.Count() > 1));

        var changes = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["listItems"] = s.Items.Count(i => s.Pairs.Contains(new(s.Lists[i.ShoppingListId].UserAccountId, i.ProductId))),
            ["preferences"] = s.Preferences.Count(i => s.Pairs.Contains(new(i.UserAccountId, i.ProductId))),
            ["activities"] = s.Activities.Count(i => s.Pairs.Contains(new(i.UserAccountId, i.ProductId))),
            ["imports"] = s.Jobs.Values.Count(i => i.ProductId is { } id && s.Pairs.Contains(new(i.UserAccountId, id))),
            ["packJobs"] = s.PackJobs.Count(i => s.Pairs.Contains(new(i.UserAccountId, i.ProductId))),
            ["brandJobs"] = s.BrandJobs.Count(i => s.Pairs.Contains(new(i.UserAccountId, i.ProductId))),
            ["choiceProducts"] = s.Choices.Count(i => s.Pairs.Contains(new(i.UserAccountId, i.ProductId))),
            ["choiceRetailers"] = s.Choices.Count(i => s.Pairs.Contains(new(i.UserAccountId, s.Mappings[i.ShopProductId].ProductId))),
            ["importRetailers"] = s.Results.Count(i => i.ShopProductId is { } id && s.Pairs.Contains(new(s.Jobs[i.ProductImportJobId].UserAccountId, s.Mappings[id].ProductId)))
        };
        var schemaReady = s.Tables.Contains("LegacyProductMigrationMaps") && s.Tables.Contains("LegacyShopProductMigrationMaps")
            && s.Tables.Contains("__EFMigrationsHistory")
            && await db.Database.SqlQueryRaw<int>(DeferredConstraintsSql).SingleAsync(ct) == 4
            && await db.Database.SqlQueryRaw<int>(GuardTriggersSql).SingleAsync(ct) == 10
            && await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20261026073219_AddLegacyProductMigrationMaps'").SingleAsync(ct) == 1;
        if (s.Tables.Contains("LegacyProductMigrationMaps") != s.Tables.Contains("LegacyShopProductMigrationMaps")) conflicts.Add("partial migration bookkeeping schema");
        return new(s.Database, s.Schema, true, schemaReady, await FingerprintAsync(s, ct), s.Pairs.Count, newPairs.Count, newMappings.Count,
            s.Prices.Count(p => p.UserAccountId is null && PersonalOwner(s, s.Mappings[p.ShopProductId], p.ContributionObservationId, p.SourceUrl) is { } owner && newPairs.Contains(new(owner, s.Mappings[p.ShopProductId].ProductId))),
            s.History.Count(p => p.UserAccountId is null && PersonalOwner(s, s.Mappings[p.ShopProductId], p.ContributionObservationId, p.SourceUrl) is { } owner && newPairs.Contains(new(owner, s.Mappings[p.ShopProductId].ProductId))),
            changes, conflicts.ToArray(), [
                "Read-only preview. Apply requires an explicit database/schema and the current preview hash. Rehearsals remain restricted to isolated codex_migration_ schemas without a search-path fallback.",
                "Copy account-linked legacy snapshots, including retained inactive/expired records. Do not reactivate accounts or change list IDs, item IDs, notes, quantities, purchase states, preferences or activity dates.",
                "Copy only accepted PersonalAdd evidence with consistent account/import/list/product/URL links. Preserve CheckedDate, CollectedAt, source type and store scope exactly, including differing timestamps. No price is refreshed or relabelled as current.",
                "Keep all original legacy products, mappings, prices and history. Shared/unlinked/ambiguous observations, shared alternative reviews and automatic suggestions are not copied or resumed.",
                "Chosen alternatives retain their saved identity and quantity. Without qualifying private evidence they need review before entering totals; shared approval is not promoted to private approval. Undo remains available.",
                "Schema deployment and live conversion require separate approval, backup and a maintenance window. Re-read the preview after schema deployment or any data changes.",
                "Reference counts describe changed columns; one row may occur in more than one category. Existing receipts prevent replay from overwriting later edits or observations."
            ]);
    }

    private static bool SameIdentity(Product a, Product b) => !string.IsNullOrWhiteSpace(a.Name)
        && string.Equals(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Brand?.Trim(), b.Brand?.Trim(), StringComparison.OrdinalIgnoreCase)
        && a.PackQuantity == b.PackQuantity && a.PackSize == b.PackSize && a.PackUnit == b.PackUnit && a.Variant == b.Variant;

    private static long? PersonalOwner(Snapshot s, ShopProduct mapping, long? observationId, string sourceUrl)
    {
        if (mapping.UserAccountId is not null || s.Products[mapping.ProductId].UserAccountId is not null
            || observationId is not { } id || !s.Observations.TryGetValue(id, out var observation)
            || observation.Source != "PersonalAdd" || observation.Outcome != "Accepted" || observation.TaskId is not null
            || observation.UserAccountId is not { } owner || !s.Accounts.Contains(owner)
            || observation.ImportJobId is not { } jobId || !s.Jobs.TryGetValue(jobId, out var job)
            || job.UserAccountId != owner || s.Lists[job.ShoppingListId].UserAccountId != owner || job.ProductId != mapping.ProductId
            || observation.Url != mapping.ProductUrl || sourceUrl != mapping.ProductUrl) return null;
        return owner;
    }

    private async Task<string> FingerprintAsync(Snapshot s, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add("personal-copy-v1"); Add(s.Database); Add(s.Schema);
        foreach (var table in Tables.Where(s.Tables.Contains))
        {
            Add(table);
            // Account credentials and display information are irrelevant to the plan.
            var projection = table == "UserAccounts" ? "jsonb_build_object('Id', t.\"Id\", 'IsActive', t.\"IsActive\", 'IsTrial', t.\"IsTrial\", 'ExpiresDate', t.\"ExpiresDate\")" : "to_jsonb(t)";
            var sql = "SELECT " + projection + "::text AS \"Value\" FROM " + Quote(table) + " t ORDER BY \"Value\"";
            await foreach (var row in db.Database.SqlQueryRaw<string>(sql)
                .AsAsyncEnumerable().WithCancellation(ct)) Add(row);
        }
        Add((await db.Database.SqlQueryRaw<int>(DeferredConstraintsSql).SingleAsync(ct)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private const string DeferredConstraintsSql = """
        SELECT count(*)::int AS "Value" FROM pg_constraint c
        JOIN pg_class r ON r.oid = c.conrelid JOIN pg_namespace n ON n.oid = r.relnamespace
        JOIN pg_class parent ON parent.oid = c.confrelid JOIN pg_namespace pn ON pn.oid = parent.relnamespace
        WHERE c.contype = 'f' AND c.condeferrable AND NOT c.condeferred AND n.nspname = current_schema() AND pn.nspname = n.nspname
            AND parent.relname = 'ShoppingListProducts'
            AND r.relname IN ('ProductImportJobs', 'PackDiscoveryJobs', 'BrandDiscoveryJobs', 'ShoppingListItemSubstitutions')
        """;

    private const string GuardTriggersSql = """
        SELECT count(*)::int AS "Value" FROM pg_trigger t JOIN pg_class r ON r.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = r.relnamespace WHERE n.nspname = current_schema()
            AND NOT t.tgisinternal AND t.tgenabled IN ('O', 'A') AND current_setting('session_replication_role') = 'origin'
            AND t.tgname IN ('TR_LegacyProductMigrationMaps_Owners', 'TR_LegacyShopProductMigrationMaps_Owners',
                'TR_ShopProducts_KeepMigrationParent', 'TR_ShopProducts_ParentOwner', 'TR_ShopProductPrices_ParentOwner',
                'TR_ShopProductPriceHistory_ParentOwner', 'TR_Products_ImmutableOwner', 'TR_ShopProducts_ImmutableOwner',
                'TR_ShopProductPrices_ImmutableOwner', 'TR_ShopProductPriceHistory_ImmutableOwner')
        """;

    private readonly record struct Pair(long Owner, long Product);
    private sealed class Snapshot
    {
        public string Database { get; init; } = "";
        public string Schema { get; init; } = "";
        public HashSet<string> Tables { get; init; } = [];
        public HashSet<long> Accounts { get; init; } = [];
        public Dictionary<long, Product> Products { get; init; } = [];
        public Dictionary<long, ShopProduct> Mappings { get; init; } = [];
        public Dictionary<long, ShoppingList> Lists { get; init; } = [];
        public List<ShoppingListProduct> Items { get; init; } = [];
        public List<UserProductPreference> Preferences { get; init; } = [];
        public List<UserProductActivity> Activities { get; init; } = [];
        public Dictionary<long, ProductImportJob> Jobs { get; init; } = [];
        public List<ProductImportRetailerResult> Results { get; init; } = [];
        public List<ShoppingListItemSubstitution> Choices { get; init; } = [];
        public List<PackDiscoveryJob> PackJobs { get; init; } = [];
        public List<BrandDiscoveryJob> BrandJobs { get; init; } = [];
        public List<ShopProductPrice> Prices { get; init; } = [];
        public List<ShopProductPriceHistory> History { get; init; } = [];
        public Dictionary<long, ContributionObservation> Observations { get; init; } = [];
        public List<LegacyProductMigrationMap> ProductMaps { get; set; } = [];
        public List<LegacyShopProductMigrationMap> ShopMaps { get; set; } = [];
        public HashSet<Pair> Pairs { get; } = [];
    }
}
