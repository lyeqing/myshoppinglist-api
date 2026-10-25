using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using myshoppinglist_api.Services.Models;
using Npgsql;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class PersonalProductPersistenceTests
{
    [PostgreSqlFact]
    public async Task Same_product_has_independent_metadata_prices_and_history_for_two_accounts_and_legacy()
    {
        await using var f = await PersonalScope.CreateAsync();
        var a = await f.CaptureAsync(f.First);
        var b = await f.CaptureAsync(f.Second, f.Source with { Description = "Second account description", Offer = Offer(f.Source, 8m) });
        var legacyJob = await f.JobAsync(f.Second, f.Source);
        var legacy = await f.Service.SaveAsync(legacyJob.Id, f.Second.Account, legacyJob.ClaimToken!.Value, f.Source, default);
        Assert.Equal(ProductPersistenceStatus.Success, a.Status);
        Assert.Equal(ProductPersistenceStatus.Success, b.Status);
        Assert.Equal(ProductPersistenceStatus.Success, legacy.Status);
        Assert.Equal(3, new[] { a.ProductId, b.ProductId, legacy.ProductId }.Distinct().Count());
        Assert.Equal(3, await f.Db.ShopProducts.CountAsync());
        Assert.Equal(f.First.Account, (await f.Db.Products.SingleAsync(p => p.Id == a.ProductId)).UserAccountId);
        Assert.Equal("Second account description", (await f.Db.Products.SingleAsync(p => p.Id == b.ProductId)).Description);
        Assert.Null((await f.Db.Products.SingleAsync(p => p.Id == a.ProductId)).Description);
        Assert.Equal(4m, (await f.Db.ShopProductPrices.SingleAsync(p => p.UserAccountId == f.First.Account)).Price);
        Assert.Equal(f.Source.CheckedDate.UtcDateTime, (await f.Db.ShopProductPriceHistory.SingleAsync(p => p.UserAccountId == f.First.Account)).CheckedDate);
        Assert.Equal(8m, (await f.Db.ShopProductPrices.SingleAsync(p => p.UserAccountId == f.Second.Account)).Price);
        Assert.Equal(4m, (await f.Db.ShopProductPrices.SingleAsync(p => p.UserAccountId == null)).Price);
        Assert.Equal(3, await f.Db.ShopProductPriceHistory.CountAsync());

        f.Clock.Now += TimeSpan.FromSeconds(1);
        var updated = f.Source with { CheckedDate = f.Clock.Now, Offer = Offer(f.Source, 3m, f.Clock.Now) };
        var again = await f.CaptureAsync(f.First, updated);
        Assert.Equal(a.ProductId, again.ProductId);
        Assert.Equal(a.ShoppingListProductId, again.ShoppingListProductId);
        Assert.Equal(3m, (await f.Db.ShopProductPrices.SingleAsync(p => p.UserAccountId == f.First.Account)).Price);
        Assert.Equal(8m, (await f.Db.ShopProductPrices.SingleAsync(p => p.UserAccountId == f.Second.Account)).Price);
        Assert.Equal(2, await f.Db.ShopProductPriceHistory.CountAsync(p => p.UserAccountId == f.First.Account));
        Assert.Equal(ProductImportJobStatus.Completed, (await f.Db.ProductImportJobs.SingleAsync(j => j.Id == f.LastJob!.Id)).Status);
        Assert.Empty(await f.Db.BrandDiscoveryJobs.Where(j => j.UserAccountId == f.First.Account).ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Completed_delivery_retry_preserves_later_edits_and_does_not_duplicate_data()
    {
        await using var f = await PersonalScope.CreateAsync();
        var first = await f.CaptureAsync(f.First);
        var job = f.LastJob!;
        var observation = f.LastObservation;
        await f.Db.ShoppingListProducts.Where(i => i.Id == first.ShoppingListProductId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Quantity, 7).SetProperty(i => i.Notes, "Keep this edit")
            .SetProperty(i => i.IsPurchased, true).SetProperty(i => i.IsHidden, true).SetProperty(i => i.PurchasedDate, f.Clock.Now.UtcDateTime));
        var repeated = await f.Service.SavePersonalAsync(job.Id, f.First.Account, job.ClaimToken!.Value,
            f.Source with { Offer = Offer(f.Source, 1m) }, default, observation);
        Assert.Equal(ProductPersistenceStatus.Success, repeated.Status);
        Assert.Equal(first.ShoppingListProductId, repeated.ShoppingListProductId);
        var item = await f.Db.ShoppingListProducts.SingleAsync();
        Assert.Equal(7, item.Quantity);
        Assert.Equal("Keep this edit", item.Notes);
        Assert.True(item.IsPurchased && item.IsHidden);
        Assert.NotNull(item.PurchasedDate);
        Assert.Single(await f.Db.Products.ToArrayAsync());
        Assert.Single(await f.Db.UserProductActivities.ToArrayAsync());
        Assert.Single(await f.Db.ShopProductPriceHistory.ToArrayAsync());
        Assert.Equal(4m, (await f.Db.ShopProductPrices.SingleAsync()).Price);
    }

    [PostgreSqlFact]
    public async Task Exact_match_without_barcode_reuses_only_its_owners_product_across_retailers()
    {
        await using var f = await PersonalScope.CreateAsync();
        var source = f.Source with { Identity = f.Source.Identity with { GTIN = null } };
        var first = await f.CaptureAsync(f.First, source);
        var other = source with { ShopCode = "woolworths", ProductUrl = new("https://www.woolworths.com.au/shop/productdetails/123456"), Offer = null };
        var sameAccount = await f.CaptureAsync(f.First, other);
        var otherAccount = await f.CaptureAsync(f.Second, other);
        Assert.Equal(ProductPersistenceStatus.Success, sameAccount.Status);
        Assert.Equal(first.ProductId, sameAccount.ProductId);
        Assert.Equal(first.ShoppingListProductId, sameAccount.ShoppingListProductId);
        Assert.NotEqual(first.ProductId, otherAccount.ProductId);
        Assert.Equal(2, await f.Db.ShopProducts.CountAsync(p => p.UserAccountId == f.First.Account));
    }

    [PostgreSqlFact]
    public async Task Concurrent_delivery_retries_create_one_product_item_and_observation_history()
    {
        await using var f = await PersonalScope.CreateAsync();
        var job = await f.JobAsync(f.First, f.Source);
        var observation = await f.ObserveAsync(job, f.First.Account);
        async Task<ProductPersistenceResult> Deliver()
        {
            await using var db = new MyShoppingListDbContext(new DbContextOptionsBuilder<MyShoppingListDbContext>()
                .UseNpgsql(f.Db.Database.GetConnectionString()).Options);
            return await PersistenceScope.BuildService(db, f.Clock).SavePersonalAsync(job.Id, f.First.Account,
                job.ClaimToken!.Value, f.Source, default, observation);
        }
        var results = await Task.WhenAll(Deliver(), Deliver());
        Assert.All(results, r => Assert.Equal(ProductPersistenceStatus.Success, r.Status));
        Assert.Equal(results[0].ShoppingListProductId, results[1].ShoppingListProductId);
        Assert.Single(await f.Db.Products.ToArrayAsync());
        Assert.Single(await f.Db.ShoppingListProducts.ToArrayAsync());
        Assert.Single(await f.Db.ShopProductPrices.ToArrayAsync());
        Assert.Single(await f.Db.ShopProductPriceHistory.ToArrayAsync());
        Assert.Single(await f.Db.UserProductActivities.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Missing_price_saves_product_without_inventing_a_price_and_invalid_offer_rolls_back()
    {
        await using var f = await PersonalScope.CreateAsync();
        var noPrice = await f.CaptureAsync(f.First, f.Source with { Offer = null });
        Assert.Equal(ProductPersistenceStatus.Success, noPrice.Status);
        Assert.False(noPrice.PriceSaved);
        Assert.Empty(await f.Db.ShopProductPrices.ToArrayAsync());
        var invalid = await f.CaptureAsync(f.Second, f.Source with { Offer = Offer(f.Source, -1m) });
        Assert.Equal(ProductPersistenceStatus.InvalidData, invalid.Status);
        Assert.False(await f.Db.Products.AnyAsync(p => p.UserAccountId == f.Second.Account));
        Assert.False(await f.Db.ShoppingListProducts.AnyAsync(i => i.ShoppingListId == f.Second.List));
    }

    [PostgreSqlFact]
    public async Task Personal_path_rejects_foreign_job_foreign_observation_changed_product_and_lost_claim()
    {
        await using var f = await PersonalScope.CreateAsync();
        var job = await f.JobAsync(f.First, f.Source);
        var observation = await f.ObserveAsync(job, f.First.Account);
        Assert.Equal(ProductPersistenceStatus.AccessDenied, (await f.Service.SavePersonalAsync(job.Id, f.Second.Account, job.ClaimToken!.Value, f.Source, default, observation)).Status);
        var foreign = await f.ObserveAsync(job, f.Second.Account);
        Assert.Equal("personal_observation_required", (await f.Service.SavePersonalAsync(job.Id, f.First.Account, job.ClaimToken!.Value, f.Source, default, foreign)).ErrorCode);
        Assert.Equal("personal_observation_required", (await f.Service.SavePersonalAsync(job.Id, f.First.Account, job.ClaimToken!.Value, f.Source, default)).ErrorCode);
        Assert.Equal("source_product_mismatch", (await f.Service.SavePersonalAsync(job.Id, f.First.Account, job.ClaimToken!.Value,
            f.Source with { ProductUrl = new("https://www.coles.com.au/product/999999") }, default, observation)).ErrorCode);
        Assert.Equal(ProductPersistenceStatus.LostClaim, (await f.Service.SavePersonalAsync(job.Id, f.First.Account, Guid.NewGuid(), f.Source, default, observation)).Status);
        Assert.Empty(await f.Db.Products.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Expired_trial_and_archived_list_cannot_capture()
    {
        await using var f = await PersonalScope.CreateAsync();
        await f.Db.UserAccounts.Where(a => a.Id == f.First.Account).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresDate, f.Clock.Now.UtcDateTime.AddSeconds(-1)));
        Assert.Equal(ProductPersistenceStatus.ExpiredAccount, (await f.CaptureAsync(f.First)).Status);
        await f.Db.ShoppingLists.Where(l => l.Id == f.Second.List).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        Assert.Equal(ProductPersistenceStatus.ListUnavailable, (await f.CaptureAsync(f.Second)).Status);
        Assert.Empty(await f.Db.Products.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Caches_and_mapping_lookups_never_fall_back_to_another_account_or_legacy()
    {
        await using var f = await PersonalScope.CreateAsync();
        var saved = await f.CaptureAsync(f.First);
        var cache = new SourceProductCacheService(f.Db, new(), f.Clock);
        Assert.Null(await cache.FindAsync("coles", f.Source.ProductUrl, default));
        Assert.Null(await cache.FindPersonalAsync(f.Second.Account, "coles", f.Source.ProductUrl, default));
        Assert.NotNull(await cache.FindPersonalAsync(f.First.Account, "coles", f.Source.ProductUrl, default));
        await using var transaction = await f.Db.Database.BeginTransactionAsync();
        var mappings = new ShopProductService(f.Db);
        Assert.Empty(await mappings.FindAsync(1, f.Source, default));
        Assert.Empty(await mappings.FindPersonalAsync(f.Second.Account, 1, f.Source, default));
        Assert.Equal(saved.ShopProductId, (await mappings.FindPersonalAsync(f.First.Account, 1, f.Source, default)).Single().Id);
        var normalisation = new ProductNormalisationService();
        var products = new ProductService(f.Db, normalisation, new(normalisation));
        var mapping = await f.Db.ShopProducts.SingleAsync();
        Assert.Equal(ProductPersistenceStatus.AccessDenied, (await products.ResolveAsync(f.Source, mapping, f.Clock.Now.UtcDateTime, default)).Status);
        Assert.Equal(ProductPersistenceStatus.AccessDenied, (await products.ResolvePersonalAsync(f.Second.Account, f.Source, mapping, f.Clock.Now.UtcDateTime, default)).Status);
    }

    [PostgreSqlFact]
    public async Task Private_prices_reject_foreign_provenance_shared_evidence_and_forged_mapping()
    {
        await using var f = await PersonalScope.CreateAsync();
        await f.CaptureAsync(f.First);
        var firstJob = f.LastJob!;
        var firstObservation = f.LastObservation;
        var otherObservation = await f.ObserveAsync(firstJob, f.Second.Account);
        var mapping = await f.Db.ShopProducts.AsNoTracking().SingleAsync();
        var offer = ((ProviderResult<ShopProductOffer>.Success)f.Source.Offer!).Value;
        await using var transaction = await f.Db.Database.BeginTransactionAsync();
        var service = f.Prices;
        Assert.Equal("observation_owner_mismatch", (await service.SavePersonalAsync(f.First.Account, mapping, offer, default, otherObservation)).ErrorCode);
        Assert.Equal("personal_observation_required", (await service.SavePersonalAsync(f.First.Account, mapping, offer, default)).ErrorCode);
        Assert.Equal("mapping_owner_mismatch", (await service.SavePersonalAsync(f.Second.Account, mapping, offer, default, firstObservation)).ErrorCode);
        Assert.Equal("mapping_owner_mismatch", (await service.SaveAsync(mapping, offer, default, firstObservation)).ErrorCode);
        Assert.Equal("personal_capture_required", (await service.SavePersonalAsync(f.First.Account, mapping, offer with { SourceType = SourceType.Playwright }, default, firstObservation)).ErrorCode);
        Assert.Equal("observation_product_mismatch", (await service.SavePersonalAsync(f.First.Account, mapping,
            offer with { SourceUrl = new("https://www.coles.com.au/product/999999") }, default, firstObservation)).ErrorCode);
        mapping.UserAccountId = f.Second.Account;
        Assert.Equal("mapping_owner_mismatch", (await service.SavePersonalAsync(f.Second.Account, mapping, offer, default, otherObservation)).ErrorCode);
        Assert.Equal(4m, (await f.Db.ShopProductPrices.SingleAsync()).Price);
    }

    private static ProviderResult<ShopProductOffer> Offer(ExtractedShopProduct source, decimal price, DateTimeOffset? date = null) =>
        new ProviderResult<ShopProductOffer>.Success(new()
        {
            ShopCode = source.ShopCode, SourceUrl = source.ProductUrl, Price = price, Currency = "AUD",
            CheckedDate = date ?? source.CheckedDate, PriceScope = PriceScope.Unknown, SourceType = SourceType.BrowserExtension
        });

    private sealed class PersonalScope : IAsyncDisposable
    {
        private readonly string original;
        private readonly string schema = "codex_personal_" + Guid.NewGuid().ToString("N");
        public MyShoppingListDbContext Db { get; }
        public PersistenceClock Clock { get; } = new() { Now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero) };
        public (long Account, long List) First { get; private set; }
        public (long Account, long List) Second { get; private set; }
        public ProductImportJob? LastJob { get; private set; }
        public long LastObservation { get; private set; }
        public ExtractedShopProduct Source { get; private set; } = null!;
        public SourceProductPersistenceService Service => PersistenceScope.BuildService(Db, Clock);
        public PriceService Prices => new(Db, Options.Create(new PriceOptions()), Clock, new(new RetailerCatalog()));
        private PersonalScope(string connection)
        {
            original = connection;
            var isolated = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, Pooling = false }.ConnectionString;
            Db = new(new DbContextOptionsBuilder<MyShoppingListDbContext>().UseNpgsql(isolated).Options);
        }
        public static async Task<PersonalScope> CreateAsync()
        {
            var f = new PersonalScope(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!);
            await f.SchemaCommand($"CREATE SCHEMA \"{f.schema}\"");
            try
            {
                await f.Db.Database.MigrateAsync();
                f.First = await f.AccountAsync(); f.Second = await f.AccountAsync();
                f.Source = new()
                {
                    ShopCode = "coles", ShopProductCode = "123456", ProductUrl = new("https://www.coles.com.au/product/123456"),
                    Identity = new() { Name = "Acme Full Cream Milk 1L", Brand = "Acme", Variant = "Full Cream", GTIN = "9300000000002", PackQuantity = 1, PackSize = 1, PackUnit = "L" },
                    SourceType = SourceType.BrowserExtension, CheckedDate = f.Clock.Now.AddSeconds(-1)
                };
                f.Source = f.Source with { Offer = Offer(f.Source, 4m) };
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private async Task<(long Account, long List)> AccountAsync()
        {
            var account = new UserAccount { DisplayName = "Personal capture test", IsTrial = true, ExpiresDate = Clock.Now.UtcDateTime.AddHours(3),
                IncludeDifferentBrands = true, CreatedDate = Clock.Now.UtcDateTime, UpdatedDate = Clock.Now.UtcDateTime };
            var list = new ShoppingList { UserAccount = account, Name = "Personal list", CreatedDate = Clock.Now.UtcDateTime, UpdatedDate = Clock.Now.UtcDateTime };
            Db.Add(list); await Db.SaveChangesAsync(); Db.ChangeTracker.Clear();
            return (account.Id, list.Id);
        }
        public async Task<ProductImportJob> JobAsync((long Account, long List) account, ExtractedShopProduct source)
        {
            var job = new ProductImportJob { UserAccountId = account.Account, ShoppingListId = account.List,
                SourceUrl = source.ProductUrl.AbsoluteUri, NormalisedSourceUrl = source.ProductUrl.AbsoluteUri,
                RequestedQuantity = 2, Status = ProductImportJobStatus.Processing, ClaimToken = Guid.NewGuid(),
                LeaseExpiresDate = Clock.Now.UtcDateTime.AddMinutes(10), CreatedDate = Clock.Now.UtcDateTime };
            Db.Add(job); await Db.SaveChangesAsync(); Db.ChangeTracker.Clear(); return job;
        }
        public async Task<long> ObserveAsync(ProductImportJob job, long account)
        {
            var observation = new ContributionObservation { UserAccountId = account, ImportJobId = job.Id, Source = "PersonalAdd",
                Url = job.SourceUrl, CollectedAt = Clock.Now.UtcDateTime, ReceivedAt = Clock.Now.UtcDateTime };
            Db.Add(observation); await Db.SaveChangesAsync(); Db.ChangeTracker.Clear(); return observation.Id;
        }
        public async Task<ProductPersistenceResult> CaptureAsync((long Account, long List) account, ExtractedShopProduct? source = null)
        {
            source ??= Source;
            LastJob = await JobAsync(account, source);
            LastObservation = await ObserveAsync(LastJob, account.Account);
            return await Service.SavePersonalAsync(LastJob.Id, account.Account, LastJob.ClaimToken!.Value, source, default, LastObservation);
        }
        private async Task SchemaCommand(string sql)
        {
            await using var connection = new NpgsqlConnection(original);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await SchemaCommand($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }
}
