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
        await using var database = await IsolatedDatabaseScope.CreateAsync();
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
            var detail = await new InStoreShoppingService(f.Scope.Db, f.Scope.Clock, new()).DetailAsync(f.Scope.UserId, f.Scope.ListId, default);
            Assert.Null(detail!.Items[0].Prices[0].Price);
            Assert.Equal(23m, detail.Items[0].Prices[0].PreviousPrice);
            Assert.Equal(0, detail.SplitPricedCount);
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
        await using var database = await IsolatedDatabaseScope.CreateAsync();
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
        await using var database = await IsolatedDatabaseScope.CreateAsync();
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
        await using var database = await IsolatedDatabaseScope.CreateAsync();
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
    [PostgreSqlFact]
    public async Task Manual_refresh_checks_yesterday_even_when_weekly_cache_is_fresh_and_reports_running()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        var key = await StaleAsync(f);
        f.Scope.Clock.Now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresDate, f.Scope.Clock.Now.UtcDateTime.AddDays(2)));
        await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresDate, (DateTime?)null));
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddDays(-1)));
        Assert.Equal(0, await Service(f).ScanAsync(default));
        var result = (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "Australia/Adelaide", default))!;
        Assert.Equal(1, result.Queued);
        Assert.Equal(1, (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "Australia/Adelaide", default))!.AlreadyRunning);
        var price = Assert.Single((await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!.Items[0].Prices);
        Assert.True(price.IncludedInTotal);
        Assert.Equal("Waiting", price.RefreshStatus);
        var inStore = await new InStoreShoppingService(f.Scope.Db, f.Scope.Clock, new()).DetailAsync(f.Scope.UserId, f.Scope.ListId, default);
        Assert.Equal("Waiting", inStore!.Items[0].Prices[0].RefreshStatus);
        f.Scope.Db.RetailerWorkloadStates.Add(new() { Retailer = "coles", PausedUntil = f.Scope.Clock.Now.UtcDateTime.AddMinutes(15) });
        await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
        Assert.Equal("Paused", (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!.Items[0].Prices[0].RefreshStatus);
        await f.Scope.Db.ColesExtensionTasks.Where(t => t.Key == key).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "Failed").SetProperty(t => t.CompletedAt, f.Scope.Clock.Now.UtcDateTime));
        Assert.Equal(1, (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "Australia/Adelaide", default))!.Deferred);
        Assert.Equal(23m, (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!.Items[0].Prices[0].Price);
    }

    [PostgreSqlFact]
    public async Task Manual_refresh_uses_local_midnight_and_rejects_other_users_items()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        var key = await StaleAsync(f);
        f.Scope.Clock.Now = new DateTimeOffset(2026, 10, 15, 13, 31, 0, TimeSpan.Zero);
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresDate, f.Scope.Clock.Now.UtcDateTime.AddDays(2)));
        await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresDate, (DateTime?)null));
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddMinutes(-2)));
        Assert.Equal(1, (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "UTC", default))!.Current);
        Assert.Null(await Service(f).RequestRefreshAsync(other.Scope.UserId, f.Scope.ListId, f.ItemId, "UTC", default));
        Assert.Null(await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, other.ItemId, "UTC", default));
        Assert.Equal(1, (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "Australia/Adelaide", default))!.Queued);
        await f.Scope.Db.ColesExtensionTasks.Where(t => t.Key == key).ExecuteDeleteAsync();
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddMinutes(-1)));
        Assert.Equal(1, (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "Australia/Adelaide", default))!.Current);
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddMinutes(1)));
        Assert.Equal(1, (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "Australia/Adelaide", default))!.Queued);
    }

    [PostgreSqlFact]
    public async Task Product_request_is_scoped_and_list_request_includes_remaining_products_only()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await StaleAsync(f);
        await f.AddItemAsync();
        var second = await f.Scope.Db.ShoppingListProducts.SingleAsync(i => i.ShoppingListId == f.Scope.ListId && i.Id != f.ItemId);
        var url = "https://www.coles.com.au/product/" + Random.Shared.NextInt64(100000000, 900000000);
        f.Scope.Db.ShopProducts.Add(new()
        {
            ProductId = second.ProductId,
            ShopId = 1,
            ShopProductCode = url.Split('/').Last(),
            ProductUrl = url,
            NameAtShop = "Second product",
            MatchType = myshoppinglist_api.Models.MatchType.Exact,
            FirstFoundDate = f.Scope.Clock.Now.UtcDateTime,
            LastFoundDate = f.Scope.Clock.Now.UtcDateTime
        });
        await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId, "UTC", default))!.Queued);
        Assert.False(await f.Scope.Db.ColesExtensionTasks.AnyAsync(t => t.Url == url));
        await f.Scope.Db.ShoppingListProducts.Where(i => i.Id == second.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsHidden, true));
        var hidden = (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, null, "UTC", default))!;
        Assert.Equal(0, hidden.Queued); Assert.Equal(1, hidden.AlreadyRunning);
        await f.Scope.Db.ShoppingListProducts.Where(i => i.Id == second.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsHidden, false));
        Assert.Equal(1, (await Service(f).RequestRefreshAsync(f.Scope.UserId, f.Scope.ListId, null, "UTC", default))!.Queued);
        Assert.True(await f.Scope.Db.ColesExtensionTasks.AnyAsync(t => t.Url == url));
        await f.Scope.Db.ShopProducts.Where(m => m.ProductId == second.ProductId).ExecuteDeleteAsync();
    }

}
