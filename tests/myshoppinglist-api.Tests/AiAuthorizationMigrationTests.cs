using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using OpenIddict.EntityFrameworkCore.Models;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class AiAuthorizationMigrationTests
{
    [PostgreSqlFact]
    public async Task Empty_authorization_rollback_preserves_existing_application_data()
    {
        await using var f = await AuthorizationSchemaAsync();
        await f.SeedAsync();
        var before = await f.FingerprintAsync(legacyOnly: true);
        await f.Db.GetService<IMigrator>().MigrateAsync(MigrationRehearsalScope.CopyMigration);
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        Assert.DoesNotContain("20261031003537_AddAccountCurrentListAndAiAuthorization", await f.Db.Database.GetAppliedMigrationsAsync());
    }

    [PostgreSqlFact]
    public Task Authorization_data_blocks_rollback_before_any_table_or_receipt_is_removed() => RejectRollbackAsync(false);

    [PostgreSqlFact]
    public Task Current_list_selection_blocks_rollback_even_when_ai_is_disabled() => RejectRollbackAsync(true);

    private static async Task RejectRollbackAsync(bool selection)
    {
        await using var f = await AuthorizationSchemaAsync();
        await f.SeedAsync();
        if (selection) f.Db.UserCurrentLists.Add(new() { UserAccountId = f.A.Id, ShoppingListId = f.ListA.Id, UpdatedDate = f.Past });
        else f.Db.Set<OpenIddictEntityFrameworkCoreApplication>().Add(new() { Id = Guid.NewGuid().ToString(), ClientId = "rollback-guard", ClientType = "public" });
        await f.Db.SaveChangesAsync();
        var before = await f.FingerprintAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync(MigrationRehearsalScope.CopyMigration));
        Assert.Contains("Cannot remove account current-list or AI authorization data", error.MessageText);
        Assert.Equal(before, await f.FingerprintAsync());
    }

    private static async Task<MigrationRehearsalScope> AuthorizationSchemaAsync()
    {
        var scope = await MigrationRehearsalScope.CreateAsync(latest: false);
        try { await scope.Db.GetService<IMigrator>().MigrateAsync("20261031003537_AddAccountCurrentListAndAiAuthorization"); return scope; }
        catch { await scope.DisposeAsync(); throw; }
    }
}
