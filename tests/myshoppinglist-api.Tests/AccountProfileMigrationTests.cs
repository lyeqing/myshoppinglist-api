using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using myshoppinglist_api.Models;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class AccountProfileMigrationTests
{
    private const string Previous = "20261115012833_AddSocialSignIn";
    private static async Task<string> DataFingerprint(MigrationRehearsalScope f)
    {
        var tables = await f.Db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname=current_schema() AND tablename <> '__EFMigrationsHistory' ORDER BY tablename").ToArrayAsync();
        var result = new System.Text.StringBuilder();
        foreach (var table in tables)
        {
            // Names come from PostgreSQL's catalogue; quote every identifier.
            var sql = "SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]'::jsonb)::text AS \"Value\" FROM \"" + table.Replace("\"", "\"\"") + "\" t";
            result.Append(table).Append(await f.Db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(result.ToString())));
    }

    [PostgreSqlFact]
    public async Task Upgrade_and_empty_rollback_preserve_existing_accounts_shopping_and_receipts()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(latest: false);
        await f.Db.GetService<IMigrator>().MigrateAsync(Previous); await f.SeedAsync();
        var before = await DataFingerprint(f);
        await f.Db.Database.MigrateAsync(); Assert.False(f.Db.Database.HasPendingModelChanges());
        Assert.Equal(before, await DataFingerprint(f));
        await f.Db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.Equal(before, await DataFingerprint(f));
    }

    [PostgreSqlFact]
    public async Task Populated_rollback_refuses_to_discard_email_change_history()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var session = new UserSession { UserAccountId = f.A.Id, TokenHash = "fixture", CreatedDate = DateTime.UtcNow, ExpiresDate = DateTime.UtcNow.AddHours(1) };
        f.Db.Add(session); await f.Db.SaveChangesAsync();
        var flow = new ExternalAuthFlow { UserAccountId = f.A.Id, UserSessionId = session.Id, Provider = "local", Purpose = "email_change", Email = "new@example.test", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(15) };
        f.Db.Add(flow); await f.Db.SaveChangesAsync(); var before = await f.FingerprintAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.Contains("Email-change records exist", error.MessageText);
        Assert.Equal(before, await f.FingerprintAsync());
    }
}
