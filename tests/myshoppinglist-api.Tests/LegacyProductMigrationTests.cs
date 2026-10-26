using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;
using myshoppinglist_api.Contracts;
using Npgsql;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class LegacyProductMigrationTests
{
    [PostgreSqlFact]
    public async Task Preview_is_read_only_before_schema_deployment_and_reports_only_personal_prices()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(latest: false);
        await f.SeedAsync();
        var before = await f.FingerprintAsync();
        var plan = await f.Service.PreviewAsync();
        Assert.True(plan.ReadOnly); Assert.False(plan.SchemaReady); Assert.Empty(plan.Conflicts);
        Assert.Equal(3, plan.AccountProductPairs); Assert.Equal(3, plan.ProductCopies);
        Assert.Equal(5, plan.RetailerMappingCopies); Assert.Equal(1, plan.CurrentPriceCopies); Assert.Equal(1, plan.HistoryRowCopies);
        Assert.Equal(2, plan.ReferenceChanges["listItems"]);
        Assert.Equal(2, plan.ReferenceChanges["preferences"]);
        Assert.Equal(1, plan.ReferenceChanges["choiceProducts"]);
        Assert.Equal(1, plan.ReferenceChanges["choiceRetailers"]);
        Assert.Equal(plan.PlanHash, (await f.Service.PreviewAsync()).PlanHash);
        Assert.Equal(before, await f.FingerprintAsync());
        var json = JsonSerializer.Serialize(plan);
        Assert.DoesNotContain("private-note", json); Assert.DoesNotContain("example.test", json);
        Assert.DoesNotContain(f.Source.Name, json); Assert.DoesNotContain(f.SourceMap.ProductUrl, json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RehearseAsync(plan.PlanHash));
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Rehearsal_preserves_edits_account_states_provenance_and_all_dependent_references()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var archived = await f.FingerprintAsync(legacyOnly: true);
        var plan = await f.Service.PreviewAsync(); Assert.True(plan.SchemaReady); Assert.Empty(plan.Conflicts);
        var result = await f.Service.RehearseAsync(plan.PlanHash);
        Assert.Equal(3, result.ProductCopies); Assert.Equal(5, result.RetailerMappingCopies);
        Assert.Equal(1, result.CurrentPriceCopies); Assert.Equal(1, result.HistoryRowCopies);
        Assert.Equal(plan.ReferenceChanges.Values.Sum(), result.ReferencesChanged);
        var receipts = await f.Db.LegacyProductMigrationMaps.AsNoTracking().ToArrayAsync();
        var own = receipts.Single(m => m.UserAccountId == f.A.Id && m.LegacyProductId == f.Source.Id).PrivateProductId;
        var other = receipts.Single(m => m.UserAccountId == f.B.Id && m.LegacyProductId == f.Source.Id).PrivateProductId;
        var alternative = receipts.Single(m => m.UserAccountId == f.A.Id && m.LegacyProductId == f.Alternative.Id).PrivateProductId;
        Assert.NotEqual(own, other);
        var item = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == f.ItemA.Id);
        Assert.Equal(own, item.ProductId); Assert.Equal(3, item.Quantity); Assert.Equal("private-note-a", item.Notes);
        Assert.True(item.IsPurchased); Assert.True(item.IsHidden); Assert.Equal(f.ItemA.UpdatedDate, item.UpdatedDate);
        Assert.Equal(f.ItemA.AddedDate, item.AddedDate); Assert.Equal(f.ItemA.PurchasedDate, item.PurchasedDate);
        var itemB = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == f.ItemB.Id);
        Assert.Equal(other, itemB.ProductId); Assert.Equal(7, itemB.Quantity);
        Assert.False((await f.Db.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == f.B.Id)).IsActive);
        Assert.True((await f.Db.ShoppingLists.AsNoTracking().SingleAsync(l => l.Id == f.ListB.Id)).IsArchived);
        Assert.Equal(f.ListB.ExpiresDate, (await f.Db.ShoppingLists.AsNoTracking().SingleAsync(l => l.Id == f.ListB.Id)).ExpiresDate);
        var preference = await f.Db.UserProductPreferences.AsNoTracking().SingleAsync(p => p.UserAccountId == f.A.Id);
        Assert.Equal(own, preference.ProductId); Assert.True(preference.IsFavorite); Assert.True(preference.IsEssential);
        Assert.Equal(f.Past, preference.UpdatedDate);
        var activity = await f.Db.UserProductActivities.AsNoTracking().SingleAsync();
        Assert.Equal(own, activity.ProductId); Assert.Equal(f.ItemA.Id, activity.ShoppingListProductId);
        Assert.Equal(3, activity.Quantity); Assert.Equal(f.Past, activity.AddedDate); Assert.Equal(f.Past, activity.PurchasedDate);
        Assert.Equal(own, (await f.Db.ProductImportJobs.AsNoTracking().SingleAsync(j => j.Id == f.JobA.Id)).ProductId);
        Assert.Equal(other, (await f.Db.ProductImportJobs.AsNoTracking().SingleAsync(j => j.Id == f.JobB.Id)).ProductId);
        Assert.All(await f.Db.PackDiscoveryJobs.AsNoTracking().ToArrayAsync(), j => { Assert.Equal(own, j.ProductId); Assert.Equal("Completed", j.Status); });
        Assert.All(await f.Db.BrandDiscoveryJobs.AsNoTracking().ToArrayAsync(), j => { Assert.Equal(own, j.ProductId); Assert.Equal("Completed", j.Status); });
        var choice = await f.Db.ShoppingListItemSubstitutions.AsNoTracking().SingleAsync();
        Assert.Equal(own, choice.ProductId); Assert.Equal(alternative, (await f.Db.ShopProducts.AsNoTracking().SingleAsync(m => m.Id == choice.ShopProductId)).ProductId);
        Assert.Equal(2, choice.Quantity); Assert.Equal("original-fingerprint", choice.OriginalFingerprint);
        Assert.Equal(f.Past, choice.UpdatedDate);
        foreach (var row in await f.Db.ProductImportRetailerResults.AsNoTracking().Include(r => r.ShopProduct).Include(r => r.ProductImportJob).ToArrayAsync())
        {
            Assert.Equal(row.ProductImportJob.UserAccountId, row.ShopProduct!.UserAccountId);
            Assert.Equal(row.ProductImportJob.ProductId, row.ShopProduct.ProductId);
        }
        var price = await f.Db.ShopProductPrices.AsNoTracking().SingleAsync(p => p.UserAccountId != null);
        Assert.Equal(f.A.Id, price.UserAccountId); Assert.Equal(4, price.Price); Assert.Equal(f.Past, price.CheckedDate);
        Assert.Equal(f.Observation.Id, price.ContributionObservationId); Assert.Equal(SourceType.StructuredData, price.SourceType);
        Assert.Equal(PriceScope.Unknown, price.PriceScope); Assert.Null(price.ShopLocationId);
        var observation = await f.Db.ContributionObservations.AsNoTracking().SingleAsync(o => o.Id == price.ContributionObservationId);
        Assert.Equal(f.Past.AddSeconds(7), observation.CollectedAt); Assert.NotEqual(price.CheckedDate, observation.CollectedAt);
        var history = await f.Db.ShopProductPriceHistory.AsNoTracking().SingleAsync(h => h.UserAccountId != null);
        Assert.Equal(f.A.Id, history.UserAccountId); Assert.Equal(f.Past.AddDays(-1), history.CheckedDate);
        Assert.Equal(f.Observation.Id, history.ContributionObservationId);
        Assert.Empty(await f.Db.ShopProductPrices.Where(p => p.UserAccountId == f.B.Id).ToArrayAsync());
        Assert.Equal(archived, await f.FingerprintAsync(legacyOnly: true));
    }

    [PostgreSqlFact]
    public async Task Replay_and_reintroduced_activity_never_duplicate_copies_or_overwrite_later_edits()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var original = await f.Service.PreviewAsync(); await f.Service.RehearseAsync(original.PlanHash);
        var receipt = await f.Db.LegacyProductMigrationMaps.SingleAsync(m => m.UserAccountId == f.A.Id && m.LegacyProductId == f.Source.Id);
        await f.Db.Products.Where(p => p.Id == receipt.PrivateProductId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Name, "Later private name"));
        await f.Db.ShopProductPrices.Where(p => p.UserAccountId == f.A.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Price, 3m).SetProperty(p => p.CheckedDate, f.Clock.Now.UtcDateTime));
        var before = await f.FingerprintAsync();
        Assert.True((await f.Service.RehearseAsync(original.PlanHash)).NoChanges);
        Assert.Equal(before, await f.FingerprintAsync());
        f.Db.UserProductActivities.Add(new() { UserAccountId = f.A.Id, ProductId = f.Source.Id, Quantity = 1, AddedDate = f.Past });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var relink = await f.Service.PreviewAsync(); Assert.Equal(0, relink.ProductCopies); Assert.Equal(0, relink.CurrentPriceCopies);
        Assert.Empty(relink.Conflicts);
        var result = await f.Service.RehearseAsync(relink.PlanHash);
        Assert.Equal(1, result.ReferencesChanged);
        Assert.Equal(3, await f.Db.LegacyProductMigrationMaps.CountAsync()); Assert.Equal(5, await f.Db.LegacyShopProductMigrationMaps.CountAsync());
        Assert.Equal(1, await f.Db.ShopProductPriceHistory.CountAsync(p => p.UserAccountId != null));
        Assert.Equal("Later private name", (await f.Db.Products.AsNoTracking().SingleAsync(p => p.Id == receipt.PrivateProductId)).Name);
        Assert.Equal(3, (await f.Db.ShopProductPrices.AsNoTracking().SingleAsync(p => p.UserAccountId == f.A.Id)).Price);
    }

    [PostgreSqlFact]
    public async Task Chosen_alternatives_without_private_price_evidence_remain_reviewable_and_can_be_undone()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        await f.Service.RehearseAsync((await f.Service.PreviewAsync()).PlanHash);
        var items = await f.Db.ShoppingListProducts.AsNoTracking().Include(i => i.Product).Where(i => i.ShoppingListId == f.ListA.Id).ToArrayAsync();
        var service = new ShoppingListSubstitutionService(f.Db, f.Clock);
        var choices = await service.ReadAsync(f.A.Id, f.ListA.Id, items, [], default);
        var choice = Assert.Single(choices[f.ItemA.Id]);
        Assert.Equal("Choice needs review", choice.Status); Assert.Null(choice.Offer!.Price); Assert.Equal(2, choice.Quantity);
        var ownAlternative = await f.Db.LegacyProductMigrationMaps.AsNoTracking().SingleAsync(m => m.UserAccountId == f.A.Id && m.LegacyProductId == f.Alternative.Id);
        Assert.Equal(ownAlternative.PrivateProductId, choice.Product.Id);
        var original = Assert.Single(items);
        await service.ChangeAsync(f.A.Id, f.ListA.Id, f.ItemA.Id, 2, new ShoppingListSubstitutionRequest(f.A.Id, null, 2, original.UpdatedDate), default);
        Assert.Empty(await f.Db.ShoppingListItemSubstitutions.ToArrayAsync());
        var after = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == f.ItemA.Id);
        Assert.Equal(original.ProductId, after.ProductId); Assert.Equal(original.Quantity, after.Quantity); Assert.Equal(original.Notes, after.Notes);
    }

    [PostgreSqlFact]
    public async Task Stale_preview_and_existing_private_collisions_fail_without_writes()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var plan = await f.Service.PreviewAsync();
        await f.Db.ShoppingListProducts.Where(i => i.Id == f.ItemA.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Notes, "Changed elsewhere"));
        var before = await f.FingerprintAsync();
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RehearseAsync(plan.PlanHash));
        Assert.Contains("stale", stale.Message); Assert.Equal(before, await f.FingerprintAsync());
        var own = new Product { UserAccountId = f.A.Id, Name = "New capture", GTIN = f.Source.GTIN, CreatedDate = f.Past, UpdatedDate = f.Past };
        f.Db.ShopProducts.Add(new() { Product = own, UserAccountId = f.A.Id, ShopId = 1, NameAtShop = own.Name,
            ProductUrl = f.SourceMap.ProductUrl, ShopProductCode = f.SourceMap.ShopProductCode, FirstFoundDate = f.Past, LastFoundDate = f.Past });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var collision = await f.Service.PreviewAsync();
        Assert.Contains(collision.Conflicts, c => c.StartsWith("existing private retailer"));
        Assert.Contains(collision.Conflicts, c => c.StartsWith("existing private product"));
        before = await f.FingerprintAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RehearseAsync(collision.PlanHash));
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Missing_barcodes_do_not_falsely_merge_or_block_unrelated_private_products()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        await f.Db.Products.Where(p => p.Id == f.Source.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.GTIN, ""));
        var unrelated = new Product { UserAccountId = f.A.Id, Name = "Unrelated private product", GTIN = "", CreatedDate = f.Past, UpdatedDate = f.Past };
        f.Db.Products.Add(unrelated); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var plan = await f.Service.PreviewAsync(); Assert.Empty(plan.Conflicts);
        await f.Service.RehearseAsync(plan.PlanHash);
        Assert.Equal("Unrelated private product", (await f.Db.Products.AsNoTracking().SingleAsync(p => p.Id == unrelated.Id)).Name);
        Assert.Equal(3, await f.Db.LegacyProductMigrationMaps.CountAsync());
    }

    [PostgreSqlFact]
    public async Task Failure_after_copying_rolls_back_copies_receipts_and_reference_changes()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var plan = await f.Service.PreviewAsync(); var before = await f.FingerprintAsync();
        var failure = new FailReferenceUpdate(); await using var db = f.Context(failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LegacyProductMigrationService(db, f.Clock).RehearseAsync(plan.PlanHash));
        Assert.True(failure.Reached); Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Rehearsals_reject_other_schemas_and_search_path_fallback_without_changing_data()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(prefix: "codex_not_migration_"); await f.SeedAsync();
        var plan = await f.Service.PreviewAsync(); var before = await f.FingerprintAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RehearseAsync(plan.PlanHash));
        await using var withFallback = new MyShoppingListDbContext(new DbContextOptionsBuilder<MyShoppingListDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(f.ConnectionString) { SearchPath = f.Schema + ",public" }.ConnectionString).Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LegacyProductMigrationService(withFallback, f.Clock).RehearseAsync(plan.PlanHash));
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Unknown_product_reference_tables_block_conversion_instead_of_losing_unreviewed_links()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TABLE \"UnreviewedProductUse\" (\"ProductId\" bigint REFERENCES \"Products\"(\"Id\"))");
        var plan = await f.Service.PreviewAsync();
        Assert.Contains(plan.Conflicts, c => c.StartsWith("unreviewed product-reference tables"));
        var before = await f.FingerprintAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RehearseAsync(plan.PlanHash));
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Preview_command_does_not_start_host_or_workers_and_rejects_apply_arguments()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(latest: false); await f.SeedAsync();
        var before = await f.FingerprintAsync();
        var report = await CommandAsync(f.ConnectionString);
        Assert.Equal(0, report.ExitCode); Assert.Empty(report.Error);
        using var json = JsonDocument.Parse(report.Output);
        Assert.True(json.RootElement.GetProperty("readOnly").GetBoolean());
        Assert.False(json.RootElement.GetProperty("schemaReady").GetBoolean());
        Assert.Equal(3, json.RootElement.GetProperty("productCopies").GetInt32());
        var denied = await CommandAsync(f.ConnectionString, "--apply");
        Assert.Equal(1, denied.ExitCode); Assert.Empty(denied.Output);
        var mixed = await CommandAsync(f.ConnectionString, "--apply-personal-product-migration");
        Assert.Equal(1, mixed.ExitCode); Assert.Empty(mixed.Output);
        Assert.DoesNotContain("Now listening", report.Output); Assert.Equal(before, await f.FingerprintAsync());
    }

    private static async Task<(int ExitCode, string Output, string Error)> CommandAsync(string connection, params string[] extra)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")) };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("--plan-personal-product-migration");
        foreach (var arg in extra) start.ArgumentList.Add(arg);
        start.Environment["ConnectionStrings__MyShoppingList"] = connection;
        start.Environment["ProductImport__Enabled"] = "true"; start.Environment["PriceRefresh__Enabled"] = "true";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("Preview must exit without starting a host."); }
        return (process.ExitCode, await output, await error);
    }

    private sealed class FailReferenceUpdate : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"ShoppingListProducts\"", StringComparison.Ordinal))
            { Reached = true; throw new InvalidOperationException("Injected failure after copying, before reference updates."); }
            return new(result);
        }
    }
}

