using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Tests;

public class ProductPersistenceTests
{
    [PostgreSqlFact]
    public async Task Same_gtin_across_retailers_reuses_one_canonical_product()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var first = await scope.SaveAsync();
        Assert.Equal(ProductPersistenceStatus.Success, first.Status);
        var other = scope.Source with { ShopCode = "woolworths", ProductUrl = new("https://www.woolworths.com.au/shop/productdetails/" + scope.Code), Offer = null };
        var job = await scope.AddJobAsync(other);
        var second = await scope.SaveAsync(job, other);
        Assert.Equal(ProductPersistenceStatus.Success, second.Status);
        Assert.Equal(first.ProductId, second.ProductId);
        Assert.Equal(first.ShoppingListProductId, second.ShoppingListProductId);
        Assert.Equal(2, await scope.Db.ShopProducts.CountAsync(p => p.ProductId == first.ProductId));
    }

    [PostgreSqlFact]
    public async Task Retry_preserves_quantity_notes_and_purchase_state()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var first = await scope.SaveAsync();
        Assert.Equal(ProductPersistenceStatus.Success, first.Status);
        var item = await scope.Db.ShoppingListProducts.SingleAsync(i => i.Id == first.ShoppingListProductId);
        item.Quantity = 7; item.Notes = "Keep this"; item.IsPurchased = true; item.IsHidden = true;
        item.PurchasedDate = scope.Clock.GetUtcNow().UtcDateTime;
        await scope.Db.SaveChangesAsync();
        var retry = await scope.SaveAsync();
        Assert.Equal(ProductPersistenceStatus.Success, retry.Status);
        Assert.True(retry.ExistingListItem);
        var stored = await scope.Db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(7, stored.Quantity); Assert.Equal("Keep this", stored.Notes);
        Assert.True(stored.IsPurchased); Assert.True(stored.IsHidden);
        Assert.Single(await scope.Db.ShopProductPriceHistory.Where(p => p.ShopProductId == first.ShopProductId).ToListAsync());
    }

    [PostgreSqlFact]
    public async Task Invalid_offer_rolls_back_already_saved_product_and_mapping()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var bad = scope.Source with { Offer = new ProviderResult<ShopProductOffer>.Success(scope.Offer with { Price = -1 }) };
        var result = await scope.SaveAsync(source: bad);
        Assert.Equal(ProductPersistenceStatus.InvalidData, result.Status);
        Assert.False(await scope.Db.ShopProducts.AnyAsync(p => p.ShopProductCode == scope.Code));
        Assert.False(await scope.Db.Products.AnyAsync(p => p.Name == scope.Source.Identity.Name));
        var job = await scope.Db.ProductImportJobs.AsNoTracking().SingleAsync(j => j.Id == scope.Job.Id);
        Assert.Null(job.ProductId); Assert.Null(job.ShoppingListProductId);
    }

    [PostgreSqlFact]
    public async Task Failed_price_lookup_keeps_identified_product_and_records_failure()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var source = scope.Source with { Offer = new ProviderResult<ShopProductOffer>.Failure(new(ProviderFailureKind.Timeout, "timeout", "Unavailable")) };
        var result = await scope.SaveAsync(source: source);
        Assert.Equal(ProductPersistenceStatus.Success, result.Status);
        Assert.False(result.PriceSaved);
        var status = await scope.Db.ProductImportRetailerResults.SingleAsync(r => r.ProductImportJobId == scope.Job.Id);
        Assert.Equal(RetailerLookupStatus.Unavailable, status.Status);
        Assert.NotNull(status.ShopProductId);
    }

    [PostgreSqlFact]
    public async Task Wrong_owner_or_claim_cannot_write_catalogue()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var denied = await scope.Service.SaveAsync(scope.Job.Id, scope.UserId + 100000, scope.Job.ClaimToken!.Value, scope.Source, CancellationToken.None);
        Assert.Equal(ProductPersistenceStatus.AccessDenied, denied.Status);
        var lost = await scope.Service.SaveAsync(scope.Job.Id, scope.UserId, Guid.NewGuid(), scope.Source, CancellationToken.None);
        Assert.Equal(ProductPersistenceStatus.LostClaim, lost.Status);
        Assert.False(await scope.Db.ShopProducts.AnyAsync(p => p.ShopProductCode == scope.Code));
    }

    [PostgreSqlFact]
    public async Task Expired_trial_is_rejected()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        await scope.Db.UserAccounts.Where(u => u.Id == scope.UserId).ExecuteUpdateAsync(set => set.SetProperty(u => u.ExpiresDate, scope.Clock.GetUtcNow().UtcDateTime.AddSeconds(-1)));
        Assert.Equal(ProductPersistenceStatus.ExpiredAccount, (await scope.SaveAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task Archived_list_is_rejected()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        await scope.Db.ShoppingLists.Where(l => l.Id == scope.ListId).ExecuteUpdateAsync(set => set.SetProperty(l => l.IsArchived, true));
        Assert.Equal(ProductPersistenceStatus.ListUnavailable, (await scope.SaveAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task Contradictory_gtin_does_not_overwrite_existing_product()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var first = await scope.SaveAsync();
        var bad = scope.Source with { Identity = scope.Source.Identity with { PackQuantity = 30 } };
        Assert.Equal(ProductPersistenceStatus.IdentityConflict, (await scope.SaveAsync(source: bad)).Status);
        Assert.Equal(24, (await scope.Db.Products.AsNoTracking().SingleAsync(p => p.Id == first.ProductId)).PackQuantity);
    }

    [PostgreSqlFact]
    public async Task Lease_expiring_during_save_rolls_back_every_write()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        // Each time read advances one minute: the claim is valid initially but expires before commit.
        scope.Clock.Step = TimeSpan.FromMinutes(1);
        await scope.Db.ProductImportJobs.Where(j => j.Id == scope.Job.Id).ExecuteUpdateAsync(set => set.SetProperty(
            j => j.LeaseExpiresDate, scope.Clock.Now.UtcDateTime.AddMinutes(6)));
        Assert.Equal(ProductPersistenceStatus.LostClaim, (await scope.SaveAsync()).Status);
        Assert.False(await scope.Db.ShopProducts.AnyAsync(p => p.ShopProductCode == scope.Code));
    }

    [PostgreSqlFact]
    public async Task Concurrent_imports_commit_one_product_and_one_list_item()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var secondJob = await scope.AddJobAsync(scope.Source);
        await scope.CommitSetupAsync();
        try
        {
            async Task<ProductPersistenceResult> Save(ProductImportJob job)
            {
                await using var db = PersistenceScope.Context();
                return await PersistenceScope.BuildService(db, scope.Clock).SaveAsync(job.Id, scope.UserId,
                    job.ClaimToken!.Value, scope.Source, CancellationToken.None);
            }
            var results = await Task.WhenAll(Save(scope.Job), Save(secondJob));
            Assert.All(results, r => Assert.Equal(ProductPersistenceStatus.Success, r.Status));
            Assert.Equal(results[0].ProductId, results[1].ProductId);
            Assert.Equal(results[0].ShoppingListProductId, results[1].ShoppingListProductId);
            Assert.Single(await scope.Db.ShopProducts.Where(p => p.ShopProductCode == scope.Code).ToListAsync());
            Assert.Equal(2, (await scope.Db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == results[0].ShoppingListProductId)).Quantity);
        }
        finally { await scope.CleanupCommittedAsync(); }
    }
}

