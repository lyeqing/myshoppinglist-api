using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using myshoppinglist_api.Models;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class PersonalCaptureRequestMigrationTests
{
    private const string PreviousMigration = "20261025030113_AddPersonalProductOwnership";

    [PostgreSqlFact]
    public async Task Migration_preserves_legacy_jobs_tasks_and_lists_and_keeps_legacy_owner_index()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        var job = Job(f, false);
        job.CaptureRequestId = null; job.CaptureRequestHash = null; job.CaptureResponseJson = null;
        var task = new UserExtensionImportTask { ProductImportJob = job, RequestId = Guid.NewGuid(), Url = job.SourceUrl, UpdatedDate = f.Clock.Now.UtcDateTime };
        f.Db.Add(task); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var migrator = f.Db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        await migrator.MigrateAsync();
        var stored = await f.Db.ProductImportJobs.SingleAsync();
        Assert.Equal(job.Id, stored.Id); Assert.Equal(job.SourceUrl, stored.SourceUrl);
        Assert.Equal(f.First.Id, stored.UserAccountId); Assert.Equal(f.FirstList.Id, stored.ShoppingListId);
        Assert.Null(stored.CaptureRequestId); Assert.Null(stored.CaptureRequestHash); Assert.Null(stored.CaptureResponseJson);
        Assert.Equal(task.RequestId, (await f.Db.UserExtensionImportTasks.SingleAsync()).RequestId);
        Assert.Equal("First list", (await f.Db.ShoppingLists.SingleAsync(l => l.Id == f.FirstList.Id)).Name);
        var indexes = await f.Db.Database.SqlQueryRaw<string>("""
            SELECT indexname AS "Value" FROM pg_indexes WHERE schemaname = current_schema()
            AND tablename = 'ProductImportJobs' AND indexname IN
            ('IX_ProductImportJobs_UserAccountId', 'IX_ProductImportJobs_UserAccountId_CaptureRequestId')
            """).ToArrayAsync();
        Assert.Equal(2, indexes.Length);
        Assert.Empty(await f.Db.Database.GetPendingMigrationsAsync());
    }

    [PostgreSqlFact]
    public async Task Database_enforces_capture_request_uniqueness_per_account()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        var first = Job(f, false);
        var second = Job(f, true); second.CaptureRequestId = first.CaptureRequestId;
        f.Db.AddRange(first, second); await f.Db.SaveChangesAsync();
        Assert.Equal(2, await f.Db.ProductImportJobs.CountAsync());
        var duplicate = Job(f, false); duplicate.CaptureRequestId = first.CaptureRequestId;
        f.Db.Add(duplicate);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [PostgreSqlFact]
    public async Task Rollback_refuses_to_erase_completed_capture_receipts()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var response = await client.PostAsJsonAsync("/api/user-extension/captures", f.Request());
        response.EnsureSuccessStatusCode();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Contains("Cannot remove capture receipts", error.MessageText);
        Assert.NotNull((await f.Db.ProductImportJobs.SingleAsync()).CaptureResponseJson);
        Assert.Single(await f.Db.Products.ToArrayAsync());
        Assert.Empty(await f.Db.Database.GetPendingMigrationsAsync());
    }

    private static ProductImportJob Job(CaptureApiScope f, bool second) => new()
    {
        UserAccountId = second ? f.Second.Id : f.First.Id, ShoppingListId = second ? f.SecondList.Id : f.FirstList.Id,
        SourceShopId = 1, SourceUrl = "https://www.coles.com.au/product/123456", NormalisedSourceUrl = "https://www.coles.com.au/product/123456",
        CreatedDate = f.Clock.Now.UtcDateTime, CaptureRequestId = Guid.NewGuid(), CaptureRequestHash = new string('A', 64), CaptureResponseJson = "{}"
    };
}
