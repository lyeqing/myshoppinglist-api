using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class InStoreShoppingTests
{
    private static readonly InStoreRetailer[] Shops = [new(1, "Coles"), new(2, "Woolworths")];
    private static InStoreItem Row(long id, int quantity, decimal? coles, decimal? woolworths, bool purchased = false, bool hidden = false) => new(
        new(id, 1, new(id, "Product", null, null, null, null, null, null), quantity, null, purchased, null, hidden, null, default, default),
        [new(1, "Coles", coles, coles.HasValue ? "Fresh" : "No price", null), new(2, "Woolworths", woolworths, woolworths.HasValue ? "Fresh" : "No price", null)]);

    [Fact]
    public void Basket_savings_compare_same_items_and_quantities_excluding_purchased_and_hidden()
    {
        var result = InStoreShoppingService.Summarize(1, "Weekly", Shops,
            [Row(1, 2, 3, 5), Row(2, 3, 6, 4), Row(3, 1, 8, null), Row(4, 1, null, null), Row(5, 9, 50, 60, true), Row(6, 9, 50, 60, hidden: true)]);
        Assert.Equal(4, result.RemainingCount);
        Assert.Equal(3, result.SplitPricedCount);
        Assert.Equal(26m, result.SplitSubtotal);
        Assert.Equal(2, result.ComparableCount);
        Assert.Equal(18m, result.ComparableSplitSubtotal);
        Assert.Equal(32m, result.Baskets[0].Subtotal);
        Assert.Equal(3, result.Baskets[0].PricedCount);
        Assert.Equal(6m, result.Baskets[0].SavingsBySplitting);
        Assert.Equal(4m, result.Baskets[1].SavingsBySplitting);
    }

    [Fact]
    public void Equal_free_and_single_retailer_prices_do_not_invent_savings()
    {
        var equal = InStoreShoppingService.Summarize(1, "List", Shops, [Row(1, 2, 0, 0)]);
        Assert.All(equal.Baskets, b => Assert.Equal(0m, b.SavingsBySplitting));
        var single = InStoreShoppingService.Summarize(1, "List", [Shops[0]], [Row(1, 2, 3, null)]);
        Assert.Equal(0, single.ComparableCount);
        Assert.Equal(6m, single.SplitSubtotal);
    }

    [Fact]
    public void Eligibility_uses_catalogue_week_and_rejects_conditional_or_unavailable_prices()
    {
        var now = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        var freshness = new CatalogueFreshnessService();
        var price = new ShopProductPrice { Price = 5, Currency = "AUD", CheckedDate = now.UtcDateTime.AddDays(-2), PriceScope = PriceScope.Unknown };
        string Status() => InStoreShoppingService.PriceStatus(price, now, freshness);
        Assert.Equal("Fresh", Status());
        price.CheckedDate = now.UtcDateTime.AddDays(-7); Assert.Equal("Stale price", Status());
        price.CheckedDate = now.UtcDateTime; price.InStock = false; Assert.Equal("Out of stock", Status());
        price.InStock = true; price.SpecialDescription = "2 for $8"; Assert.Equal("Conditional offer", Status());
        price.SpecialDescription = null; price.SpecialEndDate = now.UtcDateTime.AddMinutes(-1); Assert.Equal("Offer not current", Status());
        price.SpecialEndDate = null; price.Currency = "USD"; Assert.Equal("Not comparable", Status());
    }

    [PostgreSqlFact]
    public async Task Read_is_owned_excludes_hidden_and_reads_all_items_and_saved_prices()
    {
        await using var f = await ListFixture.CreateAsync();
        var service = new InStoreShoppingService(f.Scope.Db, f.Scope.Clock, new());
        await f.AddItemAsync(hidden: true);
        await f.AddItemAsync(purchased: true);
        Assert.Null(await service.DetailAsync(-1, f.Scope.ListId, default));
        Assert.Empty(await service.ListsAsync(-1, default));
        var list = Assert.Single(await service.ListsAsync(f.Scope.UserId, default));
        Assert.Equal(1, list.RemainingCount);
        var detail = await service.DetailAsync(f.Scope.UserId, f.Scope.ListId, default);
        Assert.NotNull(detail); Assert.Equal(2, detail.Items.Length);
        Assert.Equal(1, detail.RemainingCount);
        Assert.Contains(detail.Items, i => i.Prices.Any(p => p.Price.HasValue));
        await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        Assert.Null(await service.DetailAsync(f.Scope.UserId, f.Scope.ListId, default));
    }
}