internal sealed class MigrationRehearsalScope : IAsyncDisposable
{
    internal const string PreviousMigration = "20261025101322_AddPersonalCaptureRequests";
    internal const string CopyMigration = "20261026073219_AddLegacyProductMigrationMaps";
    private readonly string original;
    public string Schema { get; }
    public string ConnectionString { get; }
    public MyShoppingListDbContext Db { get; }
    public PersistenceClock Clock { get; } = new() { Now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero) };
    public DateTime Past => Clock.Now.UtcDateTime.AddDays(-3);
    public LegacyProductMigrationService Service => new(Db, Clock);
    public UserAccount A { get; private set; } = null!;
    public UserAccount B { get; private set; } = null!;
    public ShoppingList ListA { get; private set; } = null!;
    public ShoppingList ListB { get; private set; } = null!;
    public Product Source { get; private set; } = null!;
    public Product Alternative { get; private set; } = null!;
    public ShopProduct SourceMap { get; private set; } = null!;
    public ShoppingListProduct ItemA { get; private set; } = null!;
    public ShoppingListProduct ItemB { get; private set; } = null!;
    public ProductImportJob JobA { get; private set; } = null!;
    public ProductImportJob JobB { get; private set; } = null!;
    public ContributionObservation Observation { get; private set; } = null!;
    private MigrationRehearsalScope(string original, string prefix)
    {
        this.original = original; Schema = prefix + Guid.NewGuid().ToString("N");
        ConnectionString = new NpgsqlConnectionStringBuilder(original) { SearchPath = Schema, Pooling = false }.ConnectionString;
        Db = Context();
    }
    public MyShoppingListDbContext Context(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<MyShoppingListDbContext>()
        .UseNpgsql(ConnectionString).AddInterceptors(interceptors).Options);
    public static async Task<MigrationRehearsalScope> CreateAsync(bool latest = true, string prefix = "codex_migration_")
    {
        var f = new MigrationRehearsalScope(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!, prefix);
        await f.SchemaCommand("CREATE SCHEMA \"" + f.Schema + "\"");
        try { await f.Db.GetService<IMigrator>().MigrateAsync(latest ? null : PreviousMigration); return f; }
        catch { await f.DisposeAsync(); throw; }
    }
    public async Task SeedAsync()
    {
        A = new() { DisplayName = "Account A", Email = "a@example.test", PasswordHash = "fixture", PasswordSalt = "fixture", CreatedDate = Past, UpdatedDate = Past };
        B = new() { DisplayName = "Expired B", IsTrial = true, IsActive = false, ExpiresDate = Past, CreatedDate = Past, UpdatedDate = Past };
        ListA = new() { UserAccount = A, Name = "Weekly", CreatedDate = Past, UpdatedDate = Past };
        ListB = new() { UserAccount = B, Name = "Archived", IsArchived = true, ExpiresDate = Past, CreatedDate = Past, UpdatedDate = Past };
        Source = new() { Name = "Acme Original Coffee 500g", Brand = "Acme", GTIN = "9300000000002", PackQuantity = 1, PackSize = 500,
            PackUnit = "g", Description = "Original description", CreatedDate = Past.AddDays(-2), UpdatedDate = Past };
        Alternative = new() { Name = "Acme Original Coffee 1kg", Brand = "Acme", GTIN = "9300000000019", PackQuantity = 1, PackSize = 1000,
            PackUnit = "g", CreatedDate = Past.AddDays(-2), UpdatedDate = Past };
        SourceMap = Mapping(Source, 1, "100001"); var sharedMap = Mapping(Source, 2, "100002"); var alternativeMap = Mapping(Alternative, 2, "100003");
        ItemA = new() { ShoppingList = ListA, Product = Source, Quantity = 3, Notes = "private-note-a", IsPurchased = true, IsHidden = true,
            PurchasedDate = Past, AddedDate = Past.AddDays(-1), UpdatedDate = Past };
        ItemB = new() { ShoppingList = ListB, Product = Source, Quantity = 7, Notes = "private-note-b", AddedDate = Past, UpdatedDate = Past };
        Db.AddRange(SourceMap, sharedMap, alternativeMap, ItemA, ItemB); await Db.SaveChangesAsync();
        JobA = Job(A, ListA, Source, ItemA); JobB = Job(B, ListB, Source, ItemB);
        var altJob = Job(A, ListA, Alternative, null); Db.AddRange(JobA, JobB, altJob); await Db.SaveChangesAsync();
        Observation = new() { UserAccountId = A.Id, ImportJobId = JobA.Id, Source = "PersonalAdd", Outcome = "Accepted",
            Url = SourceMap.ProductUrl, CollectedAt = Past.AddSeconds(7), ReceivedAt = Past.AddSeconds(8) };
        var sharedObservation = new ContributionObservation { UserAccountId = B.Id, ImportJobId = JobB.Id, Source = "SharedCustomer", Outcome = "Accepted",
            Url = sharedMap.ProductUrl, CollectedAt = Past, ReceivedAt = Past };
        var wrongObservation = new ContributionObservation { UserAccountId = B.Id, ImportJobId = JobA.Id, Source = "PersonalAdd", Outcome = "Accepted",
            Url = SourceMap.ProductUrl, CollectedAt = Past, ReceivedAt = Past };
        Db.AddRange(Observation, sharedObservation, wrongObservation); await Db.SaveChangesAsync();
        Db.ShopProductPrices.AddRange(
            new() { ShopProductId = SourceMap.Id, ContributionObservationId = Observation.Id, Price = 4, NormalPrice = 5,
                SourceType = SourceType.StructuredData, SourceUrl = SourceMap.ProductUrl, CheckedDate = Past, CreatedDate = Past, UpdatedDate = Past },
            new() { ShopProductId = sharedMap.Id, ContributionObservationId = sharedObservation.Id, Price = 25, SourceUrl = sharedMap.ProductUrl,
                CheckedDate = Past, CreatedDate = Past, UpdatedDate = Past });
        Db.ShopProductPriceHistory.AddRange(
            new() { ShopProductId = SourceMap.Id, ContributionObservationId = Observation.Id, Price = 4, SourceUrl = SourceMap.ProductUrl, CheckedDate = Past.AddDays(-1), CreatedDate = Past },
            new() { ShopProductId = SourceMap.Id, ContributionObservationId = wrongObservation.Id, Price = 5, SourceUrl = SourceMap.ProductUrl, CheckedDate = Past.AddDays(-2), CreatedDate = Past },
            new() { ShopProductId = sharedMap.Id, Price = 30, SourceUrl = sharedMap.ProductUrl, CheckedDate = Past.AddDays(-5), CreatedDate = Past });
        Db.UserProductPreferences.AddRange(new() { UserAccountId = A.Id, ProductId = Source.Id, IsFavorite = true, IsEssential = true, UpdatedDate = Past },
            new() { UserAccountId = B.Id, ProductId = Source.Id, IsFavorite = true, UpdatedDate = Past });
        Db.UserProductActivities.Add(new() { UserAccountId = A.Id, ProductId = Source.Id, ShoppingListProductId = ItemA.Id,
            Quantity = 3, AddedDate = Past, PurchasedDate = Past });
        Db.ProductImportRetailerResults.AddRange(new() { ProductImportJobId = JobA.Id, ShopId = 1, ShopProductId = SourceMap.Id, Status = RetailerLookupStatus.Exact, CreatedDate = Past, UpdatedDate = Past },
            new() { ProductImportJobId = JobB.Id, ShopId = 1, ShopProductId = SourceMap.Id, Status = RetailerLookupStatus.Exact, CreatedDate = Past, UpdatedDate = Past });
        Db.PackDiscoveryJobs.Add(new() { UserAccountId = A.Id, ShoppingListId = ListA.Id, ShoppingListProductId = ItemA.Id,
            ProductId = Source.Id, ShopId = 1, Status = "Completed", CreatedDate = Past, UpdatedDate = Past });
        var brand = new BrandDiscoveryJob { UserAccountId = A.Id, ShoppingListId = ListA.Id, ShoppingListProductId = ItemA.Id,
            ProductId = Source.Id, ShopId = 1, Status = "Completed", CreatedDate = Past, UpdatedDate = Past };
        Db.ProductBrandSuggestions.Add(new() { BrandDiscoveryJob = brand, AlternativeProductId = Alternative.Id, ShopProductId = alternativeMap.Id, DiscoveredDate = Past });
        Db.ShoppingListItemSubstitutions.Add(new() { UserAccountId = A.Id, ShoppingListId = ListA.Id, ShoppingListProductId = ItemA.Id,
            ProductId = Source.Id, ShopId = 2, ShopProductId = alternativeMap.Id, Quantity = 2, OriginalFingerprint = "original-fingerprint",
            AlternativeFingerprint = "alternative-fingerprint", UpdatedDate = Past });
        Db.UserExtensionImportTasks.Add(new() { ProductImportJobId = JobA.Id, RequestId = Guid.NewGuid(), Status = "Completed", Url = SourceMap.ProductUrl, UpdatedDate = Past });
        Db.ColesExtensionTasks.Add(new() { Key = "archive-task", Kind = "product", Url = SourceMap.ProductUrl, Status = "Completed", CreatedAt = Past });
        await Db.SaveChangesAsync(); Db.ChangeTracker.Clear();
    }
    private ShopProduct Mapping(Product product, long shop, string code) => new() { Product = product, ShopId = shop,
        ShopProductCode = code, NameAtShop = product.Name, ProductUrl = shop == 1 ? "https://www.coles.com.au/product/" + code : "https://www.woolworths.com.au/shop/productdetails/" + code,
        MatchType = MatchType.Exact, MatchConfidence = 100, FirstFoundDate = Past.AddDays(-2), LastFoundDate = Past, LastCheckedDate = Past };
    private ProductImportJob Job(UserAccount account, ShoppingList list, Product product, ShoppingListProduct? item) => new() {
        UserAccountId = account.Id, ShoppingListId = list.Id, ProductId = product.Id, ShoppingListProductId = item?.Id,
        SourceUrl = SourceMap.ProductUrl, NormalisedSourceUrl = SourceMap.ProductUrl, RequestedQuantity = item?.Quantity ?? 1,
        Status = ProductImportJobStatus.Completed, ProgressStage = ProductImportProgressStage.Completed, CreatedDate = Past, CompletedDate = Past };
    public async Task<string> FingerprintAsync(bool legacyOnly = false)
    {
        var names = await Db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = current_schema() ORDER BY tablename").ToArrayAsync();
        var rows = new StringBuilder();
        foreach (var name in names)
        {
            var catalogue = new[] { "Products", "ShopProducts", "ShopProductPrices", "ShopProductPriceHistory" }.Contains(name);
            if (legacyOnly && !catalogue && !new[] { "ContributionObservations", "UserExtensionImportTasks", "ColesExtensionTasks", "ProductBrandSuggestions", "ProductAlternativeRelationships" }.Contains(name)) continue;
            var sql = "SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text AS \"Value\" FROM \"" + name.Replace("\"", "\"\"") + "\" t"
                + (legacyOnly && catalogue ? " WHERE \"UserAccountId\" IS NULL" : "");
            rows.Append(name).Append(await Db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rows.ToString())));
    }
    private async Task SchemaCommand(string sql)
    {
        await using var connection = new NpgsqlConnection(original); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }
    public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await SchemaCommand("DROP SCHEMA \"" + Schema + "\" CASCADE"); }
}
