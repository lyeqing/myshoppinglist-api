using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ProductLibraryServiceTests
{
    [PostgreSqlFact]
    public async Task ProductLibrary_concurrent_badges_and_adds_preserve_independent_flags_and_one_list_item()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(f, false);
        var original = await f.ItemAsync();
        async Task Save(string kind)
        {
            await using var db = PersistenceScope.Context();
            await new ProductLibraryService(db, f.Scope.Clock).SavePreferenceAsync(f.Scope.UserId, original.Product.Id, new(f.Scope.UserId, kind, true), default);
        }
        await Task.WhenAll(Save("favorite"), Save("essential"));
        var library = new ProductLibraryService(f.Scope.Db, f.Scope.Clock);
        var preference = Assert.Single(await library.PreferencesAsync(f.Scope.UserId, [original.Product.Id], default));
        Assert.True(preference.IsFavorite); Assert.True(preference.IsEssential);
        // Remove Essential before creating the empty target for concurrent Adds.
        await library.SavePreferenceAsync(f.Scope.UserId, original.Product.Id, new(f.Scope.UserId, "essential", false), default);
        var target = await f.Service.CreateAsync(f.Scope.UserId, new("Concurrent"), null, default);
        async Task<ProductLibraryAddResponse> Add()
        {
            await using var db = PersistenceScope.Context();
            return await new ProductLibraryService(db, f.Scope.Clock).AddAsync(f.Scope.UserId, original.Product.Id, target.Id, new(f.Scope.UserId), default);
        }
        var results = await Task.WhenAll(Add(), Add());
        Assert.Single(results, r => !r.AlreadyOnList); Assert.Single(results, r => r.AlreadyOnList);
        Assert.Equal(results[0].ItemId, results[1].ItemId);
        Assert.Single(await f.Scope.Db.ShoppingListProducts.Where(i => i.ShoppingListId == target.Id).ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task ProductLibrary_badges_are_independent_owned_and_available_to_free_accounts()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(f, false);
        await ShoppingListServiceTests.RegisterFixtureAsync(other, true);
        var item = await f.ItemAsync();
        var service = new ProductLibraryService(f.Scope.Db, f.Scope.Clock);
        var favorite = await service.SavePreferenceAsync(f.Scope.UserId, item.Product.Id, new(f.Scope.UserId, "favorite", true), default);
        Assert.True(favorite.IsFavorite); Assert.False(favorite.IsEssential);
        var essential = await service.SavePreferenceAsync(f.Scope.UserId, item.Product.Id, new(f.Scope.UserId, "essential", true), default);
        Assert.True(essential.IsFavorite); Assert.True(essential.IsEssential);
        var removed = await service.SavePreferenceAsync(f.Scope.UserId, item.Product.Id, new(f.Scope.UserId, "favorite", false), default);
        Assert.False(removed.IsFavorite); Assert.True(removed.IsEssential);
        Assert.Empty((await service.ReadAsync(f.Scope.UserId, "favorites", "all", null, 0, 24, default)).Items);
        Assert.Single((await service.ReadAsync(f.Scope.UserId, "essentials", "all", null, 0, 24, default)).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.SavePreferenceAsync(other.Scope.UserId, item.Product.Id, new(other.Scope.UserId, "favorite", true), default))).Status);
        Assert.Empty(await service.PreferencesAsync(other.Scope.UserId, [item.Product.Id], default));
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => service.SavePreferenceAsync(f.Scope.UserId, item.Product.Id, new(other.Scope.UserId, "favorite", false), default))).Status);
        Assert.Equal(item, await f.ItemAsync());
    }

    [PostgreSqlFact]
    public async Task ProductLibrary_purchased_filters_undo_search_and_deletion_preserve_truthful_history()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(f, false);
        await f.AddItemAsync(); // Keep the list open while purchase/undo are exercised.
        var service = new ProductLibraryService(f.Scope.Db, f.Scope.Clock);
        var item = await f.ItemAsync();
        var history = await service.ReadAsync(f.Scope.UserId, "history", "all", null, 0, 24, default);
        Assert.Equal(item.Product.Id, Assert.Single(history.Items).Product.Id);
        Assert.Empty((await service.ReadAsync(f.Scope.UserId, "history", "purchased", null, 0, 24, default)).Items);
        var saved = await f.UpdateAsync(new(item.Quantity, item.Notes, true, false, item.UpdatedDate));
        Assert.Equal(200, saved.StatusCode);
        Assert.NotNull(Assert.Single((await service.ReadAsync(f.Scope.UserId, "history", "purchased", null, 0, 24, default)).Items).LastPurchasedDate);
        var undone = await f.UpdateAsync(new(item.Quantity, item.Notes, false, false, saved.Item!.UpdatedDate));
        Assert.Equal(200, undone.StatusCode);
        Assert.Empty((await service.ReadAsync(f.Scope.UserId, "history", "purchased", null, 0, 24, default)).Items);
        Assert.Single((await service.ReadAsync(f.Scope.UserId, "history", "unpurchased", item.Product.Name[..8].ToLowerInvariant(), 0, 24, default)).Items);
        Assert.Empty((await service.ReadAsync(f.Scope.UserId, "history", "all", "%", 0, 24, default)).Items);
        var list = (await f.Service.ManageAsync(f.Scope.UserId, default)).Lists.Single();
        await f.Service.ChangeListAsync(f.Scope.UserId, list.Id, new(list.UpdatedDate), false, default);
        Assert.Single((await service.ReadAsync(f.Scope.UserId, "history", "all", null, 0, 24, default)).Items);
        Assert.Null((await f.Scope.Db.UserProductActivities.SingleAsync(a => a.ProductId == item.Product.Id)).ShoppingListProductId);
    }

    [PostgreSqlFact]
    public async Task ProductLibrary_add_is_idempotent_and_preserves_quantity_notes_and_purchase_state()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        await using var other = await ListFixture.CreateAsync();
        await ShoppingListServiceTests.RegisterFixtureAsync(f, false);
        var service = new ProductLibraryService(f.Scope.Db, f.Scope.Clock);
        var original = await f.ItemAsync();
        var target = await f.Service.CreateAsync(f.Scope.UserId, new("Reuse"), null, default);
        var added = await service.AddAsync(f.Scope.UserId, original.Product.Id, target.Id, new(f.Scope.UserId), default);
        Assert.False(added.AlreadyOnList);
        var edited = await f.Service.UpdateAsync(f.Scope.UserId, target.Id, added.ItemId, new(7, "Keep this", false, false,
            (await f.Service.ReadAsync(f.Scope.UserId, target.Id, null, 24, true, true, default))!.Items.Single().UpdatedDate), default);
        var repeated = await service.AddAsync(f.Scope.UserId, original.Product.Id, target.Id, new(f.Scope.UserId), default);
        Assert.True(repeated.AlreadyOnList); Assert.Equal(added.ItemId, repeated.ItemId);
        Assert.Equal(edited.Item, Assert.Single((await f.Service.ReadAsync(f.Scope.UserId, target.Id, null, 24, true, true, default))!.Items));
        Assert.Equal(2, await f.Scope.Db.UserProductActivities.CountAsync(a => a.ProductId == original.Product.Id));
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.AddAsync(f.Scope.UserId, original.Product.Id, other.Scope.ListId, new(f.Scope.UserId), default))).Status);
    }

    [PostgreSqlFact]
    public async Task ProductLibrary_rejects_trials_and_invalid_filters_and_paginates_without_duplicates()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await ListFixture.CreateAsync();
        var service = new ProductLibraryService(f.Scope.Db, f.Scope.Clock);
        Assert.Equal(403, (await Assert.ThrowsAsync<UserImportException>(() => service.ReadAsync(f.Scope.UserId, "history", "all", null, 0, 24, default))).Status);
        await ShoppingListServiceTests.RegisterFixtureAsync(f, true);
        await f.AddItemAsync();
        var item = await f.Scope.Db.ShoppingListProducts.OrderByDescending(i => i.Id).FirstAsync();
        await ProductLibraryService.TrackActivityAsync(f.Scope.Db, f.Scope.UserId, item, default);
        await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
        var first = await service.ReadAsync(f.Scope.UserId, "history", "all", null, 0, 1, default);
        var second = await service.ReadAsync(f.Scope.UserId, "history", "all", null, first.NextOffset!.Value, 1, default);
        Assert.Equal(2, first.Total); Assert.NotEqual(first.Items.Single().Product.Id, second.Items.Single().Product.Id); Assert.Null(second.NextOffset);
        foreach (var view in new[] { "bad", "HISTORY" })
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => service.ReadAsync(f.Scope.UserId, view, "all", null, 0, 24, default))).Status);
    }
}
