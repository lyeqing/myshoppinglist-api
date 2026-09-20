using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Tests;

[CollectionDefinition("Import worker database", DisableParallelization = true)]
public sealed class ImportWorkerDatabaseCollection;

[Collection("Import worker database")]
public class ProductImportJobServiceTests
{
    [PostgreSqlFact]
    public async Task Competing_workers_claim_a_job_only_once()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        async Task<ProductImportClaim?> Claim()
        {
            await using var db = PersistenceScope.Context();
            return await fixture.Jobs(db).ClaimNextAsync(default);
        }
        var claims = await Task.WhenAll(Claim(), Claim());
        var claim = Assert.Single(claims, c => c is not null);
        Assert.Equal(fixture.Scope.Job.Id, claim!.JobId);
        var stored = await fixture.ReadAsync();
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(ProductImportJobStatus.Processing, stored.Status);
    }

    [PostgreSqlFact]
    public async Task Retry_backoff_is_durable_and_stops_at_attempt_limit()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var jobs = fixture.Jobs();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claim = Assert.IsType<ProductImportClaim>(await jobs.ClaimNextAsync(default));
            Assert.Equal(attempt, (await fixture.ReadAsync()).AttemptCount);
            Assert.True(await jobs.FailAsync(claim, "timeout", true, default));
            var stored = await fixture.ReadAsync();
            Assert.Null(stored.ClaimToken); Assert.Null(stored.LeaseExpiresDate);
            Assert.Null(await jobs.ClaimNextAsync(default));
            if (attempt < 3)
            {
                Assert.Equal(ProductImportJobStatus.Queued, stored.Status);
                Assert.Equal(fixture.Scope.Clock.Now.UtcDateTime.AddSeconds(30 * Math.Pow(2, attempt - 1)).Ticks / 10,
                    stored.NextAttemptDate!.Value.Ticks / 10);
                fixture.Scope.Clock.Now += TimeSpan.FromMinutes(2);
            }
            else Assert.Equal(ProductImportJobStatus.Failed, stored.Status);
        }
    }

    [PostgreSqlFact]
    public async Task Recovery_replaces_claim_and_fences_old_workers()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var jobs = fixture.Jobs();
        var first = (await jobs.ClaimNextAsync(default))!;
        fixture.Scope.Clock.Now += TimeSpan.FromSeconds(601);
        Assert.False(await jobs.RenewAsync(first, default));
        Assert.Equal(1, await jobs.RecoverExpiredAsync(default));
        var second = (await jobs.ClaimNextAsync(default))!;
        Assert.NotEqual(first.Token, second.Token);
        Assert.False(await jobs.ProgressAsync(first, ProductImportProgressStage.Completed, default));
        Assert.False(await jobs.FailAsync(first, "stale", false, default));
        Assert.False(await jobs.CompleteSourceStageAsync(first, default));
        Assert.True(await jobs.RenewAsync(second, default));
        Assert.Equal(second.Token, (await fixture.ReadAsync()).ClaimToken);
    }

    [PostgreSqlFact]
    public async Task Renewal_prevents_recovery_and_expiry_eventually_exhausts_attempts()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var jobs = fixture.Jobs();
        var claim = (await jobs.ClaimNextAsync(default))!;
        fixture.Scope.Clock.Now += TimeSpan.FromSeconds(590);
        Assert.True(await jobs.RenewAsync(claim, default));
        fixture.Scope.Clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(0, await jobs.RecoverExpiredAsync(default));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            fixture.Scope.Clock.Now += TimeSpan.FromSeconds(601);
            Assert.Equal(1, await jobs.RecoverExpiredAsync(default));
            if (attempt < 3) Assert.NotNull(await jobs.ClaimNextAsync(default));
        }
        Assert.Equal(ProductImportJobStatus.Failed, (await fixture.ReadAsync()).Status);
        Assert.Null(await jobs.ClaimNextAsync(default));
    }

    [PostgreSqlFact]
    public async Task Saved_source_survives_exhausted_recovery()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var claim = (await fixture.Jobs().ClaimNextAsync(default))!;
        var saved = await fixture.Scope.Service.SaveAsync(claim.JobId, claim.UserAccountId, claim.Token, fixture.Scope.Source, default);
        Assert.Equal(ProductPersistenceStatus.Success, saved.Status);
        fixture.Scope.Db.ProductImportRetailerResults.Add(new()
        {
            ProductImportJobId = claim.JobId, ShopId = 2, Status = RetailerLookupStatus.Checking,
            CreatedDate = fixture.Scope.Clock.Now.UtcDateTime, UpdatedDate = fixture.Scope.Clock.Now.UtcDateTime
        });
        await fixture.Scope.Db.SaveChangesAsync(); fixture.Scope.Db.ChangeTracker.Clear();
        await fixture.Scope.Db.ProductImportJobs.Where(j => j.Id == claim.JobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.AttemptCount, 3));
        fixture.Scope.Clock.Now += TimeSpan.FromSeconds(601);
        await fixture.Jobs().RecoverExpiredAsync(default);
        var job = await fixture.ReadAsync();
        Assert.Equal(ProductImportJobStatus.Partial, job.Status);
        Assert.Equal(saved.ShoppingListProductId, job.ShoppingListProductId);
        Assert.True(await fixture.Scope.Db.ShoppingListProducts.AnyAsync(i => i.Id == saved.ShoppingListProductId));
        var retailer = await fixture.Scope.Db.ProductImportRetailerResults.SingleAsync(r => r.ProductImportJobId == claim.JobId && r.ShopId == 2);
        Assert.Equal(RetailerLookupStatus.CheckFailed, retailer.Status);
        Assert.Equal("comparison_interrupted", retailer.ErrorCode); Assert.NotNull(retailer.CompletedDate);
    }

    [PostgreSqlFact]
    public async Task Finalisation_preserves_completed_results_and_marks_unfinished_checks_failed()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await RetailerComparisonTests.PrepareAsync(f);
        await using var services = RetailerComparisonTests.Services(f, new RetailerComparisonTests.ComparisonProvider(RetailerComparisonTests.Other(f)));
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(services);
        var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<RetailerComparisonPersistenceService>(scope.ServiceProvider);
        Assert.True(await store.SaveStatusAsync(claim, 2, RetailerLookupStatus.NotFound, "no_verified_match_in_candidates", default));
        Assert.True(await f.Jobs().CompleteSourceStageAsync(claim, default));
        Assert.Equal(RetailerLookupStatus.NotFound, (await RetailerComparisonTests.ResultAsync(f)).Status);
        var unfinished = await f.Scope.Db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == claim.JobId && r.ShopId > 2).ToListAsync();
        Assert.Equal(3, unfinished.Count); Assert.All(unfinished, r => Assert.Equal(RetailerLookupStatus.CheckFailed, r.Status));
    }

    [PostgreSqlFact]
    public async Task Provider_retry_after_is_respected_with_a_bounded_delay()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var jobs = fixture.Jobs();
        var claim = (await jobs.ClaimNextAsync(default))!;
        await jobs.FailAsync(claim, "rate_limited", true, default, TimeSpan.FromMinutes(5));
        Assert.Equal(fixture.Scope.Clock.Now.UtcDateTime.AddMinutes(5).Ticks / 10,
            (await fixture.ReadAsync()).NextAttemptDate!.Value.Ticks / 10);
    }
}

