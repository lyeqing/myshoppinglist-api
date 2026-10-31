using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class ReadOnlyListBaselineTests
{
    [PostgreSqlFact]
    public async Task Management_reads_only_the_owners_active_lists_without_writing_any_table()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var first = await ListFixture.CreateAsync();
        await using var second = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(first, false);
        await ShoppingListServiceTests.RegisterFixtureAsync(second, false);
        var db = first.Scope.Db;
        var now = first.Scope.Clock.Now.UtcDateTime;
        db.ShoppingLists.AddRange(
            new ShoppingList { UserAccountId = first.Scope.UserId, Name = "Archived", IsArchived = true, CreatedDate = now, UpdatedDate = now },
            new ShoppingList { UserAccountId = first.Scope.UserId, Name = "Expired", ExpiresDate = now, CreatedDate = now, UpdatedDate = now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var before = await FingerprintAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            // PostgreSQL rejects writes, including raw SQL and writes outside EF tracking.
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
            Assert.Equal("on", Assert.Single(await db.Database.SqlQueryRaw<string>("SHOW transaction_read_only").ToArrayAsync()));
            var owned = await first.Service.ManageAsync(first.Scope.UserId, default);
            Assert.Equal(first.Scope.ListId, Assert.Single(owned.Lists).Id);
            var other = await first.Service.ManageAsync(second.Scope.UserId, default);
            Assert.Equal(second.Scope.ListId, Assert.Single(other.Lists).Id);
            Assert.Equal(owned.Lists, (await first.Service.ManageAsync(first.Scope.UserId, default)).Lists);
            Assert.Empty(db.ChangeTracker.Entries());
            await transaction.CommitAsync();
        }
        Assert.Equal(before, await FingerprintAsync(db));
    }

    [PostgreSqlFact]
    public async Task Reading_an_empty_account_does_not_create_a_default_list()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var fixture = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(fixture, false);
        await fixture.Scope.Db.ShoppingLists.Where(l => l.UserAccountId == fixture.Scope.UserId).ExecuteDeleteAsync();
        fixture.Scope.Db.ChangeTracker.Clear();
        var before = await FingerprintAsync(fixture.Scope.Db);
        await using (var transaction = await fixture.Scope.Db.Database.BeginTransactionAsync())
        {
            await fixture.Scope.Db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
            Assert.Empty((await fixture.Service.ManageAsync(fixture.Scope.UserId, default)).Lists);
            await transaction.CommitAsync();
        }
        Assert.Equal(before, await FingerprintAsync(fixture.Scope.Db));
    }

    // Same schema-wide fingerprint technique as LegacyProductMigrationAuditTests.
    private static async Task<string> FingerprintAsync(MyShoppingListDbContext db)
    {
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = current_schema() ORDER BY tablename").ToArrayAsync();
        var rows = new StringBuilder();
        foreach (var table in tables)
        {
            var sql = "SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text AS \"Value\" FROM \""
                + table.Replace("\"", "\"\"") + "\" AS t";
            rows.Append(table).Append(await db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rows.ToString())));
    }
}
