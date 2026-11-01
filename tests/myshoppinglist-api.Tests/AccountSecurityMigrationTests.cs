using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class AccountSecurityMigrationTests
{
    private const string Previous = "20261031061702_AddTestBillingLedger";
    private const string Current = "20261101001539_AddAccountRecoveryAndEmailOutbox";

    [PostgreSqlFact]
    public async Task Additive_upgrade_and_empty_rollback_preserve_accounts_lists_and_prices()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(latest: false);
        await f.Db.GetService<IMigrator>().MigrateAsync(Previous); await f.SeedAsync();
        var before = await f.FingerprintAsync(legacyOnly: true);
        var customers = await CustomerSnapshot(f);
        await f.Db.GetService<IMigrator>().MigrateAsync(Current);
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        Assert.Equal(customers, await CustomerSnapshot(f));
        Assert.False(f.Db.Database.HasPendingModelChanges());
        await f.Db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        Assert.Equal(customers, await CustomerSnapshot(f));
    }

    private static Task<string> CustomerSnapshot(MigrationRehearsalScope f) => f.Db.Database.SqlQueryRaw<string>("""
        SELECT jsonb_build_object(
            'accounts',(SELECT jsonb_agg(to_jsonb(a) ORDER BY a."Id") FROM "UserAccounts" a),
            'lists',(SELECT jsonb_agg(to_jsonb(l) ORDER BY l."Id") FROM "ShoppingLists" l),
            'items',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i."Id") FROM "ShoppingListProducts" i)
        )::text AS "Value"
        """).SingleAsync();

    [PostgreSqlFact]
    public async Task Rollback_refuses_to_discard_delivery_or_credential_history()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        f.Db.AccountCredentialChanges.Add(new() { UserAccountId = f.A.Id, ChangedAt = f.Past }); await f.Db.SaveChangesAsync();
        var before = await f.FingerprintAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.Contains("Cannot discard email or credential history", error.MessageText);
        Assert.Equal(before, await f.FingerprintAsync());
    }
}