internal sealed class ImportFixture : IAsyncDisposable
{
    public PersistenceScope Scope { get; }
    public ProductImportOptions Options { get; } = new();
    private ImportFixture(PersistenceScope scope) => Scope = scope;
    public static async Task<ImportFixture> CreateAsync()
    {
        var scope = await PersistenceScope.CreateAsync();
        try
        {
            // These tests exercise the real queue. Refuse to consume unrelated user jobs.
            Assert.False(await scope.Db.ProductImportJobs.AnyAsync(j => j.Id != scope.Job.Id &&
                (j.Status == ProductImportJobStatus.Queued || j.Status == ProductImportJobStatus.Processing)),
                "Worker integration tests require a database with no unrelated active import jobs.");
            await scope.Db.ProductImportJobs.Where(j => j.Id == scope.Job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, ProductImportJobStatus.Queued).SetProperty(j => j.ClaimToken, (Guid?)null)
                .SetProperty(j => j.LeaseExpiresDate, (DateTime?)null));
            await scope.CommitSetupAsync();
            return new(scope);
        }
        catch { await scope.DisposeAsync(); throw; }
    }
    public ProductImportJobService Jobs(myshoppinglist_api.Data.MyShoppingListDbContext? db = null) =>
        new(db ?? Scope.Db, Microsoft.Extensions.Options.Options.Create(Options), Scope.Clock, NullLogger<ProductImportJobService>.Instance);
    public Task<ProductImportJob> ReadAsync() => Scope.Db.ProductImportJobs.AsNoTracking().SingleAsync(j => j.Id == Scope.Job.Id);
    public async ValueTask DisposeAsync()
    {
        try { await Scope.CleanupCommittedAsync(); }
        finally { await Scope.DisposeAsync(); }
    }
}
