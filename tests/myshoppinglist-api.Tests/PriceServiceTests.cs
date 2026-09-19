using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Tests;

public class PriceServiceTests
{
    [PostgreSqlFact]
    public async Task Changes_and_periodic_samples_record_history_without_duplicate_refresh_rows()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var saved = await scope.SaveAsync();
        Assert.Equal(ProductPersistenceStatus.Success, saved.Status);
        var mapping = await scope.Db.ShopProducts.SingleAsync(p => p.Id == saved.ShopProductId);
        var service = Service(scope);
        var first = scope.Offer;
        scope.Clock.Now += TimeSpan.FromMinutes(2);
        var fresh = first with { CheckedDate = scope.Clock.Now };
        var unchanged = await service.SaveAsync(mapping, fresh, CancellationToken.None);
        Assert.False(unchanged.HistoryCreated);
        scope.Clock.Now += TimeSpan.FromMinutes(1);
        var changed = fresh with { Price = 20, CheckedDate = scope.Clock.Now };
        Assert.True((await service.SaveAsync(mapping, changed, CancellationToken.None)).HistoryCreated);
        scope.Clock.Now += TimeSpan.FromMinutes(1);
        var promotion = changed with { SpecialDescription = "Two for $35", CheckedDate = scope.Clock.Now };
        Assert.True((await service.SaveAsync(mapping, promotion, CancellationToken.None)).HistoryCreated);
        scope.Clock.Now += TimeSpan.FromHours(24);
        Assert.True((await service.SaveAsync(mapping, promotion with { CheckedDate = scope.Clock.Now }, CancellationToken.None)).HistoryCreated);
        Assert.Equal(4, await scope.Db.ShopProductPriceHistory.CountAsync(p => p.ShopProductId == mapping.Id));
    }

    [PostgreSqlFact]
    public async Task Older_or_equal_timestamp_conflicts_cannot_replace_current_price()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var saved = await scope.SaveAsync();
        var mapping = await scope.Db.ShopProducts.SingleAsync(p => p.Id == saved.ShopProductId);
        var service = Service(scope);
        Assert.Equal(PriceUpdateStatus.OlderObservation, (await service.SaveAsync(mapping,
            scope.Offer with { Price = 1, CheckedDate = scope.Offer.CheckedDate.AddDays(-1) }, CancellationToken.None)).Status);
        Assert.Equal(PriceUpdateStatus.InvalidOffer, (await service.SaveAsync(mapping,
            scope.Offer with { Price = 1 }, CancellationToken.None)).Status);
        Assert.Equal(23m, (await scope.Db.ShopProductPrices.SingleAsync(p => p.ShopProductId == mapping.Id)).Price);
        Assert.Single(await scope.Db.ShopProductPriceHistory.Where(p => p.ShopProductId == mapping.Id).ToListAsync());
    }

    [PostgreSqlFact]
    public async Task Price_scope_and_currency_have_separate_current_rows()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var saved = await scope.SaveAsync();
        var mapping = await scope.Db.ShopProducts.SingleAsync(p => p.Id == saved.ShopProductId);
        var service = Service(scope);
        Assert.Equal(PriceUpdateStatus.Saved, (await service.SaveAsync(mapping, scope.Offer with { PriceScope = PriceScope.Online }, CancellationToken.None)).Status);
        Assert.Equal(PriceUpdateStatus.Saved, (await service.SaveAsync(mapping, scope.Offer with { Currency = "NZD" }, CancellationToken.None)).Status);
        Assert.Equal(3, await scope.Db.ShopProductPrices.CountAsync(p => p.ShopProductId == mapping.Id));
    }

    [PostgreSqlFact]
    public async Task Location_must_belong_to_the_same_retailer()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var saved = await scope.SaveAsync();
        var mapping = await scope.Db.ShopProducts.SingleAsync(p => p.Id == saved.ShopProductId);
        var location = new ShopLocation { ShopId = 2, Name = "Other retailer", StoreCode = scope.Code };
        scope.Db.Add(location); await scope.Db.SaveChangesAsync();
        var offer = scope.Offer with { PriceScope = PriceScope.StoreSpecific, Location = new() { ShopCode = "coles", ShopLocationId = location.Id } };
        Assert.Equal(PriceUpdateStatus.InvalidOffer, (await Service(scope).SaveAsync(mapping, offer, CancellationToken.None)).Status);
        Assert.Single(await scope.Db.ShopProductPrices.Where(p => p.ShopProductId == mapping.Id).ToListAsync());
    }

    [PostgreSqlFact]
    public async Task Unknown_location_does_not_become_store_specific()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var saved = await scope.SaveAsync();
        var mapping = await scope.Db.ShopProducts.SingleAsync(p => p.Id == saved.ShopProductId);
        Assert.Equal(PriceUpdateStatus.InvalidOffer, (await Service(scope).SaveAsync(mapping,
            scope.Offer with { PriceScope = PriceScope.StoreSpecific }, CancellationToken.None)).Status);
    }

    [PostgreSqlFact]
    public async Task Promotion_timestamps_at_submicrosecond_precision_do_not_duplicate_history()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var precise = new DateTimeOffset(scope.Clock.Now.UtcTicks / 10 * 10 + 7, TimeSpan.Zero);
        var offer = scope.Offer with { SpecialStartDate = precise.AddDays(-1), SpecialEndDate = precise.AddDays(1) };
        var saved = await scope.SaveAsync(source: scope.Source with { Offer = new ProviderResult<ShopProductOffer>.Success(offer) });
        Assert.Equal(ProductPersistenceStatus.Success, saved.Status);
        var mapping = await scope.Db.ShopProducts.SingleAsync(p => p.Id == saved.ShopProductId);
        var refreshed = await Service(scope).SaveAsync(mapping, offer, CancellationToken.None);
        Assert.Equal(PriceUpdateStatus.Unchanged, refreshed.Status);
        Assert.False(refreshed.HistoryCreated);
    }

    private static PriceService Service(PersistenceScope scope) => new(scope.Db, Options.Create(new PriceOptions()), scope.Clock,
        new ProductUrlValidator(new RetailerCatalog()));
}
