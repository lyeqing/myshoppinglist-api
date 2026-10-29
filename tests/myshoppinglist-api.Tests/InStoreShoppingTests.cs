using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class InStoreShoppingTests
{
    [PostgreSqlFact]
    public async Task Private_aldi_observations_are_available_in_store_without_sharing_legacy_or_foreign_prices()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var db = new MyShoppingListDbContext(new DbContextOptionsBuilder<MyShoppingListDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!).Options);
        var clock = new PersistenceClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var first = new UserAccount
        {
            DisplayName = "First",
            Email = "first@example.test",
            PasswordHash = "fixture",
            PasswordSalt = "fixture",
            CreatedDate = now,
            UpdatedDate = now
        };
        var second = new UserAccount
        {
            DisplayName = "Second",
            Email = "second@example.test",
            PasswordHash = "fixture",
            PasswordSalt = "fixture",
            CreatedDate = now,
            UpdatedDate = now
        };
        db.AddRange(first, second);
        await db.SaveChangesAsync();
        var firstList = new ShoppingList { UserAccountId = first.Id, Name = "First list", CreatedDate = now, UpdatedDate = now };
        var secondList = new ShoppingList { UserAccountId = second.Id, Name = "Second list", CreatedDate = now, UpdatedDate = now };
        db.AddRange(firstList, secondList);
        await db.SaveChangesAsync();
        var aldi = await db.Shops.SingleAsync(s => s.Code == "aldi");
        async Task<ShopProductPrice> AddProduct(long? owner, ShoppingList list, string name, decimal amount)
        {
            var product = new Product { UserAccountId = owner, Name = name, CreatedDate = now, UpdatedDate = now };
            var mapping = new ShopProduct
            {
                UserAccountId = owner,
                Product = product,
                ShopId = aldi.Id,
                NameAtShop = name,
                ProductUrl = "https://www.aldi.com.au/product/test-000000000000398949",
                MatchType = MatchType.Exact,
                FirstFoundDate = now,
                LastFoundDate = now
            };
            var price = new ShopProductPrice
            {
                UserAccountId = owner,
                ShopProduct = mapping,
                Price = amount,
                CheckedDate = now,
                CreatedDate = now,
                UpdatedDate = now,
                SourceType = SourceType.BrowserExtension,
                SourceUrl = mapping.ProductUrl,
                PriceScope = PriceScope.Unknown
            };
            db.Add(price);
            db.Add(new ShoppingListProduct { ShoppingListId = list.Id, Product = product, Quantity = 2, AddedDate = now, UpdatedDate = now });
            await db.SaveChangesAsync();
            return price;
        }
        var own = await AddProduct(first.Id, firstList, "Own milk", 7.29m);
        var foreign = await AddProduct(second.Id, secondList, "Foreign milk", 1m);
        await AddProduct(null, firstList, "Legacy milk", 3m);
        db.Add(new ShoppingListProduct
        {
            ShoppingListId = firstList.Id,
            ProductId = foreign.ShopProduct.ProductId,
            Quantity = 1,
            AddedDate = now,
            UpdatedDate = now
        });
        db.Add(new UserPreferredShop { UserAccountId = first.Id, ShopId = aldi.Id, IsEnabled = true });
        await db.SaveChangesAsync();
        var service = new InStoreShoppingService(db, clock, new());
        foreach (var preferredOnly in new[] { false, true })
        {
            first.CompareOnlyPreferredShops = preferredOnly;
            await db.SaveChangesAsync();
            Assert.Equal(aldi.Id, Assert.Single(Assert.Single(await service.ListsAsync(first.Id, default)).Retailers).Id);
            var detail = (await service.DetailAsync(first.Id, firstList.Id, default))!;
            Assert.Equal(2, detail.Items.Length);
            Assert.DoesNotContain(detail.Items, i => i.Item.Product.Name == "Foreign milk");
            var price = Assert.Single(detail.Items.Single(i => i.Item.Product.Name == "Own milk").Prices);
            Assert.Equal(7.29m, price.Price);
            Assert.Equal("Fresh", price.Status);
            Assert.Null(price.RefreshStatus);
            Assert.Equal(14.58m, Assert.Single(detail.Baskets).Subtotal);
            Assert.All(detail.Items.Single(i => i.Item.Product.Name == "Legacy milk").Prices, p => Assert.Null(p.Price));
        }
        Assert.Null(await service.DetailAsync(second.Id, firstList.Id, default));
        Assert.Equal(1m, Assert.Single(Assert.Single((await service.DetailAsync(second.Id, secondList.Id, default))!.Items).Prices).Price);
        own.CheckedDate = now.AddDays(-8);
        await db.SaveChangesAsync();
        var stale = Assert.Single((await service.DetailAsync(first.Id, firstList.Id, default))!.Items.Single(i => i.Item.Product.Name == "Own milk").Prices);
        Assert.Null(stale.Price);
        Assert.Equal("Stale price", stale.Status);
        Assert.Equal(7.29m, stale.PreviousPrice);
        await db.UserPreferredShops.Where(p => p.UserAccountId == first.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, false));
        Assert.Empty(Assert.Single(await service.ListsAsync(first.Id, default)).Retailers);
        Assert.All((await service.DetailAsync(first.Id, firstList.Id, default))!.Items, i => Assert.Empty(i.Prices));
        await db.UserPreferredShops.Where(p => p.UserAccountId == first.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, true));
        own.ShopProduct.IsActive = false;
        await db.SaveChangesAsync();
        Assert.Empty(Assert.Single(await service.ListsAsync(first.Id, default)).Retailers);
    }

    [Theory]
    [InlineData(1, 30, 0, 1)]
    [InlineData(2, 30, 1, 0)]
    [InlineData(3, 60, 1, 1)]
    [InlineData(4, 60, 2, 0)]
    [InlineData(5, 90, 2, 1)]
    public void Verified_deals_apply_only_to_complete_same_product_bundles(int quantity, decimal total, int bundles, int remaining)
    {
        var result = PromotionCalculationService.Calculate(30, quantity, new(2, 30, 15, 30));
        Assert.Equal(total, result.Total);
        Assert.Equal(bundles, result.AppliedBundles);
        Assert.Equal(remaining, result.RemainingQuantity);
        Assert.Equal(30 * quantity - total, result.Savings);
        Assert.Equal(30 * quantity, PromotionCalculationService.Calculate(30, quantity, null).Total);
    }

    [Fact]
    public void Both_baskets_rank_quantity_cost_instead_of_single_price_and_do_not_pool_products()
    {
        var deal = new ShoppingListMultibuy(2, 30, 15, 30);
        var row = Row(1, 2, 30, 20);
        row = row with { Prices = [row.Prices[0] with { Multibuy = deal }, row.Prices[1]] };
        var store = InStoreShoppingService.Summarize(1, "List", Shops, [row]);
        Assert.Equal(30m, store.SplitSubtotal);
        Assert.Equal(10m, store.Baskets[1].SavingsBySplitting);
        var planning = new ShoppingListPlanningItem(row.Item,
            [new(1, "Coles", 30, true, "Fresh", null, null, null, Multibuy: deal), new(2, "Woolworths", 20, true, "Fresh", null, null, null)]);
        Assert.Equal(30m, ShoppingListService.SummarizePlan(1, "List", [planning], [(1, "Coles"), (2, "Woolworths")]).Lowest.Subtotal);
        var separate = new[] { row with { Item = row.Item with { Quantity = 1 } }, row with { Item = row.Item with { Id = 2, Quantity = 1 } } };
        Assert.Equal(60m, InStoreShoppingService.Summarize(1, "List", Shops, separate).Baskets[0].Subtotal);
    }

    [Fact]
    public void Bundle_arithmetic_preserves_decimal_totals_without_rounding_unit_prices()
    {
        var result = PromotionCalculationService.Calculate(4.99m, 7, new(3, 10, 10m / 3, 4.97m));
        Assert.Equal(24.99m, result.Total);
        Assert.Equal(9.94m, result.Savings);
        Assert.Equal(2, result.AppliedBundles);
    }

    [PostgreSqlFact]
    public async Task Completing_trial_list_removes_it_from_in_store_without_deleting_saved_items()
    {
        await using var f = await ListFixture.CreateAsync();
        var service = new InStoreShoppingService(f.Scope.Db, f.Scope.Clock, new CatalogueFreshnessService());
        Assert.Single(await service.ListsAsync(f.Scope.UserId, default));
        var item = await f.ItemAsync();
        var saved = await f.UpdateAsync(new(item.Quantity, item.Notes, true, false, item.UpdatedDate));
        Assert.True(saved.Item!.ListArchived);
        Assert.Empty(await service.ListsAsync(f.Scope.UserId, default));
        Assert.Null(await service.DetailAsync(f.Scope.UserId, f.Scope.ListId, default));
        Assert.True(await f.Scope.Db.ShoppingListProducts.AnyAsync(i => i.Id == item.Id));
    }
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

    [Fact]
    public async Task Parsed_Coles_single_price_remains_comparable_with_a_separate_multibuy()
    {
        var result = await new myshoppinglist_api.Providers.Coles.ColesProductParser().ParseAsync(
            new(ColesProductParserTests.Url, ColesProductParserTests.Fixture, ColesProductParserTests.Checked), default);
        var product = Assert.IsType<myshoppinglist_api.Providers.Models.ProviderResult<myshoppinglist_api.Providers.Models.ExtractedShopProduct>.Success>(result).Value;
        var offer = Assert.IsType<myshoppinglist_api.Providers.Models.ProviderResult<myshoppinglist_api.Providers.Models.ShopProductOffer>.Success>(product.Offer).Value;
        var price = new ShopProductPrice
        {
            Price = offer.Price,
            SourceType = offer.SourceType,
            SourceUrl = offer.SourceUrl.AbsoluteUri,
            SpecialType = offer.SpecialType,
            SpecialDescription = offer.SpecialDescription,
            CheckedDate = offer.CheckedDate.UtcDateTime
        };
        Assert.Equal("Fresh", InStoreShoppingService.PriceStatus(price, offer.CheckedDate, new()));
        var deal = Assert.IsType<ShoppingListMultibuy>(InStoreShoppingService.SinglePriceMultibuy(price));
        Assert.Equal(2, deal.Quantity); Assert.Equal(23m, deal.Total); Assert.Equal(11.5m, deal.UnitPrice);
    }

    [Fact]
    public void Multibuy_requires_verified_single_price_and_never_bypasses_freshness_or_membership()
    {
        var now = DateTimeOffset.UtcNow;
        var price = new ShopProductPrice
        {
            Price = 30,
            SourceType = SourceType.RetailerPage,
            SourceUrl = "https://www.coles.com.au/product/1115507",
            SpecialType = "MULTI_SAVE",
            SpecialDescription = "Pick any 2 for $30",
            CheckedDate = now.UtcDateTime
        };
        Assert.Equal(new ShoppingListMultibuy(2, 30, 15, 30), InStoreShoppingService.SinglePriceMultibuy(price));
        Assert.Equal("Fresh", InStoreShoppingService.PriceStatus(price, now, new()));
        price.CheckedDate = now.UtcDateTime.AddDays(-8);
        Assert.Equal("Stale price", InStoreShoppingService.PriceStatus(price, now, new()));
        price.CheckedDate = now.UtcDateTime; price.SpecialEndDate = now.UtcDateTime.AddMinutes(-1);
        Assert.Equal("Offer not current", InStoreShoppingService.PriceStatus(price, now, new()));
        price.SpecialEndDate = null;
        foreach (var text in new[] { "Members: Pick any 2 for $30", "Pick any 2 for $30 with loyalty card", "2 for $0", "0 for $30", "2 for $300", "2 for $30 and spend $100" })
        {
            price.SpecialDescription = text;
            Assert.Null(InStoreShoppingService.SinglePriceMultibuy(price));
            Assert.Equal("Conditional offer", InStoreShoppingService.PriceStatus(price, now, new()));
        }
        price.SpecialDescription = "Pick any 2 for $30"; price.SourceType = SourceType.Manual;
        Assert.Null(InStoreShoppingService.SinglePriceMultibuy(price));
        Assert.Equal("Conditional offer", InStoreShoppingService.PriceStatus(price, now, new()));
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
