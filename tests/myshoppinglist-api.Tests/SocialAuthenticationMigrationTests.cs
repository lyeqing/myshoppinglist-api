using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using myshoppinglist_api.Models;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class SocialAuthenticationMigrationTests
{
    [PostgreSqlFact]
    public async Task Upgrade_preserves_existing_accounts_and_database_prevents_orphaned_and_moved_identities()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(latest: false);
        await f.Db.GetService<IMigrator>().MigrateAsync("20261114103211_AddPrivateProductResearch"); await f.SeedAsync();
        var oldId = f.A.Id; var oldName = f.A.DisplayName; var oldPassword = f.A.PasswordHash;
        await f.Db.Database.MigrateAsync();
        var preserved = await f.Db.UserAccounts.AsNoTracking().SingleAsync(x => x.Id == oldId);
        Assert.Equal(oldName, preserved.DisplayName); Assert.Equal(oldPassword, preserved.PasswordHash); Assert.Null(preserved.EmailVerifiedAt);
        Assert.False(f.Db.Database.HasPendingModelChanges());
        var source = new UserAccount { DisplayName = "Social only", CreatedDate = DateTime.UtcNow, UpdatedDate = DateTime.UtcNow };
        await using (var tx = await f.Db.Database.BeginTransactionAsync())
        {
            f.Db.UserAccounts.Add(source);
            f.Db.ExternalIdentities.Add(new() { UserAccount = source, Provider = "google", Subject = "immutable-subject", CreatedAt = DateTime.UtcNow });
            await f.Db.SaveChangesAsync(); await tx.CommitAsync();
        }
        f.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<PostgresException>(() => f.Db.ExternalIdentities.Where(x => x.UserAccountId == source.Id).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<PostgresException>(() => f.Db.ExternalIdentities.Where(x => x.UserAccountId == source.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserAccountId, oldId)));
        await Assert.ThrowsAsync<PostgresException>(() => f.Db.Database.ExecuteSqlRawAsync("""INSERT INTO "UserAccounts" ("DisplayName","IsTrial","IsActive","IsPaid","CreatedDate","UpdatedDate","CompareOnlyPreferredShops","ContributionBlocked") VALUES ('Unusable',false,true,false,now(),now(),false,false)"""));
        var failure = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync("20261114103211_AddPrivateProductResearch"));
        Assert.Contains("Social sign-in data exists", failure.MessageText);
        Assert.Equal(source.Id, (await f.Db.ExternalIdentities.SingleAsync()).UserAccountId);
        var foreignSession = new UserSession { UserAccountId = f.B.Id, TokenHash = "fixture-hash", CreatedDate = DateTime.UtcNow, ExpiresDate = DateTime.UtcNow.AddMinutes(30) };
        f.Db.Add(foreignSession); await f.Db.SaveChangesAsync();
        f.Db.ExternalAuthFlows.Add(new() { UserAccountId = f.A.Id, UserSessionId = foreignSession.Id, Provider = "google", Purpose = "link", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync()); f.Db.ChangeTracker.Clear();

        f.Db.ExternalIdentities.Add(new() { UserAccountId = source.Id, Provider = "microsoft", Subject = "second-subject", CreatedAt = DateTime.UtcNow });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        await using var one = f.Context(); await using var two = f.Context();
        await using var txOne = await one.Database.BeginTransactionAsync(); await using var txTwo = await two.Database.BeginTransactionAsync();
        await one.ExternalIdentities.Where(x => x.UserAccountId == source.Id && x.Provider == "google").ExecuteDeleteAsync();
        await two.ExternalIdentities.Where(x => x.UserAccountId == source.Id && x.Provider == "microsoft").ExecuteDeleteAsync();
        async Task<bool> Commit(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx)
        { try { await tx.CommitAsync(); return true; } catch (PostgresException) { return false; } }
        var committed = await Task.WhenAll(Commit(txOne), Commit(txTwo)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(committed, success => success);
        Assert.Single(await f.Db.ExternalIdentities.Where(x => x.UserAccountId == source.Id).ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Empty_social_feature_can_roll_back_without_changing_existing_customer_data()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(latest: false);
        await f.Db.GetService<IMigrator>().MigrateAsync("20261114103211_AddPrivateProductResearch"); await f.SeedAsync();
        var before = await f.FingerprintAsync(legacyOnly: true);
        await f.Db.Database.MigrateAsync();
        await f.Db.GetService<IMigrator>().MigrateAsync("20261114103211_AddPrivateProductResearch");
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
    }
}
