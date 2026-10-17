using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class PackComparisonTests
{
    private static ProductIdentity Identity(string name = "Acme Original Coffee 500g", decimal size = 500, string unit = "g", int? count = null) =>
        new() { Name = name, Brand = "Acme", PackSize = size, PackUnit = unit, PackQuantity = count };

    [Theory]
    [InlineData("Acme Original Coffee 500g", 500, "g", "Acme Original Coffee 1kg", 1, "kg")]
    [InlineData("Acme Original Milk 500ml", 500, "ml", "Acme Original Milk 1L", 1, "l")]
    public void Matches_only_same_product_with_convertible_different_total(string first, int sizeA, string unitA, string second, int sizeB, string unitB)
    {
        var a = Identity(first, sizeA, unitA); var b = Identity(second, sizeB, unitB);
        Assert.True(new ProductFamilyMatchingService().IsDifferentSize(a, b));
        Assert.True(new ProductFamilyMatchingService().IsDifferentSize(b, a));
        Assert.Equal(MatchType.NoMatch, new ProductMatchingService(new()).Match(a, b).Type);
    }

    [Theory]
    [InlineData("Acme Vanilla Coffee 1kg", "Acme", 1, "kg")]
    [InlineData("Other Original Coffee 1kg", "Other", 1, "kg")]
    [InlineData("Acme Original Coffee Decaf 1kg", "Acme", 1, "kg")]
    [InlineData("Acme Original Coffee Sugar Free 1kg", "Acme", 1, "kg")]
    [InlineData("Acme Original Coffee Beans 1kg", "Acme", 1, "kg")]
    [InlineData("Acme Original Coffee 1L", "Acme", 1, "l")]
    [InlineData("Acme Original Coffee 0.5kg", "Acme", 0.5, "kg")]
    [InlineData("Acme Original Coffee 2kg", "Acme", 1, "kg")]
    public void Rejects_variant_brand_format_unit_same_size_and_title_conflicts(string name, string brand, double size, string unit)
    {
        Assert.False(new ProductFamilyMatchingService().IsDifferentSize(Identity(), Identity(name, (decimal)size, unit) with { Brand = brand }));
    }

    [Fact]
    public void Missing_identity_evidence_and_reused_gtin_cannot_establish_a_family()
    {
        var matcher = new ProductFamilyMatchingService();
        var a = Identity(); var b = Identity("Acme Original Coffee 1kg", 1, "kg");
        Assert.False(matcher.IsDifferentSize(a with { Brand = null }, b));
        Assert.False(matcher.IsDifferentSize(a, b with { PackUnit = null }));
        Assert.False(matcher.IsDifferentSize(a, b with { PackSize = null }));
        Assert.False(matcher.IsDifferentSize(a, b with { Variant = "Original" }));
        Assert.False(matcher.IsDifferentSize(a, b with { ModelNumber = "concentrate" }));
        Assert.False(matcher.IsDifferentSize(a with { GTIN = "4006381333931" }, b with { GTIN = "4006381333931" }));
        Assert.True(matcher.IsDifferentSize(a with { GTIN = "4006381333931" }, b with { GTIN = "5012345678900" }));
        Assert.False(matcher.IsDifferentSize(Identity("Acme 2% Milk 500ml", 500, "ml"), Identity("Acme 3% Milk 1L", 1, "l")));
    }

    [Fact]
    public void Multipacks_require_explicit_per_unit_evidence_and_total_quantity_must_differ()
    {
        var packs = new PackComparisonService(); var matcher = new ProductFamilyMatchingService();
        var a = Identity("Acme Original Juice 4 × 250ml", 250, "ml", 4);
        var b = Identity("Acme Original Juice 6 x 250ml", 250, "ml", 6);
        Assert.Equal(1000, packs.Read(a)!.Total); Assert.Equal(1500, packs.Read(b)!.Total);
        Assert.True(matcher.IsDifferentSize(a, b));
        Assert.Null(packs.Read(a with { Name = "Acme Original Juice 4 pack 250ml" }));
        Assert.Null(packs.Read(a with { PackQuantity = null }));
        Assert.Null(packs.Read(a with { Name = "Acme Original Juice 4x250ml", PackQuantity = null }));
        Assert.Null(packs.Read(a with { Name = "Acme Original Juice 4x250ml + 4x250ml" }));
        Assert.Null(packs.Read(a with { PackQuantity = 6 }));
        Assert.False(matcher.IsDifferentSize(a, Identity("Acme Original Juice 1L", 1, "l")));
        Assert.Null(packs.Read(a with { PackQuantity = 0 }));
        Assert.Null(packs.Read(a with { PackSize = -1 }));
        Assert.Null(packs.Read(a with { Name = "Acme Original Juice 4 x 250ml 1L" }));
    }

    [Fact]
    public void Decimal_unit_prices_and_percentage_preserve_upfront_tradeoffs_and_zero_baseline()
    {
        var packs = new PackComparisonService();
        var small = packs.Read(Identity())!;
        var large = packs.Read(Identity("Acme Original Coffee 1kg", 1, "kg"))!;
        Assert.Equal(2, PackComparisonService.UnitPrice(10, small));
        Assert.Equal(1.3m, PackComparisonService.UnitPrice(13, large));
        Assert.Equal(-35, PackComparisonService.DifferencePercent(2, 1.3m));
        Assert.Null(PackComparisonService.DifferencePercent(0, 1));
        Assert.Null(PackComparisonService.DifferencePercent(0.000000000000001m, 100000000000000000000m));
        Assert.Equal(-100, PackComparisonService.DifferencePercent(2, 0));
        var enormous = packs.Read(Identity("Acme Original Coffee 1000kg", 1000, "kg"))!;
        Assert.Equal(2000, enormous.Total / small.Total); // Facts only; never an automatic recommendation.
    }

    private static async Task<(Product Original, Product Alternative)> SeedAsync(ListFixture f)
    {
        var item = await f.ItemAsync();
        var original = await f.Scope.Db.Products.SingleAsync(p => p.Id == item.Product.Id);
        original.Name = "Acme Original Coffee 500g"; original.Brand = "Acme"; original.Variant = null;
        original.PackQuantity = null; original.PackSize = 500; original.PackUnit = "g";
        var now = f.Scope.Clock.Now.UtcDateTime;
        var alternative = new Product { Name = "Acme Original Coffee 1kg", Brand = "Acme", PackSize = 1, PackUnit = "kg", CreatedDate = now, UpdatedDate = now };
        var mapping = new ShopProduct { Product = alternative, ShopId = 2, NameAtShop = alternative.Name, ProductUrl = "https://www.woolworths.com.au/shop/productdetails/123456", MatchType = MatchType.Exact, FirstFoundDate = now, LastFoundDate = now };
        f.Scope.Db.ShopProductPrices.Add(new() { ShopProduct = mapping, Price = 13, Currency = "AUD", CheckedDate = now, CreatedDate = now, UpdatedDate = now, InStock = true });
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == original.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 10m).SetProperty(p => p.CheckedDate, now));
        await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
        return (original, alternative);
    }

    [PostgreSqlFact]
    public async Task Both_views_return_fact_based_alternatives_without_changing_items_or_totals()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        var before = await f.ItemAsync();
        await SeedAsync(f);
        var plan = (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!;
        var row = Assert.Single(plan.Items); var alternative = Assert.Single(row.PackAlternatives!);
        Assert.Equal("SameProductDifferentSize", alternative.Relationship);
        Assert.Equal(2, alternative.QuantityRatio); Assert.Equal(1000, alternative.TotalQuantity);
        Assert.Equal(1.3m, alternative.Offer.UnitPrice); Assert.Equal(2, alternative.Original!.UnitPrice);
        Assert.Equal(-35, alternative.UnitPriceDifferencePercent); Assert.Equal(3, alternative.PackagePriceDifference);
        Assert.Equal(10 * before.Quantity, plan.Lowest.Subtotal);
        Assert.Equal(0, plan.Retailers.Single(b => b.ShopId == 2).Subtotal);
        var detail = (await new InStoreShoppingService(f.Scope.Db, f.Scope.Clock, new()).DetailAsync(f.Scope.UserId, f.Scope.ListId, default))!;
        Assert.Equal(alternative, Assert.Single(Assert.Single(detail.Items).PackAlternatives!));
        Assert.Equal(before.Quantity, detail.Items[0].Item.Quantity);
        Assert.Equal(before.Notes, detail.Items[0].Item.Notes);
        Assert.Equal(before.Product.Id, detail.Items[0].Item.Product.Id);
        Assert.Null(await f.Service.PlanAsync(-1, f.Scope.ListId, default));
        Assert.Null(await new InStoreShoppingService(f.Scope.Db, f.Scope.Clock, new()).DetailAsync(-1, f.Scope.ListId, default));
    }

    [PostgreSqlFact]
    public async Task Preferences_and_disabled_deleted_unverified_mappings_filter_alternatives()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        var (original, alternative) = await SeedAsync(f);
        var service = new PackAlternativeService(f.Scope.Db, f.Scope.Clock);
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IncludeDifferentPackSizes, false).SetProperty(a => a.IncludeDifferentBrands, true));
        Assert.Empty(await service.ReadAsync(f.Scope.UserId, [original], default));
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IncludeDifferentPackSizes, true).SetProperty(a => a.CompareOnlyPreferredShops, true));
        f.Scope.Db.UserPreferredShops.Add(new() { UserAccountId = f.Scope.UserId, ShopId = 1, IsEnabled = true });
        await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
        Assert.Empty((await service.ReadAsync(f.Scope.UserId, [original], default))[original.Id]);
        await f.Scope.Db.UserPreferredShops.Where(p => p.UserAccountId == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(p => p.ShopId, 2));
        Assert.Null(Assert.Single((await service.ReadAsync(f.Scope.UserId, [original], default))[original.Id]).Original);
        await f.Scope.Db.ShopProducts.Where(m => m.ProductId == alternative.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.MatchType, MatchType.Likely));
        Assert.Empty((await service.ReadAsync(f.Scope.UserId, [original], default))[original.Id]);
        await f.Scope.Db.ShopProducts.Where(m => m.ProductId == alternative.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.MatchType, MatchType.Exact).SetProperty(m => m.IsActive, false));
        Assert.Empty((await service.ReadAsync(f.Scope.UserId, [original], default))[original.Id]);
        await f.Scope.Db.Products.Where(p => p.Id == alternative.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDeleted, true));
        Assert.Empty(await service.ReadAsync(f.Scope.UserId, [original], default));
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresDate, f.Scope.Clock.Now.UtcDateTime.AddMinutes(-1)));
        Assert.Empty(await service.ReadAsync(f.Scope.UserId, [original], default));
    }

    [PostgreSqlFact]
    public async Task Stale_conditional_out_of_stock_future_and_missing_original_prices_never_claim_savings()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        var (original, alternative) = await SeedAsync(f);
        var service = new PackAlternativeService(f.Scope.Db, f.Scope.Clock);
        var prices = f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == alternative.Id);
        foreach (var condition in new[] { "stale", "conditional", "out_of_stock", "future" })
        {
            await prices.ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, condition == "stale" ? f.Scope.Clock.Now.UtcDateTime.AddDays(-8) : condition == "future" ? f.Scope.Clock.Now.UtcDateTime.AddDays(1) : f.Scope.Clock.Now.UtcDateTime)
                .SetProperty(p => p.InStock, condition != "out_of_stock").SetProperty(p => p.SpecialDescription, condition == "conditional" ? "Members only" : null));
            var found = Assert.Single((await service.ReadAsync(f.Scope.UserId, [original], default))[original.Id]);
            Assert.NotEqual("Fresh", found.Offer.Status); Assert.Null(found.UnitPriceDifferencePercent); Assert.Null(found.PackagePriceDifference);
        }
        await prices.ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime).SetProperty(p => p.SpecialDescription, (string?)null).SetProperty(p => p.Currency, "USD"));
        Assert.Empty((await service.ReadAsync(f.Scope.UserId, [original], default))[original.Id]);
        await prices.ExecuteUpdateAsync(s => s.SetProperty(p => p.Currency, "AUD"));
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == original.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddDays(-8)));
        var noBaseline = Assert.Single((await service.ReadAsync(f.Scope.UserId, [original], default))[original.Id]);
        Assert.Null(noBaseline.Original); Assert.Null(noBaseline.UnitPriceDifferencePercent); Assert.Null(noBaseline.PackagePriceDifference);
    }
}
