using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ProductImportStatusTests
{
    [PostgreSqlFact]
    public async Task Comparison_status_exposes_verified_price_and_cache_metadata()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await RetailerComparisonTests.PrepareAsync(f);
        var provider = new RetailerComparisonTests.ComparisonProvider(RetailerComparisonTests.Other(f));
        await RetailerComparisonTests.SeedMappingAsync(f, claim, provider.Source);
        await RetailerComparisonTests.RunAsync(f, claim, provider);
        var status = (await new ProductImportStatusService(f.Scope.Db, f.Scope.Clock).ReadAsync(claim.UserAccountId, claim.JobId, default))!;
        var retailer = Assert.Single(status.Retailers, r => r.ShopId == 2);
        Assert.Equal(RetailerLookupStatus.Exact, retailer.Status); Assert.True(retailer.IsFromCache);
        var price = Assert.Single(retailer.Prices);
        Assert.Equal(19, price.Price); Assert.Equal("AUD", price.Currency);
        Assert.Equal(PriceScope.Unknown, price.PriceScope); Assert.Null(price.ShopLocationId);
        Assert.Equal(provider.Source.CheckedDate.UtcTicks / 10, retailer.CheckedDate!.Value.Ticks / 10);
    }

    [PostgreSqlFact]
    public async Task Uncertain_comparison_status_does_not_expose_a_price()
    {
        await using var f = await ImportFixture.CreateAsync();
        var claim = await RetailerComparisonTests.PrepareAsync(f);
        var source = RetailerComparisonTests.Other(f);
        var provider = new RetailerComparisonTests.ComparisonProvider(source with
        { Identity = source.Identity with { GTIN = null, PackQuantity = null } });
        await RetailerComparisonTests.RunAsync(f, claim, provider);
        var status = (await new ProductImportStatusService(f.Scope.Db, f.Scope.Clock).ReadAsync(claim.UserAccountId, claim.JobId, default))!;
        var retailer = Assert.Single(status.Retailers, r => r.ShopId == 2);
        Assert.Equal(RetailerLookupStatus.Likely, retailer.Status); Assert.Empty(retailer.Prices);
    }

    [PostgreSqlFact]
    public async Task Discovery_paginates_without_duplicates_and_checks_list_access()
    {
        await using var scope = await SubmissionScope.CreateAsync();
        var ids = new List<long>();
        for (var i = 0; i < 3; i++) ids.Add((await scope.SubmitAsync(new(scope.Url + i))).Response!.JobId);
        var service = new ProductImportStatusService(scope.Db, scope.Clock);
        var first = (await service.ListAsync(scope.AccountId, scope.ListId, null, 2, default))!;
        Assert.Equal(ids.AsEnumerable().Reverse().Take(2), first.Items.Select(i => i.JobId));
        var second = (await service.ListAsync(scope.AccountId, scope.ListId, first.NextBeforeId, 2, default))!;
        Assert.Equal(ids[0], Assert.Single(second.Items).JobId); Assert.Null(second.NextBeforeId);
        Assert.Null(await service.ListAsync(scope.AccountId + 100000, scope.ListId, null, 2, default));
        scope.Clock.Now += TimeSpan.FromHours(4);
        Assert.Null(await service.ListAsync(scope.AccountId, scope.ListId, null, 2, default));
    }

    [PostgreSqlFact]
    public async Task Worker_commit_during_poll_does_not_mix_progress_and_price_snapshots()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var claim = (await fixture.Jobs().ClaimNextAsync(default))!;
        var saved = await fixture.Scope.Service.SaveAsync(claim.JobId, claim.UserAccountId, claim.Token, fixture.Scope.Source, default);
        Assert.Equal(ProductPersistenceStatus.Success, saved.Status);
        var interceptor = new DuringProductRead(async () =>
        {
            await using var writer = PersistenceScope.Context();
            await using var transaction = await writer.Database.BeginTransactionAsync();
            await writer.ProductImportJobs.Where(j => j.Id == claim.JobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, ProductImportJobStatus.Partial).SetProperty(j => j.ProgressStage, ProductImportProgressStage.Completed)
                .SetProperty(j => j.ClaimToken, (Guid?)null).SetProperty(j => j.LeaseExpiresDate, (DateTime?)null));
            await writer.ShopProductPrices.Where(p => p.ShopProductId == saved.ShopProductId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 99));
            await transaction.CommitAsync();
        });
        await using var reader = new MyShoppingListDbContext(new DbContextOptionsBuilder<MyShoppingListDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!).AddInterceptors(interceptor).Options);
        var service = new ProductImportStatusService(reader, fixture.Scope.Clock);
        var before = (await service.ReadAsync(claim.UserAccountId, claim.JobId, default))!;
        Assert.True(interceptor.Fired);
        Assert.Equal(ProductImportJobStatus.Processing, before.Status);
        Assert.Equal(23, Assert.Single(Assert.Single(before.Retailers).Prices).Price);
        var after = (await service.ReadAsync(claim.UserAccountId, claim.JobId, default))!;
        Assert.Equal(ProductImportJobStatus.Partial, after.Status);
        Assert.Equal(99, Assert.Single(Assert.Single(after.Retailers).Prices).Price);
    }

    [PostgreSqlFact]
    public async Task Queued_status_exposes_pending_retailers_without_product_or_prices()
    {
        await using var scope = await SubmissionScope.CreateAsync();
        var accepted = await scope.SubmitAsync(new(scope.Url, 2));
        var status = await new ProductImportStatusService(scope.Db, scope.Clock).ReadAsync(scope.AccountId, accepted.Response!.JobId, default);
        Assert.NotNull(status); Assert.Null(status.Product); Assert.Null(status.ShoppingListProductId);
        Assert.Equal(ProductImportJobStatus.Queued, status.Status);
        Assert.Equal(5, status.Retailers.Count);
        Assert.All(status.Retailers, r => { Assert.Equal(RetailerLookupStatus.Pending, r.Status); Assert.Empty(r.Prices); });
    }

    [PostgreSqlFact]
    public async Task Source_product_and_price_are_visible_while_job_is_processing()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var claim = (await fixture.Jobs().ClaimNextAsync(default))!;
        var saved = await fixture.Scope.Service.SaveAsync(claim.JobId, claim.UserAccountId, claim.Token, fixture.Scope.Source, default);
        Assert.Equal(ProductPersistenceStatus.Success, saved.Status);
        var service = new ProductImportStatusService(fixture.Scope.Db, fixture.Scope.Clock);
        var status = (await service.ReadAsync(claim.UserAccountId, claim.JobId, default))!;
        Assert.Equal(ProductImportJobStatus.Processing, status.Status);
        Assert.Equal(ProductImportProgressStage.CheckingRetailers, status.ProgressStage);
        Assert.Equal(saved.ProductId, status.Product!.Id);
        var price = Assert.Single(Assert.Single(status.Retailers).Prices);
        Assert.Equal(23, price.Price); Assert.Equal("AUD", price.Currency);
        Assert.Equal(PriceScope.Unknown, price.PriceScope); Assert.Null(price.ShopLocationId);
        Assert.Equal(fixture.Scope.Offer.CheckedDate.UtcTicks / 10, price.CheckedDate.Ticks / 10);
    }

    [PostgreSqlFact]
    public async Task Different_currency_and_scope_remain_separate_and_failure_does_not_claim_old_price()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var claim = (await fixture.Jobs().ClaimNextAsync(default))!;
        var saved = await fixture.Scope.Service.SaveAsync(claim.JobId, claim.UserAccountId, claim.Token, fixture.Scope.Source, default);
        fixture.Scope.Db.ShopProductPrices.Add(new()
        {
            ShopProductId = saved.ShopProductId!.Value, Price = 12, Currency = "USD", PriceScope = PriceScope.National,
            SourceType = SourceType.StructuredData, SourceUrl = fixture.Scope.Source.ProductUrl.AbsoluteUri,
            CheckedDate = fixture.Scope.Clock.Now.UtcDateTime, CreatedDate = fixture.Scope.Clock.Now.UtcDateTime, UpdatedDate = fixture.Scope.Clock.Now.UtcDateTime
        });
        await fixture.Scope.Db.SaveChangesAsync(); fixture.Scope.Db.ChangeTracker.Clear();
        var service = new ProductImportStatusService(fixture.Scope.Db, fixture.Scope.Clock);
        var status = (await service.ReadAsync(claim.UserAccountId, claim.JobId, default))!;
        Assert.Equal(2, Assert.Single(status.Retailers).Prices.Count);
        Assert.Contains(status.Retailers[0].Prices, p => p.Currency == "USD" && p.PriceScope == PriceScope.National);
        await fixture.Scope.Db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == claim.JobId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RetailerLookupStatus.Unavailable).SetProperty(r => r.ErrorCode, "timeout"));
        status = (await service.ReadAsync(claim.UserAccountId, claim.JobId, default))!;
        Assert.Equal(RetailerLookupStatus.Unavailable, status.Retailers[0].Status);
        Assert.Empty(status.Retailers[0].Prices);
    }

    [PostgreSqlFact]
    public async Task Ownership_archival_and_expiry_restrict_status_reads()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var service = new ProductImportStatusService(fixture.Scope.Db, fixture.Scope.Clock);
        Assert.Null(await service.ReadAsync(fixture.Scope.UserId + 100000, fixture.Scope.Job.Id, default));
        Assert.Null(await service.ReadAsync(fixture.Scope.UserId, long.MaxValue, default));
        await fixture.Scope.Db.ShoppingLists.Where(l => l.Id == fixture.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        Assert.Null(await service.ReadAsync(fixture.Scope.UserId, fixture.Scope.Job.Id, default));
        await fixture.Scope.Db.ShoppingLists.Where(l => l.Id == fixture.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, false));
        fixture.Scope.Clock.Now += TimeSpan.FromHours(4);
        Assert.Null(await service.ReadAsync(fixture.Scope.UserId, fixture.Scope.Job.Id, default));
    }

    private sealed class DuringProductRead(Func<Task> update) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (!Fired && command.CommandText.Contains("FROM \"Products\"", StringComparison.Ordinal))
            { Fired = true; await update(); }
            return result;
        }
    }
}
