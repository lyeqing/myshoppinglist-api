using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ShoppingListServiceTests
{
    internal static async Task RegisterFixtureAsync(ListFixture f, bool paid)
    {
        var password = myshoppinglist_api.Security.PasswordHasher.Hash("Testing123!");
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.IsTrial, false).SetProperty(a => a.IsPaid, paid)
            .SetProperty(a => a.Email, f.Scope.Code + "@example.test")
            .SetProperty(a => a.PasswordHash, password.Hash).SetProperty(a => a.PasswordSalt, password.Salt)
            .SetProperty(a => a.ExpiresDate, (DateTime?)null));
        f.Scope.Db.ChangeTracker.Clear();
    }
    [PostgreSqlFact]
    public async Task List_limits_are_serialized_and_archive_delete_free_slots_with_paid_only_history()
    {
        await using var f = await ListFixture.CreateAsync();
        await RegisterFixtureAsync(f, false);
        async Task<bool> Create()
        {
            await using var db = PersistenceScope.Context();
            try { await new ShoppingListService(db, f.Scope.Clock).CreateAsync(f.Scope.UserId, new(null), "Australia/Adelaide", default); return true; }
            catch (UserImportException e) when (e.Status == 409) { return false; }
        }
        var concurrent = await Task.WhenAll(Create(), Create());
        Assert.Single(concurrent, success => success);
        var free = await f.Service.ManageAsync(f.Scope.UserId, default);
        Assert.Equal(2, free.Limit); Assert.Equal(2, free.Lists.Length);
        Assert.Equal(403, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.HistoryAsync(f.Scope.UserId, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.ChangeListAsync(f.Scope.UserId, free.Lists[0].Id, new(free.Lists[0].UpdatedDate), true, default))).Status);
        await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsPaid, true));
        for (var i = 0; i < 3; i++) await f.Service.CreateAsync(f.Scope.UserId, new(null), "Australia/Adelaide", default);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.CreateAsync(f.Scope.UserId, new("Sixth"), null, default))).Status);
        var full = await f.Service.ManageAsync(f.Scope.UserId, default);
        Assert.Equal(5, full.Lists.Length); Assert.Equal(5, full.Limit);
        var first = full.Lists[0];
        await f.Service.ChangeListAsync(f.Scope.UserId, first.Id, new(first.UpdatedDate), true, default);
        Assert.Single(await f.Service.HistoryAsync(f.Scope.UserId, default));
        Assert.Single((await f.Service.HistoryDetailAsync(f.Scope.UserId, first.Id, default)).Items);
        await f.Service.CreateAsync(f.Scope.UserId, new("Replacement"), null, default);
        var archived = (await f.Service.HistoryAsync(f.Scope.UserId, default))[0];
        await f.Service.ChangeListAsync(f.Scope.UserId, archived.Id, new(archived.UpdatedDate), false, default);
        Assert.Empty(await f.Service.HistoryAsync(f.Scope.UserId, default));
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.HistoryDetailAsync(f.Scope.UserId, first.Id, default))).Status);
        Assert.False(await f.Scope.Db.ShoppingListProducts.AnyAsync(i => i.ShoppingListId == first.Id));
        Assert.True(await f.Scope.Db.Products.AnyAsync(p => p.Name == f.Scope.Source.Identity.Name));
    }

    [PostgreSqlFact]
    public async Task Automatic_archiving_requires_nonempty_list_and_every_item_hidden_or_purchased()
    {
        foreach (var paid in new[] { false, true })
        {
            await using var f = await ListFixture.CreateAsync();
            await RegisterFixtureAsync(f, paid);
            var empty = await f.Service.CreateAsync(f.Scope.UserId, new("Empty"), null, default);
            Assert.False(await f.Scope.Db.ShoppingLists.Where(l => l.Id == empty.Id).Select(l => l.IsArchived).SingleAsync());
            await f.AddItemAsync(hidden: true);
            var original = await f.ItemAsync();
            var saved = await f.UpdateAsync(new(original.Quantity, "Keep note", true, false, original.UpdatedDate));
            Assert.True(saved.Item!.ListArchived);
            Assert.Null(await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default));
            Assert.Single((await f.Service.ManageAsync(f.Scope.UserId, default)).Lists);
            Assert.Equal(ProductImportJobStatus.Cancelled, await f.Scope.Db.ProductImportJobs.Where(j => j.Id == f.Scope.Job.Id).Select(j => j.Status).SingleAsync());
            if (paid) Assert.Equal(2, (await f.Service.HistoryDetailAsync(f.Scope.UserId, f.Scope.ListId, default)).Items.Length);
            else Assert.Equal(403, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.HistoryDetailAsync(f.Scope.UserId, f.Scope.ListId, default))).Status);
        }
    }
    [PostgreSqlFact]
    public async Task Multibuy_is_separate_from_single_price_totals_and_stale_offers_are_not_promoted()
    {
        await using var f = await ListFixture.CreateAsync();
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 30m).SetProperty(p => p.SourceType, SourceType.RetailerPage)
                .SetProperty(p => p.SourceUrl, "https://www.coles.com.au/product/1115507")
                .SetProperty(p => p.SpecialType, "MULTI_SAVE").SetProperty(p => p.SpecialDescription, "Pick any 2 for $30"));
        var original = await f.ItemAsync();
        await f.UpdateAsync(new(1, null, false, false, original.UpdatedDate));
        var plan = (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!;
        var price = Assert.Single(Assert.Single(plan.Items).Prices);
        Assert.Equal("Fresh", price.Status); Assert.True(price.IncludedInTotal);
        Assert.Equal(new ShoppingListMultibuy(2, 30, 15, 30), price.Multibuy);
        Assert.Equal(30m, plan.Lowest.Subtotal); Assert.Empty(plan.Lowest.MissingItems);
        var item = await f.ItemAsync();
        await f.UpdateAsync(new(2, null, false, false, item.UpdatedDate));
        Assert.Equal(60m, (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!.Lowest.Subtotal);
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == f.Scope.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddDays(-8)));
        plan = (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!;
        Assert.Null(plan.Items[0].Prices[0].Multibuy);
        Assert.Equal(0m, plan.Lowest.Subtotal);
        Assert.Equal("Pick any 2 for $30", plan.Items[0].Prices[0].SpecialDescription);
    }

    [Fact]
    public void Planning_totals_include_all_quantities_and_report_missing_prices_without_inventing_savings()
    {
        ShoppingListPlanningItem Row(long id, int quantity, decimal? coles, decimal? woolworths) => new(
            new(id, 1, new(id, "Product " + id, null, null, null, null, null, null), quantity, null, true, null, true, null, default, default),
            [new(1, "Coles", coles, coles.HasValue, "Fresh", null, null, null), new(2, "Woolworths", woolworths, woolworths.HasValue, "No price", null, null, null)]);
        var plan = ShoppingListService.SummarizePlan(1, "Plan", [Row(1, 2, 3, 5), Row(2, 3, 6, 4), Row(3, 1, 8, null), Row(4, 1, null, null)], [(1, "Coles"), (2, "Woolworths")]);
        Assert.Equal(26m, plan.Lowest.Subtotal);
        Assert.Equal(3, plan.Lowest.PricedCount);
        Assert.Equal(4, Assert.Single(plan.Lowest.MissingItems).ItemId);
        Assert.Equal(32m, plan.Retailers[0].Subtotal);
        Assert.Equal(22m, plan.Retailers[1].Subtotal);
        Assert.Equal(2, plan.Retailers[1].MissingItems.Length);
        var free = ShoppingListService.SummarizePlan(1, "Free", [Row(1, 2, 0, 0)], [(1, "Coles"), (2, "Woolworths")]);
        Assert.Equal(0, free.Lowest.Subtotal);
        Assert.Empty(free.Lowest.MissingItems);
    }

    [PostgreSqlFact]
    public async Task Plan_reads_entire_list_and_preserves_stale_prices_but_excludes_them_from_totals()
    {
        await using var f = await ListFixture.CreateAsync();
        for (var i = 0; i < 21; i++) await f.AddItemAsync(hidden: i % 2 == 0, purchased: true);
        await f.AddItemAsync(deleted: true);
        var plan = (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!;
        Assert.Equal(22, plan.Items.Length);
        Assert.Equal(46m, plan.Lowest.Subtotal);
        Assert.Equal(21, plan.Lowest.MissingItems.Length);
        Assert.Contains(plan.Retailers, s => s.Name == "Woolworths" && s.PricedCount == 0 && s.MissingItems.Length == 22);
        Assert.Null(await f.Service.PlanAsync(-1, f.Scope.ListId, default));
        var product = (await f.ItemAsync()).Product.Id;
        await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == product)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddDays(-8)));
        plan = (await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!;
        var price = Assert.Single(plan.Items.Single(i => i.Item.Id == f.ItemId).Prices);
        Assert.Equal(23m, price.Price);
        Assert.Equal("Stale price", price.Status);
        Assert.False(price.IncludedInTotal);
        Assert.Equal(0m, plan.Lowest.Subtotal);
        await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        Assert.Null(await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default));
    }

    [PostgreSqlFact]
    public async Task Delete_is_versioned_owned_preserves_catalogue_and_history_and_fences_unfinished_imports()
    {
        await using var f = await ListFixture.CreateAsync();
        var item = await f.ItemAsync();
        var request = new ShoppingListItemDeleteRequest(item.UpdatedDate);
        Assert.Equal(404, (await f.Service.DeleteAsync(-1, f.Scope.ListId, item.Id, request, default)).StatusCode);
        Assert.Equal(404, (await f.Service.DeleteAsync(f.Scope.UserId, -1, item.Id, request, default)).StatusCode);
        Assert.Equal(400, (await f.Service.DeleteAsync(f.Scope.UserId, f.Scope.ListId, item.Id, new(default), default)).StatusCode);
        Assert.Equal(409, (await f.Service.DeleteAsync(f.Scope.UserId, f.Scope.ListId, item.Id, new(item.UpdatedDate.AddSeconds(-1)), default)).StatusCode);
        f.Scope.Db.UserExtensionImportTasks.Add(new() { ProductImportJobId = f.Scope.Job.Id, RequestId = Guid.NewGuid(), UpdatedDate = item.UpdatedDate });
        await f.Scope.Db.SaveChangesAsync();
        f.Scope.Db.ChangeTracker.Clear();
        Assert.Equal(204, (await f.Service.DeleteAsync(f.Scope.UserId, f.Scope.ListId, item.Id, request, default)).StatusCode);
        Assert.Empty((await f.Service.PlanAsync(f.Scope.UserId, f.Scope.ListId, default))!.Items);
        Assert.True(await f.Scope.Db.Products.AnyAsync(p => p.Id == item.Product.Id));
        Assert.True(await f.Scope.Db.ShopProductPrices.AnyAsync(p => p.ShopProduct.ProductId == item.Product.Id));
        var job = await f.Scope.Db.ProductImportJobs.AsNoTracking().SingleAsync(j => j.Id == f.Scope.Job.Id);
        Assert.Null(job.ShoppingListProductId);
        Assert.Equal(ProductImportJobStatus.Cancelled, job.Status);
        Assert.Null(job.ClaimToken);
        Assert.Equal("Failed", (await f.Scope.Db.UserExtensionImportTasks.SingleAsync(t => t.ProductImportJobId == job.Id)).Status);
        var replay = await f.Scope.SaveAsync();
        Assert.NotEqual(ProductPersistenceStatus.Success, replay.Status);
        Assert.False(await f.Scope.Db.ShoppingListProducts.AnyAsync(i => i.Id == item.Id));
    }

    [PostgreSqlFact]
    public async Task Edits_preserve_identity_and_manage_purchase_dates_without_deletion()
    {
        await using var f = await ListFixture.CreateAsync();
        await f.AddItemAsync();
        var original = await f.ItemAsync();
        var purchased = (await f.UpdateAsync(new(4, "Two for the pantry", true, true, original.UpdatedDate))).Item!;
        Assert.Equal(4, purchased.Quantity); Assert.True(purchased.IsHidden); Assert.NotNull(purchased.PurchasedDate);
        Assert.Equal(original.Product.Id, purchased.Product.Id); Assert.Equal(original.AddedDate, purchased.AddedDate);
        var date = purchased.PurchasedDate;
        f.Scope.Clock.Now += TimeSpan.FromMinutes(1);
        var edited = (await f.UpdateAsync(new(5, null, true, false, purchased.UpdatedDate))).Item!;
        Assert.Null(edited.Notes); Assert.Equal(date, edited.PurchasedDate); Assert.False(edited.IsHidden);
        var unmarked = (await f.UpdateAsync(new(5, null, false, false, edited.UpdatedDate))).Item!;
        Assert.Null(unmarked.PurchasedDate); Assert.False(unmarked.IsPurchased);
        Assert.True(await f.Scope.Db.Products.AnyAsync(p => p.Id == original.Product.Id && !p.IsDeleted));
        Assert.Equal(2, await f.Scope.Db.ShoppingListProducts.CountAsync(i => i.ShoppingListId == f.Scope.ListId));
    }

    [PostgreSqlFact]
    public async Task Concurrent_edits_have_one_winner_and_no_op_preserves_version()
    {
        await using var f = await ListFixture.CreateAsync();
        var original = await f.ItemAsync();
        var unchanged = await f.UpdateAsync(new(original.Quantity, original.Notes, false, false, original.UpdatedDate));
        Assert.Equal(original.UpdatedDate, unchanged.Item!.UpdatedDate);
        async Task<ShoppingListUpdateResult> Edit(int quantity)
        {
            await using var db = PersistenceScope.Context();
            return await new ShoppingListService(db, f.Scope.Clock).UpdateAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId,
                new(quantity, null, false, false, original.UpdatedDate), default);
        }
        var results = await Task.WhenAll(Edit(3), Edit(4));
        Assert.Single(results, r => r.StatusCode == 200); Assert.Single(results, r => r.StatusCode == 409);
        Assert.Equal(results.Single(r => r.StatusCode == 200).Item!.Quantity, (await f.ItemAsync()).Quantity);
        Assert.True((await f.ItemAsync()).UpdatedDate > original.UpdatedDate);
    }

    [PostgreSqlFact]
    public async Task Pagination_filters_hidden_purchased_and_deleted_products()
    {
        await using var f = await ListFixture.CreateAsync();
        await f.AddItemAsync(hidden: true);
        await f.AddItemAsync(purchased: true);
        await f.AddItemAsync(deleted: true);
        var visible = (await f.Service.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, 20, false, true, default))!;
        Assert.Equal(2, visible.Items.Count);
        var pending = (await f.Service.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, 20, false, false, default))!;
        Assert.Equal(f.ItemId, Assert.Single(pending.Items).Id);
        var first = (await f.Service.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, 2, true, true, default))!;
        Assert.Equal(2, first.Items.Count); Assert.NotNull(first.NextBeforeId);
        var second = (await f.Service.ReadAsync(f.Scope.UserId, f.Scope.ListId, first.NextBeforeId, 2, true, true, default))!;
        Assert.Single(second.Items); Assert.Null(second.NextBeforeId);
        Assert.Equal(3, first.Items.Concat(second.Items).Select(i => i.Id).Distinct().Count());
    }

    [PostgreSqlFact]
    public async Task Invalid_values_and_cross_list_ids_do_not_change_items()
    {
        await using var f = await ListFixture.CreateAsync();
        var original = await f.ItemAsync();
        foreach (var request in new ShoppingListItemUpdateRequest[] {
            new(0, null, false, false, original.UpdatedDate), new(-1, null, false, false, original.UpdatedDate),
            new(1, new string('a', 4001), false, false, original.UpdatedDate), new(1, null, false, false, default),
            new(1, null, false, false, DateTime.SpecifyKind(original.UpdatedDate, DateTimeKind.Unspecified)) })
            Assert.Equal(400, (await f.UpdateAsync(request)).StatusCode);
        var valid = new ShoppingListItemUpdateRequest(4, null, false, false, original.UpdatedDate);
        Assert.Equal(404, (await f.Service.UpdateAsync(f.Scope.UserId + 100000, f.Scope.ListId, f.ItemId, valid, default)).StatusCode);
        Assert.Equal(404, (await f.Service.UpdateAsync(f.Scope.UserId, f.Scope.ListId + 100000, f.ItemId, valid, default)).StatusCode);
        Assert.Equal(404, (await f.Service.UpdateAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId + 100000, valid, default)).StatusCode);
        Assert.Null(await f.Service.ReadAsync(f.Scope.UserId + 100000, f.Scope.ListId, null, 20, true, true, default));
        Assert.Equal(original, await f.ItemAsync());
    }

    [PostgreSqlFact]
    public async Task Archived_expired_and_inactive_access_is_denied()
    {
        foreach (var condition in new[] { "archived", "list_expired", "trial_expired", "inactive" })
        {
            await using var f = await ListFixture.CreateAsync();
            var original = await f.ItemAsync();
            var past = f.Scope.Clock.Now.UtcDateTime.AddSeconds(-1);
            if (condition == "archived") await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
            if (condition == "list_expired") await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresDate, past));
            if (condition == "trial_expired") await f.Scope.Db.UserAccounts.Where(u => u.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.ExpiresDate, past));
            if (condition == "inactive") await f.Scope.Db.UserAccounts.Where(u => u.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            Assert.Null(await f.Service.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, 20, true, true, default));
            Assert.Equal(404, (await f.UpdateAsync(new(4, null, false, false, original.UpdatedDate))).StatusCode);
        }
    }

    [PostgreSqlFact]
    public async Task Expiry_before_commit_rolls_back_edits_and_read_rechecks_expiry()
    {
        await using var f = await ListFixture.CreateAsync();
        var original = await f.ItemAsync();
        var service = new ShoppingListService(f.Scope.Db, new ExpiringClock(f.Scope.Clock.Now, 3));
        Assert.Equal(404, (await service.UpdateAsync(f.Scope.UserId, f.Scope.ListId, f.ItemId,
            new(8, "must roll back", true, true, original.UpdatedDate), default)).StatusCode);
        Assert.Equal(original, await f.ItemAsync());
        var reader = new ShoppingListService(f.Scope.Db, new ExpiringClock(f.Scope.Clock.Now, 2));
        Assert.Null(await reader.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, 20, true, true, default));
    }

    [PostgreSqlFact]
    public async Task Reimport_replaces_quantity_preserving_notes_and_purchase_state_without_duplicate_items()
    {
        await using var f = await ListFixture.CreateAsync();
        await f.AddItemAsync();
        var original = await f.ItemAsync();
        var edited = (await f.UpdateAsync(new(9, "Keep this", true, true, original.UpdatedDate))).Item!;
        f.Scope.Clock.Now += TimeSpan.FromSeconds(1);
        var job = await f.Scope.AddJobAsync(f.Scope.Source);
        var saved = await f.Scope.SaveAsync(job);
        Assert.Equal(ProductPersistenceStatus.Success, saved.Status);
        Assert.Equal(f.ItemId, saved.ShoppingListProductId);
        var updated = await f.ItemAsync();
        Assert.Equal(job.RequestedQuantity, updated.Quantity);
        Assert.Equal(edited.Notes, updated.Notes);
        Assert.Equal(edited.IsPurchased, updated.IsPurchased);
        Assert.Equal(edited.IsHidden, updated.IsHidden);
        Assert.Equal(2, (await f.Service.ReadAsync(f.Scope.UserId, f.Scope.ListId, null, 20, true, true, default))!.Items.Count);
    }

    private sealed class ExpiringClock(DateTimeOffset start, int expireOnCall) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => ++calls >= expireOnCall ? start.AddHours(4) : start;
    }
}

