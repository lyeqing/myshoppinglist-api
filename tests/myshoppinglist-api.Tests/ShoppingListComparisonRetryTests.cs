using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ShoppingListComparisonRetryTests
{
    internal static async Task FailAsync(ListFixture f, string code = "read_timeout")
    {
        var db = f.Scope.Db;
        await db.ProductImportJobs.Where(j => j.Id == f.Scope.Job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, ProductImportJobStatus.Partial).SetProperty(j => j.CompletedDate, f.Scope.Clock.Now.UtcDateTime)
            .SetProperty(j => j.ClaimToken, (Guid?)null).SetProperty(j => j.LeaseExpiresDate, (DateTime?)null));
        var other = await db.Shops.SingleAsync(s => s.Code == "woolworths");
        db.ProductImportRetailerResults.Add(new()
        {
            ProductImportJobId = f.Scope.Job.Id,
            ShopId = other.Id,
            Status = RetailerLookupStatus.CheckFailed,
            ErrorCode = code,
            CreatedDate = f.Scope.Clock.Now.UtcDateTime,
            UpdatedDate = f.Scope.Clock.Now.UtcDateTime
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [PostgreSqlFact]
    public async Task Concurrent_retries_reuse_job_preserve_edits_and_remove_personal_worker_reservation()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        var original = await f.ItemAsync();
        await f.UpdateAsync(new(7, "Keep my note", false, false, original.UpdatedDate));
        await FailAsync(f);
        f.Scope.Db.UserExtensionImportTasks.Add(new()
        {
            ProductImportJobId = f.Scope.Job.Id,
            RequestId = Guid.NewGuid(),
            Url = f.Scope.Job.SourceUrl,
            Status = "Failed",
            UpdatedDate = f.Scope.Clock.Now.UtcDateTime
        });
        await f.Scope.Db.SaveChangesAsync();
        async Task<long> Retry()
        {
            await using var db = PersistenceScope.Context();
            return (await new ShoppingListComparisonRetryService(db, f.Scope.Clock).RetryAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, default)).JobId;
        }
        var jobs = await Task.WhenAll(Retry(), Retry());
        Assert.All(jobs, id => Assert.Equal(f.Scope.Job.Id, id));
        Assert.Single(await f.Scope.Db.ProductImportJobs.ToArrayAsync());
        Assert.Empty(await f.Scope.Db.UserExtensionImportTasks.ToArrayAsync());
        var item = await f.ItemAsync();
        Assert.Equal(7, item.Quantity);
        Assert.Equal("Keep my note", item.Notes);
        var job = await f.Scope.Db.ProductImportJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ProductImportJobStatus.Queued, job.Status);
        Assert.Equal(f.ItemId, job.ShoppingListProductId);
        Assert.Null(job.ClaimToken);
        Assert.True(await f.Scope.Db.ProductImportRetailerResults.AnyAsync(r => r.Shop.Code == "woolworths" && r.Status == RetailerLookupStatus.Pending));
    }

    [PostgreSqlFact]
    public async Task Retry_enforces_access_and_blocked_cooldown_at_boundary()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await FailAsync(f, "retailer_access_restricted");
        var service = new ShoppingListComparisonRetryService(f.Scope.Db, f.Scope.Clock);
        Assert.Equal(401, (await Assert.ThrowsAsync<UserImportException>(() => service.RetryAsync(-1, f.Scope.ListId, f.ItemId, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.RetryAsync(f.Scope.UserId, -1, f.ItemId, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.RetryAsync(f.Scope.UserId, f.Scope.ListId, -1, default))).Status);
        f.Scope.Clock.Now += TimeSpan.FromMinutes(15) - TimeSpan.FromMilliseconds(1);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => service.RetryAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, default))).Status);
        f.Scope.Clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.Equal("Checking", (await service.RetryAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, default)).Status);
        await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        f.Scope.Db.ChangeTracker.Clear();
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => service.RetryAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, default))).Status);
    }

    [PostgreSqlFact]
    public async Task Shared_blocked_task_is_reset_only_after_its_cooldown()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await FailAsync(f);
        var task = new ColesExtensionTask
        {
            Key = "retry-test",
            Kind = "search",
            Url = "https://www.woolworths.com.au/shop/search/products?searchTerm=test",
            Status = "Failed",
            ErrorCode = "retailer_access_restricted",
            Attempts = 3,
            CompletedAt = f.Scope.Clock.Now.UtcDateTime,
            CreatedAt = f.Scope.Clock.Now.UtcDateTime
        };
        f.Scope.Db.Add(task); await f.Scope.Db.SaveChangesAsync();
        await f.Scope.Db.ProductImportJobs.Where(j => j.Id == f.Scope.Job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.ErrorCode, "extension_task_failed_" + task.Id));
        f.Scope.Db.ChangeTracker.Clear();
        var service = new ShoppingListComparisonRetryService(f.Scope.Db, f.Scope.Clock);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => service.RetryAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, default))).Status);
        f.Scope.Clock.Now += TimeSpan.FromMinutes(15);
        await service.RetryAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, default);
        var saved = await f.Scope.Db.ColesExtensionTasks.AsNoTracking().SingleAsync();
        Assert.Equal("Waiting", saved.Status);
        Assert.Equal(0, saved.Attempts);
    }
}