internal sealed class PersistenceClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public TimeSpan Step { get; set; }
    public override DateTimeOffset GetUtcNow() { var result = Now; Now += Step; return result; }
}

internal sealed class PersistenceScope : IAsyncDisposable
{
    public MyShoppingListDbContext Db { get; } = Context();
    public PersistenceClock Clock { get; } = new();
    public string Code { get; } = "persistence-test-" + Guid.NewGuid().ToString("N");
    public long UserId { get; private set; }
    public long ListId { get; private set; }
    public ProductImportJob Job { get; private set; } = null!;
    public ExtractedShopProduct Source { get; private set; } = null!;
    public ShopProductOffer Offer => ((ProviderResult<ShopProductOffer>.Success)Source.Offer!).Value;
    public SourceProductPersistenceService Service => BuildService(Db, Clock);
    private IDbContextTransaction? _transaction;

    public static MyShoppingListDbContext Context() => new(new DbContextOptionsBuilder<MyShoppingListDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!).Options);
    public static SourceProductPersistenceService BuildService(MyShoppingListDbContext db, TimeProvider clock)
    {
        var normalisation = new ProductNormalisationService();
        var urls = new ProductUrlValidator(new RetailerCatalog());
        return new(db, new(db, normalisation, new(normalisation)), new(db),
            new(db, Options.Create(new PriceOptions()), clock, urls), normalisation, urls, clock,
            NullLogger<SourceProductPersistenceService>.Instance);
    }

    public static async Task<PersistenceScope> CreateAsync()
    {
        var scope = new PersistenceScope();
        scope._transaction = await scope.Db.Database.BeginTransactionAsync();
        var now = scope.Clock.GetUtcNow();
        var user = new UserAccount { IsTrial = true, DisplayName = scope.Code, ExpiresDate = now.UtcDateTime.AddHours(3), CreatedDate = now.UtcDateTime, UpdatedDate = now.UtcDateTime };
        var list = new ShoppingList { UserAccount = user, Name = scope.Code, CreatedDate = now.UtcDateTime, UpdatedDate = now.UtcDateTime };
        scope.Db.Add(list); await scope.Db.SaveChangesAsync();
        scope.UserId = user.Id; scope.ListId = list.Id;
        var digits = Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sum = digits.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        var gtin = digits + ((10 - sum % 10) % 10);
        var url = new Uri("https://www.coles.com.au/product/" + scope.Code);
        scope.Source = new()
        {
            ShopCode = "coles", ShopProductCode = scope.Code, ProductUrl = url, CheckedDate = now.AddMinutes(-1), SourceType = SourceType.StructuredData,
            Identity = new() { Name = scope.Code + " Classic Cans", Brand = scope.Code, Variant = "Classic", GTIN = gtin, PackQuantity = 24, PackSize = 375, PackUnit = "mL" },
            Offer = new ProviderResult<ShopProductOffer>.Success(new()
            {
                ShopCode = "coles", Price = 23, Currency = "AUD", PriceScope = PriceScope.Unknown, SourceType = SourceType.StructuredData,
                SourceUrl = url, CheckedDate = now.AddMinutes(-1)
            })
        };
        scope.Job = await scope.AddJobAsync(scope.Source);
        return scope;
    }

    public async Task<ProductImportJob> AddJobAsync(ExtractedShopProduct source)
    {
        var job = new ProductImportJob
        {
            UserAccountId = UserId, ShoppingListId = ListId, SourceUrl = source.ProductUrl.AbsoluteUri,
            NormalisedSourceUrl = source.ProductUrl.AbsoluteUri, RequestedQuantity = 2, Status = ProductImportJobStatus.Processing,
            ClaimToken = Guid.NewGuid(), LeaseExpiresDate = Clock.GetUtcNow().UtcDateTime.AddMinutes(30), CreatedDate = Clock.GetUtcNow().UtcDateTime
        };
        Db.Add(job); await Db.SaveChangesAsync(); Db.ChangeTracker.Clear(); return job;
    }

    public Task<ProductPersistenceResult> SaveAsync(ProductImportJob? job = null, ExtractedShopProduct? source = null)
    {
        Db.ChangeTracker.Clear(); job ??= Job;
        return Service.SaveAsync(job.Id, UserId, job.ClaimToken!.Value, source ?? Source, CancellationToken.None);
    }
    public async Task CommitSetupAsync()
    {
        await _transaction!.CommitAsync(); await _transaction.DisposeAsync(); _transaction = null;
    }
    public async Task CleanupCommittedAsync()
    {
        Db.ChangeTracker.Clear();
        await using var cleanup = await Db.Database.BeginTransactionAsync();
        var productIds = await Db.ShopProducts.Where(p => p.ShopProductCode == Code).Select(p => p.ProductId).ToListAsync();
        var mappingIds = await Db.ShopProducts.Where(p => p.ShopProductCode == Code).Select(p => p.Id).ToListAsync();
        await Db.UserAccounts.Where(u => u.Id == UserId).ExecuteDeleteAsync();
        await Db.ShopProductPrices.Where(p => mappingIds.Contains(p.ShopProductId)).ExecuteDeleteAsync();
        await Db.ShopProductPriceHistory.Where(p => mappingIds.Contains(p.ShopProductId)).ExecuteDeleteAsync();
        await Db.ShopProducts.Where(p => mappingIds.Contains(p.Id)).ExecuteDeleteAsync();
        await Db.Products.Where(p => productIds.Contains(p.Id)).ExecuteDeleteAsync();
        await cleanup.CommitAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null) { await _transaction.RollbackAsync(); await _transaction.DisposeAsync(); }
        await Db.DisposeAsync();
    }
}
