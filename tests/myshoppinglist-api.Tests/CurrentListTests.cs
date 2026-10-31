using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class CurrentListTests
{
    [PostgreSqlFact]
    public async Task Selection_is_explicit_owned_shared_versioned_and_does_not_create_lists_on_read()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var first = await ListFixture.CreateAsync();
        await using var second = await ListFixture.CreateAsync();
        var db = first.Scope.Db;
        var service = new CurrentListService(db, first.Scope.Clock);
        Assert.Equal(new CurrentListResponse(0, null, "selection_required"), await service.ReadAsync(first.Scope.UserId, default));
        Assert.Empty(await db.UserCurrentLists.ToArrayAsync());
        var now = first.Scope.Clock.Now.UtcDateTime;
        var extra = new ShoppingList { UserAccountId = first.Scope.UserId, Name = "Second", CreatedDate = now, UpdatedDate = now };
        db.ShoppingLists.Add(extra);
        await db.SaveChangesAsync();
        var selected = await service.SetAsync(first.Scope.UserId, new(first.Scope.UserId, extra.Id, 0), default);
        Assert.Equal("selected", selected.Status);
        Assert.Equal(extra.Id, selected.List!.Id);
        Assert.Equal(1, selected.SelectionVersion);
        Assert.Equal(selected, await new CurrentListService(db, first.Scope.Clock).ReadAsync(first.Scope.UserId, default));
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.SetAsync(first.Scope.UserId,
            new(first.Scope.UserId, second.Scope.ListId, 1), default))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => service.SetAsync(first.Scope.UserId,
            new(second.Scope.UserId, extra.Id, 1), default))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => service.SetAsync(first.Scope.UserId,
            new(first.Scope.UserId, first.Scope.ListId, 0), default))).Status);
        var unchanged = await service.SetAsync(first.Scope.UserId, new(first.Scope.UserId, extra.Id, 1), default);
        Assert.Equal(1, unchanged.SelectionVersion);
        var changed = await service.SetAsync(first.Scope.UserId, new(first.Scope.UserId, first.Scope.ListId, 1), default);
        Assert.Equal(2, changed.SelectionVersion);
        await db.ShoppingLists.Where(l => l.Id == first.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        Assert.Equal(new CurrentListResponse(2, null, "selection_unavailable"), await service.ReadAsync(first.Scope.UserId, default));
        Assert.Equal("selection_required", (await service.ReadAsync(second.Scope.UserId, default)).Status);
    }

    [PostgreSqlFact]
    public async Task Database_rejects_cross_account_selection_even_if_service_is_bypassed()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var first = await ListFixture.CreateAsync();
        await using var second = await ListFixture.CreateAsync();
        first.Scope.Db.UserCurrentLists.Add(new() { UserAccountId = first.Scope.UserId,
            ShoppingListId = second.Scope.ListId, UpdatedDate = first.Scope.Clock.Now.UtcDateTime });
        await Assert.ThrowsAsync<DbUpdateException>(() => first.Scope.Db.SaveChangesAsync());
        first.Scope.Db.ChangeTracker.Clear();
    }
}