internal sealed class ListFixture : IAsyncDisposable
{
    public PersistenceScope Scope { get; }
    public long ItemId { get; }
    private readonly List<long> extraProducts = [];
    public ShoppingListService Service => new(Scope.Db, Scope.Clock);
    private ListFixture(PersistenceScope scope, long itemId) { Scope = scope; ItemId = itemId; }
    public static async Task<ListFixture> CreateAsync()
    {
        var scope = await PersistenceScope.CreateAsync();
        try
        {
            var saved = await scope.SaveAsync();
            Assert.Equal(ProductPersistenceStatus.Success, saved.Status);
            await scope.CommitSetupAsync();
            return new(scope, saved.ShoppingListProductId!.Value);
        }
        catch { await scope.DisposeAsync(); throw; }
    }
    public async Task<ShoppingListItemResponse> ItemAsync() => (await Service.ReadAsync(Scope.UserId, Scope.ListId, null, 50, true, true, default))!.Items.Single(i => i.Id == ItemId);
    public Task<ShoppingListUpdateResult> UpdateAsync(ShoppingListItemUpdateRequest request) => Service.UpdateAsync(Scope.UserId, Scope.ListId, ItemId, request, default);
    public async Task AddItemAsync(bool hidden = false, bool purchased = false, bool deleted = false)
    {
        var now = Scope.Clock.Now.UtcDateTime;
        var product = new Product { Name = Scope.Code, IsDeleted = deleted, CreatedDate = now, UpdatedDate = now };
        Scope.Db.ShoppingListProducts.Add(new()
        {
            ShoppingListId = Scope.ListId,
            Product = product,
            IsHidden = hidden,
            IsPurchased = purchased,
            PurchasedDate = purchased ? now : null,
            AddedDate = now,
            UpdatedDate = now
        });
        await Scope.Db.SaveChangesAsync(); extraProducts.Add(product.Id); Scope.Db.ChangeTracker.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Scope.CleanupCommittedAsync();
            await Scope.Db.Products.Where(p => extraProducts.Contains(p.Id)).ExecuteDeleteAsync();
        }
        finally { await Scope.DisposeAsync(); }
    }
}
