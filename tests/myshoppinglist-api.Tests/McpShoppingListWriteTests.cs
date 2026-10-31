using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ModelContextProtocol;
using myshoppinglist_api.Data;
using myshoppinglist_api.Mcp;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class McpShoppingListWriteTests
{
    [PostgreSqlFact]
    public async Task Owned_list_workflow_is_versioned_idempotent_partial_and_price_honest()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await WriteFixture.CreateAsync();
        var tool = f.Tool(f.A.Id);
        var createId = Guid.NewGuid().ToString();
        var created = await tool.CreateListAsync(createId, "AI weekly list");
        var replay = await tool.CreateListAsync(createId, "AI weekly list");
        Assert.Equal(created.ListId, replay.ListId); Assert.True(replay.Replayed);
        Assert.Equal("request_id_conflict", (await Assert.ThrowsAsync<McpException>(() => tool.CreateListAsync(createId, "Different intention"))).Message);
        Assert.Equal("list_limit_reached", (await Assert.ThrowsAsync<McpException>(() => tool.CreateListAsync(Guid.NewGuid().ToString(), "Third free list"))).Message);
        Assert.Empty(await f.Db.UserCurrentLists.ToArrayAsync()); // Creating does not silently select.
        var selected = await tool.SetCurrentListAsync(Guid.NewGuid().ToString(), created.ListId!, "0");
        Assert.Equal("1", selected.SelectionVersion);
        Assert.Equal(long.Parse(created.ListId!), (await new CurrentListService(f.Db, f.Clock).ReadAsync(f.A.Id, default)).List!.Id);
        var selectionConflict = await Assert.ThrowsAsync<McpWriteConflictException>(() => tool.SetCurrentListAsync(Guid.NewGuid().ToString(), f.ListA.Id.ToString(), "0"));
        Assert.Equal("1", selectionConflict.Details.CurrentSelectionVersion);

        var addId = Guid.NewGuid().ToString();
        var added = await tool.AddProductToListAsync(addId, created.ListId!, genericName: "Rice", quantity: 2);
        Assert.True((await tool.AddProductToListAsync(addId, created.ListId!, genericName: "Rice", quantity: 2)).Replayed);
        var item = await f.Db.ShoppingListProducts.AsNoTracking().Include(i => i.Product).SingleAsync(i => i.Id == long.Parse(added.ItemId!));
        Assert.Equal(f.A.Id, item.Product.UserAccountId); Assert.Equal(2, item.Quantity);
        Assert.Null(item.Product.Brand); Assert.Null(item.Product.PackSize);
        var linked = await tool.AddProductToListAsync(Guid.NewGuid().ToString(), f.ListA.Id.ToString(), item.ProductId.ToString());
        Assert.Equal("added", linked.Status);
        Assert.Equal(1, await f.Db.ShoppingListProducts.Where(i => i.Id == long.Parse(linked.ItemId!)).Select(i => i.Quantity).SingleAsync());
        Assert.Empty(await f.Db.ShopProducts.ToArrayAsync()); Assert.Empty(await f.Db.ShopProductPrices.ToArrayAsync());
        var edited = await tool.UpdateListItemAsync(Guid.NewGuid().ToString(), created.ListId!, added.ItemId!, added.UpdatedAt!.Value.ToString("O"), notes: "Keep this private note");
        var quantity = await tool.UpdateListItemAsync(Guid.NewGuid().ToString(), created.ListId!, added.ItemId!, edited.UpdatedAt!.Value.ToString("O"), quantity: 4);
        var unchanged = await tool.AddProductToListAsync(Guid.NewGuid().ToString(), created.ListId!, genericName: "RICE", quantity: 99);
        Assert.Equal("already_on_list", unchanged.Status);
        item = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(4, item.Quantity); Assert.Equal("Keep this private note", item.Notes);
        var stale = await Assert.ThrowsAsync<McpWriteConflictException>(() => tool.UpdateListItemAsync(Guid.NewGuid().ToString(), created.ListId!, added.ItemId!, added.UpdatedAt.Value.ToString("O"), quantity: 8));
        Assert.Equal(quantity.UpdatedAt, stale.Details.CurrentUpdatedAt);
        Assert.True(stale.Details.RefreshRequired);
        Assert.Equal("confirmation_required", (await Assert.ThrowsAsync<McpException>(() => tool.RemoveListItemAsync(Guid.NewGuid().ToString(), created.ListId!, added.ItemId!, quantity.UpdatedAt!.Value.ToString("O"), false))).Message);
        var removeId = Guid.NewGuid().ToString();
        await tool.RemoveListItemAsync(removeId, created.ListId!, added.ItemId!, quantity.UpdatedAt!.Value.ToString("O"), true);
        Assert.True((await tool.RemoveListItemAsync(removeId, created.ListId!, added.ItemId!, quantity.UpdatedAt.Value.ToString("O"), true)).Replayed);
        Assert.Empty(await f.Db.ShoppingListProducts.Where(i => i.ShoppingListId == long.Parse(created.ListId!)).ToArrayAsync());
        Assert.DoesNotContain("Keep this private note", string.Join(' ', await f.Db.McpMutationReceipts.Select(r => r.ResultJson).ToArrayAsync()));
    }

    [PostgreSqlFact]
    public async Task Scopes_ownership_and_account_bound_request_keys_are_enforced()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await WriteFixture.CreateAsync();
        Assert.Equal("insufficient_scope", (await Assert.ThrowsAsync<McpException>(() => f.Tool(f.A.Id, "lists.read").CreateListAsync(Guid.NewGuid().ToString(), "Denied"))).Message);
        var a = f.Tool(f.A.Id); var b = f.Tool(f.B.Id);
        var added = await a.AddProductToListAsync(Guid.NewGuid().ToString(), f.ListA.Id.ToString(), genericName: "Private pantry item");
        var productId = await f.Db.ShoppingListProducts.Where(i => i.Id == long.Parse(added.ItemId!)).Select(i => i.ProductId).SingleAsync();
        Assert.Equal("not_found", (await Assert.ThrowsAsync<McpException>(() => b.AddProductToListAsync(Guid.NewGuid().ToString(), f.ListB.Id.ToString(), productId.ToString()))).Message);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<McpException>(() => b.UpdateListItemAsync(Guid.NewGuid().ToString(), f.ListA.Id.ToString(), added.ItemId!, added.UpdatedAt!.Value.ToString("O"), quantity: 3))).Message);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<McpException>(() => b.SetCurrentListAsync(Guid.NewGuid().ToString(), f.ListA.Id.ToString(), "0"))).Message);
        var request = Guid.NewGuid().ToString();
        Assert.NotEqual((await a.CreateListAsync(request, "A list")).ListId, (await b.CreateListAsync(request, "B list")).ListId);
        Assert.Equal(2, await f.Db.McpMutationReceipts.CountAsync(r => r.RequestId == Guid.Parse(request)));
    }

    [PostgreSqlFact]
    public async Task Concurrent_retries_commit_one_mutation_and_one_receipt()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await WriteFixture.CreateAsync();
        var request = Guid.NewGuid().ToString();
        async Task<myshoppinglist_api.Contracts.McpMutationResult> Create()
        {
            await using var db = WriteFixture.Context();
            return await f.Tool(f.A.Id, db: db).CreateListAsync(request, "Concurrent list");
        }
        var result = await Task.WhenAll(Create(), Create()).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(result[0].ListId, result[1].ListId);
        Assert.Single(result, r => r.Replayed);
        Assert.Equal(2, await f.Db.ShoppingLists.CountAsync(l => l.UserAccountId == f.A.Id));
        Assert.Equal(1, await f.Db.McpMutationReceipts.CountAsync());
    }

    [PostgreSqlFact]
    public async Task Receipt_failure_rolls_back_the_mutation_and_retry_can_succeed_once()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await WriteFixture.CreateAsync();
        var failure = new RejectReceipt();
        await using var db = WriteFixture.Context(failure);
        var tool = f.Tool(f.A.Id, db: db);
        var request = Guid.NewGuid().ToString();
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => tool.CreateListAsync(request, "Atomic list"));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(1, await f.Db.ShoppingLists.CountAsync(l => l.UserAccountId == f.A.Id));
        Assert.Empty(await f.Db.McpMutationReceipts.ToArrayAsync());
        failure.Enabled = false;
        await tool.CreateListAsync(request, "Atomic list");
        Assert.True((await tool.CreateListAsync(request, "Atomic list")).Replayed);
        Assert.Equal(2, await f.Db.ShoppingLists.CountAsync(l => l.UserAccountId == f.A.Id));
        Assert.Equal(1, await f.Db.McpMutationReceipts.CountAsync());
    }

    [PostgreSqlFact]
    public async Task Deleting_a_current_list_cannot_reuse_its_revision_and_accept_an_old_selection_request()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var f = await WriteFixture.CreateAsync();
        var tool = f.Tool(f.A.Id);
        await tool.SetCurrentListAsync(Guid.NewGuid().ToString(), f.ListA.Id.ToString(), "0");
        var lists = new ShoppingListService(f.Db, f.Clock);
        var currentVersion = (await lists.ManageAsync(f.A.Id, default)).Lists.Single(l => l.Id == f.ListA.Id).UpdatedDate;
        await lists.ChangeListAsync(f.A.Id, f.ListA.Id, new(currentVersion), false, default);
        var unavailable = await new CurrentListService(f.Db, f.Clock).ReadAsync(f.A.Id, default);
        Assert.Equal("selection_unavailable", unavailable.Status);
        Assert.Equal(1, unavailable.SelectionVersion);
        var replacement = await tool.CreateListAsync(Guid.NewGuid().ToString(), "Replacement");
        Assert.Equal("2", (await tool.SetCurrentListAsync(Guid.NewGuid().ToString(), replacement.ListId!, "1")).SelectionVersion);
        var stale = await Assert.ThrowsAsync<McpWriteConflictException>(() => tool.SetCurrentListAsync(Guid.NewGuid().ToString(), replacement.ListId!, "1"));
        Assert.Equal("2", stale.Details.CurrentSelectionVersion);
    }

    private sealed class RejectReceipt : DbCommandInterceptor
    {
        public bool Enabled { get; set; } = true;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO \"McpMutationReceipts\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Simulated receipt persistence failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class WriteFixture : IAsyncDisposable
    {
        public MyShoppingListDbContext Db { get; } = Context();
        public PersistenceClock Clock { get; } = new();
        public UserAccount A { get; private set; } = null!;
        public UserAccount B { get; private set; } = null!;
        public ShoppingList ListA { get; private set; } = null!;
        public ShoppingList ListB { get; private set; } = null!;
        public static MyShoppingListDbContext Context(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<MyShoppingListDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!).AddInterceptors(interceptors).Options);
        public static async Task<WriteFixture> CreateAsync()
        {
            var f = new WriteFixture(); var now = f.Clock.Now.UtcDateTime;
            f.A = new() { Email = "a@example.test", DisplayName = "A", PasswordHash = "fixture", PasswordSalt = "fixture", CreatedDate = now, UpdatedDate = now };
            f.B = new() { Email = "b@example.test", DisplayName = "B", PasswordHash = "fixture", PasswordSalt = "fixture", CreatedDate = now, UpdatedDate = now };
            f.ListA = new() { UserAccount = f.A, Name = "A original", CreatedDate = now, UpdatedDate = now };
            f.ListB = new() { UserAccount = f.B, Name = "B original", CreatedDate = now, UpdatedDate = now };
            f.Db.AddRange(f.ListA, f.ListB); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear(); return f;
        }
        public ShoppingListWriteTools Tool(long account, string scopes = "lists.write", MyShoppingListDbContext? db = null)
        {
            db ??= Db;
            var context = new FixedContext { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, account.ToString(CultureInfo.InvariantCulture)), new Claim("scope", scopes)], "ValidatedTest")) } };
            return new(new(db, Clock), new(db, Clock), new(db, Clock), new(db, Clock), db, context, Clock);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class FixedContext : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
}
