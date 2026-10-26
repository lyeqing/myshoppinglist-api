using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;
using myshoppinglist_api.Services;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class LegacyProductMigrationApplyTests
{
    [PostgreSqlFact]
    public async Task Explicit_apply_converts_non_rehearsal_schema_and_requires_fresh_preview_for_reruns()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(prefix: "codex_apply_"); await f.SeedAsync();
        var plan = await f.Service.PreviewAsync(); var archive = await f.FingerprintAsync(legacyOnly: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RehearseAsync(plan.PlanHash));
        var result = await f.Service.ApplyAsync(plan.Database, f.Schema, plan.PlanHash);
        Assert.False(result.NoChanges); Assert.Equal(3, result.ProductCopies); Assert.Equal(5, result.RetailerMappingCopies);
        Assert.Equal(1, result.CurrentPriceCopies); Assert.Equal(1, result.HistoryRowCopies);
        Assert.Equal(plan.ReferenceChanges.Values.Sum(), result.ReferencesChanged);
        Assert.Equal(archive, await f.FingerprintAsync(legacyOnly: true));
        var item = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == f.ItemB.Id);
        Assert.Equal(f.B.Id, (await f.Db.Products.FindAsync(item.ProductId))!.UserAccountId);
        Assert.Equal(7, item.Quantity); Assert.Equal("private-note-b", item.Notes);
        Assert.False((await f.Db.UserAccounts.FindAsync(f.B.Id))!.IsActive);
        Assert.True((await f.Db.ShoppingLists.FindAsync(f.ListB.Id))!.IsArchived);
        f.Db.ChangeTracker.Clear();
        await f.Db.Products.Where(p => p.UserAccountId == f.A.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Description, "Later private edit"));
        var beforeReplay = await f.FingerprintAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(plan.Database, f.Schema, plan.PlanHash));
        var fresh = await f.Service.PreviewAsync();
        Assert.True((await f.Service.ApplyAsync(fresh.Database, f.Schema, fresh.PlanHash)).NoChanges);
        Assert.Equal(beforeReplay, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Wrong_targets_hashes_and_fallback_paths_fail_without_writes()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var plan = await f.Service.PreviewAsync(); var before = await f.FingerprintAsync();
        foreach (var (database, schema, hash) in new[] {
            (plan.Database + "_wrong", f.Schema, plan.PlanHash), (plan.Database, f.Schema + "_wrong", plan.PlanHash),
            ("", f.Schema, plan.PlanHash), (plan.Database, "", plan.PlanHash),
            (plan.Database, f.Schema, new string('Z', 64)), (plan.Database, f.Schema, "short") })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(database, schema, hash));
        await using var fallback = new MyShoppingListDbContext(new DbContextOptionsBuilder<MyShoppingListDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(f.ConnectionString) { SearchPath = f.Schema + ",public" }.ConnectionString).Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LegacyProductMigrationService(fallback, f.Clock)
            .ApplyAsync(plan.Database, f.Schema, plan.PlanHash));
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Stale_data_and_conflicts_block_apply_before_copying()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var plan = await f.Service.PreviewAsync();
        await f.Db.ShoppingListProducts.Where(i => i.Id == f.ItemA.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Notes, "Changed after review"));
        var before = await f.FingerprintAsync();
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(plan.Database, f.Schema, plan.PlanHash));
        Assert.Contains("stale", stale.Message); Assert.Equal(before, await f.FingerprintAsync());
        f.Db.Products.Add(new() { UserAccountId = f.A.Id, Name = "Existing private duplicate", GTIN = f.Source.GTIN,
            CreatedDate = f.Past, UpdatedDate = f.Past });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var conflicted = await f.Service.PreviewAsync(); Assert.NotEmpty(conflicted.Conflicts);
        before = await f.FingerprintAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(plan.Database, f.Schema, conflicted.PlanHash));
        Assert.Equal(before, await f.FingerprintAsync()); Assert.Empty(await f.Db.LegacyProductMigrationMaps.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Missing_schema_deployment_is_not_applied_automatically()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(latest: false); await f.SeedAsync();
        var plan = await f.Service.PreviewAsync(); var before = await f.FingerprintAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(plan.Database, f.Schema, plan.PlanHash));
        Assert.False((await f.Service.PreviewAsync()).SchemaReady); Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Post_conversion_validation_detects_unplanned_field_changes_and_rolls_back_every_row()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        await f.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION "TamperMigrationNotes"() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN NEW."Notes" := 'Unexpected database-side change'; RETURN NEW; END; $body$;
            CREATE TRIGGER "TamperMigrationNotes" BEFORE UPDATE OF "ProductId" ON "ShoppingListProducts"
                FOR EACH ROW EXECUTE FUNCTION "TamperMigrationNotes"();
            """);
        var plan = await f.Service.PreviewAsync(); var before = await f.FingerprintAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(plan.Database, f.Schema, plan.PlanHash));
        Assert.Contains("integrity validation failed", error.Message);
        Assert.Null(f.Db.Database.CurrentTransaction); Assert.Empty(f.Db.ChangeTracker.Entries());
        Assert.Equal(before, await f.FingerprintAsync());
        Assert.Empty(await f.Db.LegacyProductMigrationMaps.ToArrayAsync());
        Assert.Empty(await f.Db.LegacyShopProductMigrationMaps.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Busy_writer_times_out_and_cancellation_releases_conversion_transaction()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var plan = await f.Service.PreviewAsync(); var before = await f.FingerprintAsync();
        await using var writer = f.Context();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlRawAsync("LOCK TABLE \"Products\" IN ROW EXCLUSIVE MODE");
        var busy = await Assert.ThrowsAsync<PostgresException>(() => f.Service.ApplyAsync(plan.Database, f.Schema, plan.PlanHash));
        Assert.Equal("55P03", busy.SqlState); Assert.Null(f.Db.Database.CurrentTransaction);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ApplyAsync(plan.Database, f.Schema, plan.PlanHash, cancellation.Token));
        Assert.Null(f.Db.Database.CurrentTransaction);
        await transaction.RollbackAsync(); Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Apply_command_exits_without_starting_workers_and_uses_only_the_named_schema()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(prefix: "codex_apply_"); await f.SeedAsync();
        var plan = await f.Service.PreviewAsync(); var before = await f.FingerprintAsync();
        var args = Arguments(plan.Database, f.Schema, plan.PlanHash);
        var wrong = await CommandAsync(f.ConnectionString, Arguments(plan.Database + "_wrong", f.Schema, plan.PlanHash));
        Assert.Equal(1, wrong.ExitCode); Assert.Empty(wrong.Output);
        Assert.DoesNotContain("Password", wrong.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await f.FingerprintAsync());
        // Deliberate fallback in configuration: the command must replace it with
        // the explicit target, never search or migrate a fallback schema.
        var configured = new NpgsqlConnectionStringBuilder(f.ConnectionString) { SearchPath = "public," + f.Schema }.ConnectionString;
        var applied = await CommandAsync(configured, args);
        Assert.Equal(0, applied.ExitCode); Assert.Empty(applied.Error);
        using var json = JsonDocument.Parse(applied.Output);
        Assert.Equal(3, json.RootElement.GetProperty("productCopies").GetInt32());
        Assert.False(json.RootElement.GetProperty("noChanges").GetBoolean());
        Assert.DoesNotContain("Now listening", applied.Output);
        var fresh = await f.Service.PreviewAsync(); Assert.Equal(0, fresh.AccountProductPairs);
        var replay = await CommandAsync(configured, Arguments(plan.Database, f.Schema, fresh.PlanHash));
        Assert.Equal(0, replay.ExitCode);
        using var replayJson = JsonDocument.Parse(replay.Output);
        Assert.True(replayJson.RootElement.GetProperty("noChanges").GetBoolean());
    }

    [Fact]
    public async Task Apply_command_rejects_missing_duplicate_unknown_and_mixed_arguments_before_database_access()
    {
        var valid = Arguments("fixture", "isolated", new string('A', 64));
        foreach (var args in new[] { valid.Where(a => a != "--backup-verified").ToArray(),
            valid.Where(a => a != "--writers-stopped").ToArray(), valid.Concat(["--database", "other"]).ToArray(),
            valid.Concat(["--writers-stopped"]).ToArray(), valid.Concat(["--unexpected"]).ToArray(),
            valid.Concat(["--plan-personal-product-migration"]).ToArray(), valid.Concat(["--audit-personal-products"]).ToArray(), valid[..^1],
            new[] { "--apply-personal-product-migration" } })
        {
            var denied = await CommandAsync("Not a connection string: secret-marker", args);
            Assert.Equal(1, denied.ExitCode); Assert.Empty(denied.Output);
            Assert.DoesNotContain("secret-marker", denied.Error);
        }
    }

    private static string[] Arguments(string database, string schema, string hash) =>
        ["--apply-personal-product-migration", "--database", database, "--schema", schema, "--plan-hash", hash, "--backup-verified", "--writers-stopped"];

    private static async Task<(int ExitCode, string Output, string Error)> CommandAsync(string connection, string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")) };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["ConnectionStrings__MyShoppingList"] = connection;
        start.Environment["ProductImport__Enabled"] = "true"; start.Environment["PriceRefresh__Enabled"] = "true";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("Apply must exit without starting a host."); }
        return (process.ExitCode, await output, await error);
    }
}
