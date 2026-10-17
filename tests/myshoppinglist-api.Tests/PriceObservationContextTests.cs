using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class PriceObservationContextTests
{
    [Theory]
    [InlineData(PriceScope.Unknown, true)]
    [InlineData(PriceScope.Online, true)]
    [InlineData(PriceScope.National, true)]
    [InlineData(PriceScope.InStore, false)]
    [InlineData(PriceScope.Catalogue, false)]
    [InlineData(PriceScope.StoreSpecific, false)]
    [InlineData((PriceScope)999, false)]
    public void General_prices_require_eligible_coverage_and_no_branch(PriceScope scope, bool eligible)
    {
        var now = DateTimeOffset.UtcNow;
        var price = new ShopProductPrice { Price = 10, CheckedDate = now.UtcDateTime, PriceScope = scope };
        Assert.Equal(eligible ? "Fresh" : "Not comparable", InStoreShoppingService.PriceStatus(price, now, new()));
        price.ShopLocationId = 42;
        Assert.Equal("Not comparable", InStoreShoppingService.PriceStatus(price, now, new()));
    }

    [Theory]
    [InlineData("https://www.coles.com.au/product/coffee-123?token=private&userId=42#secret", "coles", "https://www.coles.com.au/product/coffee-123")]
    [InlineData("https://www.woolworths.com.au/shop/productdetails/123456/coffee?session=private", "woolworths", "https://www.woolworths.com.au/shop/productdetails/123456/coffee")]
    [InlineData("https://user:secret@www.coles.com.au/product/coffee", "coles", null)]
    [InlineData("https://@www.coles.com.au/product/coffee", "coles", null)]
    [InlineData("http://www.coles.com.au/product/coffee", "coles", null)]
    [InlineData("https://www.coles.com.au:8443/product/coffee", "coles", null)]
    [InlineData("https://www.coles.com.au.evil.test/product/coffee", "coles", null)]
    [InlineData("https://127.0.0.1/product/coffee", "coles", null)]
    [InlineData("https://www.coles.com.au/account/private-customer", "coles", null)]
    [InlineData("https://www.coles.com.au/api/products/123", "coles", null)]
    [InlineData("https://www.coles.com.au/product/coffee%2Fprivate", "coles", null)]
    [InlineData("https://www.coles.com.au/product/coffee", "woolworths", null)]
    [InlineData("not a url", "coles", null)]
    [InlineData("", "coles", null)]
    public void Source_links_keep_only_safe_public_product_routes(string url, string shopCode, string? expected)
    {
        var price = new ShopProductPrice { SourceUrl = url, SourceType = SourceType.BrowserExtension, PriceScope = PriceScope.Unknown };
        var context = PriceObservationContextService.From(price, shopCode)!;
        Assert.Equal(expected, context.SourceUrl);
        Assert.Equal(SourceType.BrowserExtension, context.SourceType);
        Assert.Equal(PriceScope.Unknown, context.PriceScope);
    }

    [Fact]
    public void Missing_and_unknown_metadata_remain_honest_and_serialization_excludes_private_fields()
    {
        Assert.Null(PriceObservationContextService.From(null, "coles"));
        var price = new ShopProductPrice { Id = 123, ShopLocationId = 456, ContributionObservationId = 789,
            SourceType = (SourceType)999, PriceScope = (PriceScope)999, SourceUrl = "https://www.coles.com.au/account/secret" };
        var context = PriceObservationContextService.From(price, "coles")!;
        Assert.Equal(SourceType.Other, context.SourceType); Assert.Equal(PriceScope.Unknown, context.PriceScope);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(context, options));
        Assert.Equal(new[] { "sourceType", "priceScope", "sourceUrl" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Unknown", json.RootElement.GetProperty("priceScope").GetString());
        Assert.Equal("Other", json.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sourceUrl").ValueKind);
        Assert.Null(new PackComparisonPrice(1, "Coles", null, null, "No price", null, null).Observation);
        Assert.Null(JsonSerializer.Deserialize<InStorePrice>("{\"shopId\":1,\"shopName\":\"Coles\",\"status\":\"No price\"}", options)!.Observation);
    }

    [PostgreSqlFact]
    public async Task Both_views_and_alternatives_preserve_observation_provenance_and_apply_the_same_scope_rules()
    {
        await using var isolation = await IsolatedDatabaseScope.CreateAsync();
        await using var db = PersistenceScope.Context();
        var clock = new PersistenceClock { Now = DateTimeOffset.UtcNow };
        var now = clock.Now.UtcDateTime;
        var account = AdminAccountTests.TestAccount(); account.IncludeDifferentBrands = true;
        Product Product(string brand, decimal size) => new() { Name = $"{brand} Original Coffee {size}g", Brand = brand,
            Category = "Drinks", SubCategory = "Ground coffee", PackSize = size, PackUnit = "g", CreatedDate = now, UpdatedDate = now };
        var original = Product("Acme", 500); var pack = Product("Acme", 1000); var brand = Product("Other", 1000);
        var list = new ShoppingList { UserAccount = account, Name = "Provenance", CreatedDate = now, UpdatedDate = now };
        db.ShoppingListProducts.Add(new() { ShoppingList = list, Product = original, Quantity = 2, AddedDate = now, UpdatedDate = now });
        ShopProduct Mapping(Product product, long shopId) => new() { Product = product, ShopId = shopId, NameAtShop = product.Name,
            ProductUrl = shopId == 1 ? "https://www.coles.com.au/product/mapping-link" : "https://www.woolworths.com.au/shop/productdetails/111111",
            MatchType = MatchType.Exact, FirstFoundDate = now, LastFoundDate = now };
        var originalMapping = Mapping(original, 1); var packMapping = Mapping(pack, 2); var brandMapping = Mapping(brand, 2);
        ShopProductPrice Observation(ShopProduct mapping, decimal amount, string url) => new() { ShopProduct = mapping,
            Price = amount, Currency = "AUD", PriceScope = PriceScope.National, SourceType = SourceType.RetailerPage,
            SourceUrl = url, CheckedDate = now, CreatedDate = now, UpdatedDate = now };
        db.ShopProductPrices.AddRange(Observation(originalMapping, 10, "https://www.coles.com.au/product/observed-coffee?token=private"),
            Observation(packMapping, 13, "https://www.woolworths.com.au/shop/productdetails/222222"),
            Observation(brandMapping, 14, "https://www.woolworths.com.au/shop/productdetails/333333"));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var reviews = new BrandAlternativeService(db, clock, Options.Create(new AdminOptions { AccountIds = [account.Id] }));
        var reviewContext = await reviews.ContextAsync(account.Id, original.Id, brand.Id, default);
        await reviews.ReviewAsync(account.Id, original.Id, brand.Id, new(true,
            "Checked intended use, format, composition and dietary claims against manufacturer details.", 0, true, true,
            reviewContext.Product.Fingerprint, reviewContext.AlternativeProduct.Fingerprint), default);
        var listService = new ShoppingListService(db, clock);
        var storeService = new InStoreShoppingService(db, clock, new());
        foreach (var scope in new[] { PriceScope.Unknown, PriceScope.Online, PriceScope.National, PriceScope.InStore, PriceScope.Catalogue, (PriceScope)999 })
        {
            await db.ShopProductPrices.ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceScope, scope));
            var plan = (await listService.PlanAsync(account.Id, list.Id, default))!;
            var detail = (await storeService.DetailAsync(account.Id, list.Id, default))!;
            var row = Assert.Single(plan.Items); var storeRow = Assert.Single(detail.Items);
            var price = Assert.Single(row.Prices); var storePrice = Assert.Single(storeRow.Prices);
            var eligible = scope is PriceScope.Unknown or PriceScope.Online or PriceScope.National;
            Assert.Equal(eligible, price.IncludedInTotal);
            Assert.Equal(eligible ? "Fresh" : "Not comparable", price.Status);
            Assert.Equal(price.Status, storePrice.Status);
            Assert.Equal(eligible ? 20 : 0, plan.Lowest.Subtotal);
            Assert.Equal(eligible ? 20 : 0, detail.SplitSubtotal);
            var expected = new PriceObservationContext(SourceType.RetailerPage, Enum.IsDefined(scope) ? scope : PriceScope.Unknown,
                "https://www.coles.com.au/product/observed-coffee");
            Assert.Equal(expected, price.Observation); Assert.Equal(expected, storePrice.Observation);
            Assert.NotEqual(price.ProductUrl, price.Observation!.SourceUrl);
            var packOffer = Assert.Single(row.PackAlternatives!); var brandOffer = Assert.Single(row.BrandAlternatives!);
            foreach (var offer in new[] { packOffer.Offer, brandOffer.Offer })
            {
                Assert.Equal(price.Status, offer.Status);
                Assert.Equal(expected.PriceScope, offer.Observation!.PriceScope);
                Assert.StartsWith("https://www.woolworths.com.au/shop/productdetails/", offer.Observation.SourceUrl);
                Assert.NotEqual(offer.ProductUrl, offer.Observation.SourceUrl);
            }
            Assert.Equal(packOffer, Assert.Single(storeRow.PackAlternatives!));
            Assert.Equal(brandOffer, Assert.Single(storeRow.BrandAlternatives!));
            Assert.Equal(eligible ? expected : null, packOffer.Original?.Observation);
            Assert.Equal(eligible ? expected : null, brandOffer.Original?.Observation);
            Assert.Equal(eligible, packOffer.UnitPriceDifferencePercent.HasValue);
            Assert.Equal(eligible, brandOffer.UnitPriceDifferencePercent.HasValue);
        }

        // A newer observation wins together with its metadata; an older eligible
        // price must never be substituted merely to make totals or savings work.
        await db.ShopProductPrices.ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceScope, PriceScope.Online)
            .SetProperty(p => p.CheckedDate, now.AddMinutes(-1)));
        var newer = Observation(originalMapping, 17, "https://www.coles.com.au/product/latest-catalogue");
        newer.ShopProduct = null!; newer.ShopProductId = originalMapping.Id;
        newer.PriceScope = PriceScope.Catalogue; newer.SourceType = SourceType.Manual;
        db.ShopProductPrices.Add(newer); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var latest = (await listService.PlanAsync(account.Id, list.Id, default))!;
        var latestPrice = Assert.Single(Assert.Single(latest.Items).Prices);
        Assert.Equal(17, latestPrice.Price); Assert.False(latestPrice.IncludedInTotal);
        Assert.Equal(new PriceObservationContext(SourceType.Manual, PriceScope.Catalogue,
            "https://www.coles.com.au/product/latest-catalogue"), latestPrice.Observation);
        Assert.Null(Assert.Single(latest.Items[0].PackAlternatives!).Original);
        Assert.Null(Assert.Single(latest.Items[0].BrandAlternatives!).Original);

        var location = new ShopLocation { ShopId = 1, Name = "Branch", CreatedDate = now, UpdatedDate = now };
        db.ShopLocations.Add(location); await db.SaveChangesAsync();
        var branchPrice = Observation(originalMapping, 1, "https://www.coles.com.au/product/branch-only");
        branchPrice.ShopProduct = null!; branchPrice.ShopProductId = originalMapping.Id;
        branchPrice.ShopLocationId = location.Id; branchPrice.PriceScope = PriceScope.StoreSpecific;
        db.ShopProductPrices.Add(branchPrice); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(latestPrice, Assert.Single(Assert.Single((await listService.PlanAsync(account.Id, list.Id, default))!.Items).Prices));
        await db.ShopProductPrices.Where(p => p.ShopLocationId == null).ExecuteDeleteAsync();
        var missing = Assert.Single((await listService.PlanAsync(account.Id, list.Id, default))!.Items);
        Assert.Null(Assert.Single(missing.Prices).Observation);
        Assert.Empty(missing.PackAlternatives!); Assert.Empty(missing.BrandAlternatives!);
        Assert.Null(await listService.PlanAsync(-1, list.Id, default));
        Assert.Null(await storeService.DetailAsync(-1, list.Id, default));
    }
}
