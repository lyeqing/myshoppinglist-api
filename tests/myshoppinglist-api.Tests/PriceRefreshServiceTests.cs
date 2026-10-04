using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class PriceRefreshServiceTests
{
    private static PriceRefreshService Service(ListFixture f) => new(f.Scope.Db, f.Scope.Clock, new(), Options.Create(new PriceRefreshOptions()));

    private static async Task<string> StaleAsync(ListFixture f)
    {
        var url = "https://www.coles.com.au/product/" + Random.Shared.NextInt64(100000000, 900000000);
        await f.Scope.Db.ShopProducts.Where(m => m.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProductUrl, url));
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddDays(-8)));
        return ColesExtensionTaskService.ProductKey(new(url));
    }

    [PostgreSqlFact]
    public async Task Refresh_deduplicates_prioritizes_preserves_old_price_and_respects_failure_cooldown()
    {
        await using var f = await ListFixture.CreateAsync();
        var key = await StaleAsync(f);
        try
        {
            Assert.Null(await Service(f).RequestAsync(-1, f.Scope.ListId, default));
            Assert.Equal(1, await Service(f).ScanAsync(default));
            var task = await f.Scope.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Key == key);
            Assert.Equal(0, task.Priority);
            Assert.Equal(0, await Service(f).RequestAsync(f.Scope.UserId, f.Scope.ListId, default));
            task = await f.Scope.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Key == key);
            Assert.Equal(2, task.Priority);
            var plan = (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!;
            var price = Assert.Single(Assert.Single(plan.Items).Prices);
            Assert.Equal("Waiting", price.RefreshStatus);
            Assert.Equal(23m, price.Price);
            Assert.False(price.IncludedInTotal);
            await f.Scope.Db.ColesExtensionTasks.Where(t => t.Key == key).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Failed").SetProperty(t => t.Attempts, 3)
                .SetProperty(t => t.CompletedAt, f.Scope.Clock.Now.UtcDateTime));
            Assert.Equal(0, await Service(f).RequestAsync(f.Scope.UserId, f.Scope.ListId, default));
            Assert.Equal("RetryLater", (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!.Items[0].Prices[0].RefreshStatus);
            await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresDate, f.Scope.Clock.Now.UtcDateTime.AddDays(2)));
            f.Scope.Clock.Now += TimeSpan.FromHours(7);
            Assert.Equal(1, await Service(f).ScanAsync(default));
            task = await f.Scope.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Key == key);
            Assert.Equal("Waiting", task.Status);
            Assert.Equal(0, task.Attempts);
            await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime).SetProperty(p => p.Price, 19m));
            await f.Scope.Db.ColesExtensionTasks.Where(t => t.Key == key).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Completed"));
            plan = (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!;
            Assert.Null(plan.Items[0].Prices[0].RefreshStatus);
            Assert.Equal(38m, plan.Lowest.Subtotal);
            Assert.Equal(0, await Service(f).ScanAsync(default));
        }
        finally { await f.Scope.Db.ColesExtensionTasks.Where(t => t.Key == key).ExecuteDeleteAsync(); }
    }

    [PostgreSqlFact]
    public async Task Only_outstanding_items_in_active_unexpired_lists_and_accounts_are_eligible()
    {
        await using var f = await ListFixture.CreateAsync();
        var key = await StaleAsync(f);
        try
        {
            await f.Scope.Db.ShoppingListProducts.Where(i => i.Id == f.ItemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsPurchased, true));
            Assert.Equal(0, await Service(f).ScanAsync(default));
            await f.Scope.Db.ShoppingListProducts.Where(i => i.Id == f.ItemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsPurchased, false).SetProperty(i => i.IsHidden, true));
            Assert.Equal(0, await Service(f).ScanAsync(default));
            await f.Scope.Db.ShoppingListProducts.Where(i => i.Id == f.ItemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsHidden, false));
            await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
            Assert.Equal(0, await Service(f).ScanAsync(default));
            await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, false).SetProperty(l => l.ExpiresDate, f.Scope.Clock.Now.UtcDateTime.AddMinutes(-1)));
            Assert.Equal(0, await Service(f).ScanAsync(default));
            await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresDate, (DateTime?)null));
            await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
            Assert.Equal(0, await Service(f).ScanAsync(default));
            await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, true).SetProperty(a => a.ExpiresDate, f.Scope.Clock.Now.UtcDateTime.AddMinutes(-1)));
            Assert.Equal(0, await Service(f).ScanAsync(default));
            await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresDate, f.Scope.Clock.Now.UtcDateTime.AddDays(2)));
            Assert.Equal(1, await Service(f).ScanAsync(default));
        }
        finally { await f.Scope.Db.ColesExtensionTasks.Where(t => t.Key == key).ExecuteDeleteAsync(); }
    }

    [PostgreSqlFact]
    public async Task Shared_product_across_customers_queues_once_and_supports_woolworths()
    {
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        var key = await StaleAsync(f);
        var woolUrl = "https://www.woolworths.com.au/shop/productdetails/" + Random.Shared.NextInt64(100000000, 900000000);
        var woolKey = ColesExtensionTaskService.ProductKey(new(woolUrl));
        try
        {
            var item = await f.ItemAsync();
            f.Scope.Db.ShoppingListProducts.Add(new()
            {
                ShoppingListId = other.Scope.ListId,
                ProductId = item.Product.Id,
                Quantity = 1,
                AddedDate = f.Scope.Clock.Now.UtcDateTime,
                UpdatedDate = f.Scope.Clock.Now.UtcDateTime
            });
            await f.Scope.Db.SaveChangesAsync();
            f.Scope.Db.ChangeTracker.Clear();
            Assert.Equal(1, await Service(f).ScanAsync(default));
            Assert.Equal(0, await Service(other).RequestAsync(other.Scope.UserId, other.Scope.ListId, default));
            Assert.Equal(1, await f.Scope.Db.ColesExtensionTasks.CountAsync(t => t.Key == key));
            f.Scope.Db.ShopProducts.Add(new()
            {
                ProductId = item.Product.Id,
                ShopId = 2,
                ShopProductCode = f.Scope.Code,
                ProductUrl = woolUrl,
                NameAtShop = item.Product.Name,
                MatchType = myshoppinglist_api.Models.MatchType.Exact,
                FirstFoundDate = f.Scope.Clock.Now.UtcDateTime.AddDays(-8),
                LastFoundDate = f.Scope.Clock.Now.UtcDateTime.AddDays(-8)
            });
            await f.Scope.Db.SaveChangesAsync();
            f.Scope.Db.ChangeTracker.Clear();
            Assert.Equal(1, await Service(f).ScanAsync(default));
            Assert.Equal(woolUrl, (await f.Scope.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Key == woolKey)).Url);
        }
        finally { await f.Scope.Db.ColesExtensionTasks.Where(t => t.Key == key || t.Key == woolKey).ExecuteDeleteAsync(); }
    }

    [PostgreSqlFact]
    public async Task Fresh_prices_are_skipped_but_expired_promotions_refresh_and_untrusted_urls_are_rejected()
    {
        await using var f = await ListFixture.CreateAsync();
        var key = await StaleAsync(f);
        try
        {
            await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime));
            Assert.Equal(0, await Service(f).ScanAsync(default));
            await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.SpecialEndDate, f.Scope.Clock.Now.UtcDateTime.AddMinutes(-1)));
            Assert.Equal(1, await Service(f).ScanAsync(default));
            await f.Scope.Db.ColesExtensionTasks.Where(t => t.Key == key).ExecuteDeleteAsync();
            await f.Scope.Db.ShopProducts.Where(m => m.ShopProductCode == f.Scope.Code)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProductUrl, "https://evil.example/product/123456"));
            Assert.Equal(0, await Service(f).ScanAsync(default));
        }
        finally { await f.Scope.Db.ColesExtensionTasks.Where(t => t.Key == key).ExecuteDeleteAsync(); }
    }
}
