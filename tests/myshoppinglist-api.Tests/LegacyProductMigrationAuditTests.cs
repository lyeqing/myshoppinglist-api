using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class LegacyProductMigrationAuditTests
{
    [PostgreSqlFact]
    public async Task Empty_audit_is_read_only_repeatable_and_leaves_every_table_unchanged()
    {
        await using var f = await AuditScope.CreateAsync();
        var before = await f.FingerprintAsync();
        var first = await f.Audit.ReadAsync();
        var second = await f.Audit.ReadAsync();
        Assert.True(first.ReadOnly);
        Assert.Equal("repeatable read", first.IsolationLevel);
        Assert.StartsWith("codex_test_", first.Schema);
        Assert.All(first.Counts.Values, value => Assert.Equal(0, value));
        Assert.True(first.Counts.Count > 30);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(before, await f.FingerprintAsync());
        Assert.Empty(f.Db.ChangeTracker.Entries());
    }

    [PostgreSqlFact]
    public async Task Candidates_preserve_account_links_without_treating_them_as_verified_private_copies()
    {
        await using var f = await AuditScope.CreateAsync();
        var (a, b, listA, listB) = await f.AccountsAsync();
        b.IsActive = false;
        listB.IsArchived = true;
        var legacy = f.Product(null); legacy.GTIN = "9300000000002";
        var own = f.Product(a.Id); own.GTIN = legacy.GTIN;
        var unused = f.Product(null); unused.IsDeleted = true;
        var mapping = f.Mapping(legacy);
        var ownMapping = f.Mapping(own);
        ownMapping.ShopProductCode = mapping.ShopProductCode;
        ownMapping.ProductUrl = mapping.ProductUrl;
        var itemA = f.Item(listA, legacy);
        var itemB = f.Item(listB, legacy);
        f.Db.AddRange(ownMapping, mapping, unused, itemA, itemB);
        await f.Db.SaveChangesAsync();
        f.Db.UserProductPreferences.Add(new() { UserAccountId = a.Id, ProductId = legacy.Id,
            IsFavorite = true, IsEssential = true, UpdatedDate = f.Now });
        f.Db.UserProductActivities.AddRange(
            new() { UserAccountId = a.Id, ProductId = legacy.Id, ShoppingListProductId = itemA.Id, Quantity = 2, AddedDate = f.Now },
            new() { UserAccountId = b.Id, ProductId = legacy.Id, Quantity = 1, AddedDate = f.Now });
        f.Db.ProductImportJobs.Add(f.Job(a.Id, listA.Id, legacy.Id));
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var before = await f.FingerprintAsync();
        var report = await f.Audit.ReadAsync();
        Assert.Equal(2, report.Counts["legacyProducts"]);
        Assert.Equal(1, report.Counts["privateProducts"]);
        Assert.Equal(2, report.Counts["candidateAccountProductPairs"]);
        Assert.Equal(2, report.Counts["candidateAccounts"]);
        Assert.Equal(1, report.Counts["linkedLegacyProducts"]);
        Assert.Equal(1, report.Counts["unlinkedLegacyProducts"]);
        Assert.Equal(1, report.Counts["legacyProductsLinkedToMultipleAccounts"]);
        Assert.Equal(1, report.Counts["candidatePairsForInactiveOrExpiredAccounts"]);
        Assert.Equal(2, report.Counts["legacyListItems"]);
        Assert.Equal(1, report.Counts["legacyFavoritePreferences"]);
        Assert.Equal(1, report.Counts["legacyEssentialPreferences"]);
        Assert.Equal(2, report.Counts["legacyActivityRows"]);
        Assert.Equal(1, report.Counts["legacyImportReferences"]);
        Assert.Equal(1, report.Counts["candidateMappingsWithPrivateUrlOrCodeCollision"]);
        Assert.Equal(1, report.Counts["candidatePairsWithPrivateGtinOverlap"]);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("do-not-print", json);
        Assert.DoesNotContain("example.test", json);
        Assert.DoesNotContain(mapping.ProductUrl, json);
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Attribution_requires_personal_accepted_matching_evidence_and_never_infers_history_ownership()
    {
        await using var f = await AuditScope.CreateAsync();
        var (a, b, listA, _) = await f.AccountsAsync();
        var product = f.Product(null);
        f.Db.Products.Add(product); await f.Db.SaveChangesAsync();
        var job = f.Job(a.Id, listA.Id, product.Id);
        f.Db.ProductImportJobs.Add(job); await f.Db.SaveChangesAsync();
        ShopProduct? acceptedMapping = null;
        foreach (var kind in new[] { "accepted", "shared", "rejected", "wrong-account", "wrong-date", "wrong-url",
            "missing-observation", "server", "no-job", "missing-account", "shared-task" })
        {
            var mapping = f.Mapping(product);
            var observation = new ContributionObservation { UserAccountId = a.Id, ImportJobId = job.Id,
                Source = "PersonalAdd", Outcome = "Accepted", Url = mapping.ProductUrl,
                CollectedAt = f.Now, ReceivedAt = f.Now };
            if (kind == "shared") observation.Source = "SharedCustomer";
            if (kind == "rejected") observation.Outcome = "Rejected";
            if (kind == "wrong-account") observation.UserAccountId = b.Id;
            if (kind == "wrong-date") observation.CollectedAt = f.Now.AddMinutes(-1);
            if (kind == "wrong-url") observation.Url = "https://www.coles.com.au/product/99999999";
            if (kind == "server") { observation.Source = "Server"; observation.UserAccountId = null; }
            if (kind == "no-job") observation.ImportJobId = null;
            if (kind == "missing-account") observation.UserAccountId = 99999999;
            if (kind == "shared-task") observation.TaskId = 99999999;
            if (kind != "missing-observation") { f.Db.Add(observation); await f.Db.SaveChangesAsync(); }
            f.Db.ShopProductPrices.Add(new() { ShopProduct = mapping,
                ContributionObservationId = kind == "missing-observation" ? null : observation.Id,
                Price = 4, SourceUrl = mapping.ProductUrl, CheckedDate = f.Now, CreatedDate = f.Now, UpdatedDate = f.Now });
            await f.Db.SaveChangesAsync();
            if (kind == "accepted") acceptedMapping = mapping;
        }
        // Even an identical current price/date provides no explicit history observation link.
        f.Db.ShopProductPriceHistory.AddRange(Enumerable.Range(0, 2).Select(_ => new ShopProductPriceHistory {
            ShopProductId = acceptedMapping!.Id, Price = 4, SourceUrl = acceptedMapping.ProductUrl,
            CheckedDate = f.Now, CreatedDate = f.Now }));
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var before = await f.FingerprintAsync();
        var report = await f.Audit.ReadAsync();
        Assert.Equal(11, report.Counts["legacyCurrentPrices"]);
        Assert.Equal(1, report.Counts["attributableCurrentPriceCandidates"]);
        Assert.Equal(10, report.Counts["unattributedLegacyCurrentPrices"]);
        Assert.Equal(1, report.Counts["legacyPricesWithSharedCustomerObservations"]);
        Assert.Equal(1, report.Counts["legacyPricesWithMissingObservation"]);
        Assert.Equal(7, report.Counts["legacyPricesWithRejectedOrInconsistentPersonalEvidence"]);
        Assert.Equal(1, report.Counts["personalEvidenceNotAccepted"]);
        Assert.Equal(2, report.Counts["personalEvidenceMissingAccountOrImport"]);
        Assert.Equal(1, report.Counts["personalEvidenceAccountListOrProductMismatch"]);
        Assert.Equal(1, report.Counts["personalEvidenceUrlMismatch"]);
        Assert.Equal(1, report.Counts["personalEvidenceObservationDateMismatch"]);
        Assert.Equal(1, report.Counts["personalEvidenceLinkedToSharedTask"]);
        Assert.Equal(2, report.Counts["legacyPriceHistoryWithoutAttribution"]);
        Assert.Equal(1, report.Counts["candidateAccountProductPairs"]);
        Assert.Contains(report.Limitations, text => text.Contains("not proof of its owner"));
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Cross_owner_references_are_reported_instead_of_silently_repaired()
    {
        await using var f = await AuditScope.CreateAsync();
        var (a, b, listA, _) = await f.AccountsAsync();
        var foreign = f.Product(b.Id);
        var legacy = f.Product(null);
        var foreignItem = f.Item(listA, foreign);
        var originalItem = f.Item(listA, legacy);
        var mapping = f.Mapping(foreign);
        var brokenMapping = f.Mapping(foreign); brokenMapping.UserAccountId = a.Id;
        f.Db.AddRange(foreignItem, originalItem, mapping, brokenMapping);
        await f.Db.SaveChangesAsync();
        f.Db.UserProductPreferences.Add(new() { UserAccountId = a.Id, ProductId = foreign.Id, IsFavorite = true, UpdatedDate = f.Now });
        f.Db.UserProductActivities.Add(new() { UserAccountId = a.Id, ProductId = foreign.Id, AddedDate = f.Now });
        f.Db.ProductImportJobs.Add(f.Job(a.Id, listA.Id, foreign.Id));
        f.Db.ShopProductPrices.Add(new() { UserAccountId = b.Id, ShopProductId = brokenMapping.Id,
            Price = 3, CheckedDate = f.Now, CreatedDate = f.Now, UpdatedDate = f.Now });
        f.Db.ShopProductPriceHistory.Add(new() { UserAccountId = b.Id, ShopProductId = brokenMapping.Id,
            Price = 3, CheckedDate = f.Now, CreatedDate = f.Now });
        f.Db.ShoppingListItemSubstitutions.Add(new() { UserAccountId = a.Id, ShoppingListId = listA.Id,
            ShoppingListProductId = originalItem.Id, ProductId = legacy.Id, ShopId = 1,
            ShopProductId = mapping.Id, Quantity = 1, OriginalFingerprint = "fixture", AlternativeFingerprint = "fixture", UpdatedDate = f.Now });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var before = await f.FingerprintAsync();
        var report = await f.Audit.ReadAsync();
        foreach (var key in new[] { "retailerMappingOwnerConflicts", "currentPriceOwnerConflicts", "priceHistoryOwnerConflicts",
            "listItemOwnerConflicts", "preferenceOwnerConflicts", "activityOwnerConflicts", "importOwnerOrItemConflicts", "chosenAlternativeOwnerConflicts" })
            Assert.Equal(1, report.Counts[key]);
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task PostgreSql_rejects_an_accidental_write_inside_the_audit_and_the_transaction_is_disposed()
    {
        await using var f = await AuditScope.CreateAsync();
        var before = await f.FingerprintAsync();
        var probe = new WriteProbe();
        await using var db = f.Context(probe);
        var error = await Assert.ThrowsAsync<PostgresException>(() => new LegacyProductMigrationAuditService(db, f.Clock).ReadAsync());
        Assert.Equal("25006", error.SqlState);
        Assert.True(probe.Attempted);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Cancellation_and_an_existing_transaction_do_not_change_caller_data_or_transaction_mode()
    {
        await using var f = await AuditScope.CreateAsync();
        var before = await f.FingerprintAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Audit.ReadAsync(cancellation.Token));
        await using (var transaction = await f.Db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Audit.ReadAsync());
            Assert.Same(transaction, f.Db.Database.CurrentTransaction);
            Assert.Equal("off", await f.Db.Database.SqlQueryRaw<string>("SELECT current_setting('transaction_read_only') AS \"Value\"").SingleAsync());
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Command_exits_with_json_without_starting_the_host_or_enabled_collection_workers()
    {
        await using var f = await AuditScope.CreateAsync();
        var before = await f.FingerprintAsync();
        var result = await RunCommandAsync(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!);
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("readOnly").GetBoolean());
        Assert.StartsWith("codex_test_", json.RootElement.GetProperty("schema").GetString());
        Assert.DoesNotContain("Now listening", result.Output);
        Assert.Empty(result.Error);
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [Fact]
    public async Task Command_reports_connection_failure_without_disclosing_credentials()
    {
        var result = await RunCommandAsync("Host=127.0.0.1;Port=1;Database=never_connect;Username=private_user;Password=do-not-print-password;Timeout=1");
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("No migrations or database changes were requested", result.Error);
        Assert.DoesNotContain("do-not-print", result.Error);
        Assert.DoesNotContain("private_user", result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(string connection)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")) };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--audit-personal-products");
        start.Environment["ConnectionStrings__MyShoppingList"] = connection;
        start.Environment["ProductImport__Enabled"] = "true";
        start.Environment["PriceRefresh__Enabled"] = "true";
        start.Environment["UserExtension__PersonalCapturesEnabled"] = "false";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("The audit command did not exit; it must never start the web host."); }
        return (process.ExitCode, await output, await error);
    }

    private sealed class WriteProbe : DbCommandInterceptor
    {
        public bool Attempted { get; private set; }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText == "SET TRANSACTION READ ONLY")
            {
                Attempted = true;
                await using var write = command.Connection!.CreateCommand();
                write.Transaction = command.Transaction;
                write.CommandText = "UPDATE \"Products\" SET \"Name\" = \"Name\" WHERE FALSE";
                await write.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class AuditScope(IsolatedDatabaseScope isolated) : IAsyncDisposable
    {
        public PersistenceClock Clock { get; } = new() { Now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero) };
        public DateTime Now => Clock.Now.UtcDateTime;
        public MyShoppingListDbContext Db { get; } = new(new DbContextOptionsBuilder<MyShoppingListDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!).Options);
        private int code = 100000;
        public LegacyProductMigrationAuditService Audit => new(Db, Clock);
        public static async Task<AuditScope> CreateAsync() => new(await IsolatedDatabaseScope.CreateAsync());
        public MyShoppingListDbContext Context(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<MyShoppingListDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!).AddInterceptors(interceptors).Options);
        public async Task<(UserAccount A, UserAccount B, ShoppingList ListA, ShoppingList ListB)> AccountsAsync()
        {
            var a = new UserAccount { DisplayName = "do-not-print-a", Email = "audit-a@example.test",
                PasswordHash = "fixture", PasswordSalt = "fixture", CreatedDate = Now, UpdatedDate = Now };
            var b = new UserAccount { DisplayName = "do-not-print-b", Email = "audit-b@example.test",
                PasswordHash = "fixture", PasswordSalt = "fixture", CreatedDate = Now, UpdatedDate = Now };
            var listA = new ShoppingList { UserAccount = a, Name = "do-not-print-list-a", CreatedDate = Now, UpdatedDate = Now };
            var listB = new ShoppingList { UserAccount = b, Name = "do-not-print-list-b", CreatedDate = Now, UpdatedDate = Now };
            Db.AddRange(listA, listB); await Db.SaveChangesAsync(); return (a, b, listA, listB);
        }
        public Product Product(long? owner) => new() { UserAccountId = owner, Name = "do-not-print-product", CreatedDate = Now, UpdatedDate = Now };
        public ShopProduct Mapping(Product product) => new() { Product = product, UserAccountId = product.UserAccountId,
            ShopId = 1, ShopProductCode = (++code).ToString(), ProductUrl = "https://www.coles.com.au/product/" + code,
            NameAtShop = product.Name, FirstFoundDate = Now, LastFoundDate = Now };
        public ShoppingListProduct Item(ShoppingList list, Product product) => new() { ShoppingList = list,
            Product = product, Quantity = 2, Notes = "do-not-print-note", AddedDate = Now, UpdatedDate = Now };
        public ProductImportJob Job(long account, long list, long product) => new() { UserAccountId = account,
            ShoppingListId = list, ProductId = product, SourceUrl = "https://www.coles.com.au/product/100000",
            NormalisedSourceUrl = "https://www.coles.com.au/product/100000", RequestedQuantity = 1,
            Status = ProductImportJobStatus.Completed, ProgressStage = ProductImportProgressStage.Completed, CreatedDate = Now, CompletedDate = Now };
        public async Task<string> FingerprintAsync()
        {
            var tables = await Db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = current_schema() ORDER BY tablename").ToArrayAsync();
            var rows = new StringBuilder();
            foreach (var table in tables)
            {
                var sql = "SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text AS \"Value\" FROM \"" + table.Replace("\"", "\"\"") + "\" AS t";
                rows.Append(table).Append(await Db.Database.SqlQueryRaw<string>(sql).SingleAsync());
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rows.ToString())));
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await isolated.DisposeAsync(); }
    }
}
