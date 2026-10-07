using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ContributionTests
{
    [PostgreSqlFact]
    public async Task Retailer_block_cools_shared_task_without_penalising_contributor()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var db = PersistenceScope.Context();
        var user = AdminAccountTests.TestAccount();
        var other = AdminAccountTests.TestAccount();
        db.AddRange(user, other);
        var task = new ColesExtensionTask
        {
            Key = "search-test-" + Guid.NewGuid(),
            Kind = "search",
            Url = "https://www.woolworths.com.au/shop/search/products?searchTerm=test",
            Query = "test",
            CreatedAt = DateTime.UtcNow
        };
        db.Add(task);
        await db.SaveChangesAsync();
        await using var app = new ContributionTestApp();
        using var scope = app.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ColesExtensionTaskService>();
        var service = scope.ServiceProvider.GetRequiredService<ContributionService>();
        var claim = Assert.IsType<ColesTaskClaim>(await service.ClaimAsync(user.Id, default));
        await service.SubmitAsync(user.Id, task.Id, new(claim.ClaimToken, task.Url, false,
            ErrorCode: "retailer_access_restricted"), default);

        Assert.Null(await service.ClaimAsync(other.Id, default));
        Assert.Null(await queue.ClaimAsync(task.Id, "customer-" + other.Id, default, other.Id));
        var saved = await db.ColesExtensionTasks.AsNoTracking().SingleAsync();
        Assert.Equal("Waiting", saved.Status);
        Assert.Equal("retailer_access_restricted", saved.ErrorCode);
        var account = await db.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == user.Id);
        Assert.True(account.IsActive);
        Assert.True(account.ContributionEnabled);
        Assert.False(account.ContributionBlocked);
        Assert.Null(account.RestrictionReason);
        var observation = await db.ContributionObservations.SingleAsync();
        Assert.Equal(user.Id, observation.UserAccountId);
        Assert.Equal("ExtractionFailed", observation.Outcome);
    }

    [PostgreSqlFact]
    public async Task Claims_are_exclusive_results_attributed_and_blocking_rejects_existing_claim()
    {
        await using var db = PersistenceScope.Context();
        var user = AdminAccountTests.TestAccount(); db.Add(user); await db.SaveChangesAsync();
        var task = new ColesExtensionTask
        {
            Key = "search-test-" + Guid.NewGuid(),
            Kind = "search",
            Url = "https://www.coles.com.au/search/products?q=test",
            Query = "test",
            CreatedAt = DateTime.UtcNow
        };
        var second = new ColesExtensionTask { Key = "search-test-" + Guid.NewGuid(), Kind = "search", Url = task.Url, Query = "test", CreatedAt = DateTime.UtcNow };
        db.AddRange(task, second); await db.SaveChangesAsync();
        try
        {
            await using var app = new ContributionTestApp();
            using var scope = app.Services.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<ColesExtensionTaskService>();
            var service = scope.ServiceProvider.GetRequiredService<ContributionService>();
            var claim = await queue.ClaimAsync(task.Id, "customer-" + user.Id, default, user.Id);
            Assert.NotNull(claim);
            Assert.Null(await queue.ClaimAsync(task.Id, "another-worker", default));
            Assert.Null(await queue.ClaimAsync(second.Id, "customer-" + user.Id, default, user.Id));
            var result = new ContributionResult(claim.ClaimToken, task.Url, true, Links: [], EmptyConfirmed: true, ExtensionVersion: "0.2.0");
            await service.SubmitAsync(user.Id, task.Id, result, default);
            var observation = await db.ContributionObservations.SingleAsync(o => o.TaskId == task.Id);
            Assert.Equal(user.Id, observation.UserAccountId); Assert.Equal("Accepted", observation.Outcome);
            Assert.Equal("0.2.0", observation.ExtensionVersion);
            await service.SubmitAsync(user.Id, task.Id, result, default);
            var next = await queue.ClaimAsync(second.Id, "customer-" + user.Id, default, user.Id);
            Assert.NotNull(next);
            await db.UserAccounts.Where(a => a.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContributionBlocked, true));
            Assert.Equal(403, (await Assert.ThrowsAsync<UserImportException>(() => service.SubmitAsync(user.Id, second.Id,
                result with { ClaimToken = next.ClaimToken }, default))).Status);
            Assert.Null(await service.ClaimAsync(user.Id, default));
            await db.UserAccounts.Where(a => a.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContributionBlocked, false).SetProperty(a => a.ContributionEnabled, false));
            Assert.Null(await service.ClaimAsync(user.Id, default));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.ContributionObservations.Where(o => o.UserAccountId == user.Id).ExecuteDeleteAsync();
            await db.ColesExtensionTasks.Where(t => t.Id == task.Id || t.Id == second.Id).ExecuteDeleteAsync();
            await db.UserAccounts.Where(a => a.Id == user.Id).ExecuteDeleteAsync();
        }
    }
}
