using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class McpMutationMigrationTests
{
    private const string AuthorizationMigration = "20261031003537_AddAccountCurrentListAndAiAuthorization";
    private const string ReceiptMigration = "20261031013313_AddMcpMutationReceipts";
    private const string RevisionMigration = "20261031014650_PreserveAccountCurrentListRevisions";
    private const string BridgeMigration = "20261031035902_AddPersonalExtensionDevicesAndTasks";
    private const string OperationsMigration = "20261031043409_AddOperationsAndPrivateFeedback";

    [PostgreSqlFact]
    public async Task Operations_migration_preserves_existing_data_and_refuses_history_loss()
    {
        await using var f = await SchemaAsync(BridgeMigration); await f.SeedAsync();
        var before = await f.FingerprintAsync(legacyOnly: true);
        await f.Db.GetService<IMigrator>().MigrateAsync(OperationsMigration);
        Assert.Empty(await f.Db.FeedbackTickets.ToArrayAsync()); Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        await f.Db.GetService<IMigrator>().MigrateAsync(BridgeMigration);
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        await f.Db.GetService<IMigrator>().MigrateAsync(OperationsMigration);
        f.Db.OperationalEvents.Add(new() { CreatedAt = f.Past, Component = "api", Name = "api_request", Outcome = "success", Release = "test", Fingerprint = "api:api_request:success" });
        await f.Db.SaveChangesAsync(); var populated = await f.FingerprintAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync(BridgeMigration));
        Assert.Contains("Cannot remove operational support, audit or incident history", error.MessageText);
        Assert.Equal(populated, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Extension_upgrade_empty_rollback_and_nonempty_guard_preserve_existing_data()
    {
        await using var f = await SchemaAsync(RevisionMigration); await f.SeedAsync();
        var before = await f.FingerprintAsync(legacyOnly: true);
        await f.Db.GetService<IMigrator>().MigrateAsync(BridgeMigration);
        Assert.Empty(await f.Db.ExtensionDevices.ToArrayAsync()); Assert.Empty(await f.Db.ExtensionTasks.ToArrayAsync());
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        await f.Db.GetService<IMigrator>().MigrateAsync(RevisionMigration);
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        await f.Db.GetService<IMigrator>().MigrateAsync(BridgeMigration);
        var session = new myshoppinglist_api.Models.UserSession { UserAccountId = f.A.Id, TokenHash = new string('C', 64), CreatedDate = f.Past, ExpiresDate = f.Past.AddDays(7) };
        // This historical schema predates nullable social reauthentication columns.
        session.Id = (await f.Db.Database.SqlQuery<long>($"""
            INSERT INTO "UserSessions" ("UserAccountId","TokenHash","CreatedDate","ExpiresDate")
            VALUES ({session.UserAccountId},{session.TokenHash},{session.CreatedDate},{session.ExpiresDate}) RETURNING "Id" AS "Value"
            """).ToArrayAsync()).Single();
        f.Db.ExtensionDevices.Add(new() { UserAccountId = f.A.Id, UserSessionId = session.Id, InstallationId = Guid.NewGuid(), TokenHash = new string('A', 64),
            ExtensionId = new string('a', 32), ExtensionVersion = "0.9.0", LinkedAt = f.Past });
        await f.Db.SaveChangesAsync();
        var linked = await f.FingerprintAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync(RevisionMigration));
        Assert.Contains("Cannot remove personal extension links or task history", error.MessageText);
        Assert.Equal(linked, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Selection_revisions_are_backfilled_and_rollback_cannot_erase_them()
    {
        await using var f = await SchemaAsync(ReceiptMigration);
        await f.SeedAsync();
        f.Db.UserCurrentLists.Add(new() { UserAccountId = f.A.Id, ShoppingListId = f.ListA.Id, Version = 7, UpdatedDate = f.Past });
        await f.Db.SaveChangesAsync();
        await f.Db.GetService<IMigrator>().MigrateAsync(RevisionMigration);
        Assert.Equal(7, (await f.Db.UserCurrentListRevisions.SingleAsync()).Version);
        var before = await f.FingerprintAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync(ReceiptMigration));
        Assert.Contains("Cannot remove current-list revision history", error.MessageText);
        Assert.Equal(before, await f.FingerprintAsync());
    }

    [PostgreSqlFact]
    public async Task Additive_upgrade_and_empty_rollback_preserve_existing_products()
    {
        await using var f = await SchemaAsync(AuthorizationMigration);
        await f.SeedAsync();
        var before = await f.FingerprintAsync(legacyOnly: true);
        await f.Db.GetService<IMigrator>().MigrateAsync(ReceiptMigration);
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        Assert.Empty(await f.Db.McpMutationReceipts.ToArrayAsync());
        await f.Db.GetService<IMigrator>().MigrateAsync(AuthorizationMigration);
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        Assert.DoesNotContain(ReceiptMigration, await f.Db.Database.GetAppliedMigrationsAsync());
    }

    [PostgreSqlFact]
    public async Task Nonempty_receipts_block_rollback_and_preserve_retry_history()
    {
        await using var f = await SchemaAsync(ReceiptMigration);
        await f.SeedAsync();
        f.Db.McpMutationReceipts.Add(new() { UserAccountId = f.A.Id, RequestId = Guid.NewGuid(), Operation = "create_list",
            RequestHash = new string('A', 64), ResultJson = "{\"version\":1}", CreatedDate = f.Past });
        await f.Db.SaveChangesAsync();
        var before = await f.FingerprintAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync(AuthorizationMigration));
        Assert.Contains("Cannot remove MCP mutation receipts", error.MessageText);
        Assert.Equal(before, await f.FingerprintAsync());
    }

    private static async Task<MigrationRehearsalScope> SchemaAsync(string migration)
    {
        var scope = await MigrationRehearsalScope.CreateAsync(latest: false);
        try { await scope.Db.GetService<IMigrator>().MigrateAsync(migration); return scope; }
        catch { await scope.DisposeAsync(); throw; }
    }
}
