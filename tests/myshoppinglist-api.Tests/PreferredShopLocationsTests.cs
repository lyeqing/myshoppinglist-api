using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class PreferredShopLocationsTests
{
    private static ShoppingPreferencesUpdate Request(ShoppingPreferencesResponse view, PreferredShopLocationSelection[]? branches = null,
        PreferredShopSelection[]? shops = null) =>
        new(view.AccountId, true, true, false, shops ?? [new(1, true), new(2, false)], view.UpdatedDate, branches);

    [PostgreSqlFact]
    public async Task Multiple_branches_persist_in_order_for_their_owner_without_enabling_branch_prices()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.BranchAsync("first"); var second = await f.BranchAsync("second"); var otherChain = await f.BranchAsync("other", 2);
        var initial = await f.Preferences.ReadAsync(f.Customer.Id, default); Assert.Empty(initial.Branches);
        var choices = new[] { new PreferredShopLocationSelection(2, otherChain), new(1, second), new(1, first) };
        var saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(initial, choices), default);
        Assert.Equal(choices.Select(b => b.ShopLocationId), saved.Branches.Select(b => b.Location.Id));
        Assert.All(saved.Branches, b => { Assert.True(b.IsAvailable); Assert.Null(b.UnavailableReason); Assert.False(b.Location.BranchPricesSupported); });
        await using var fresh = PersistenceScope.Context();
        var reloaded = await f.Service(fresh).ReadAsync(f.Customer.Id, default);
        Assert.Equal(choices.Select(b => b.ShopLocationId), reloaded.Branches.Select(b => b.Location.Id));
        Assert.Empty((await f.Preferences.ReadAsync(f.Trial.Id, default)).Branches);
        Assert.Equal(new long[] { 1 }, await ShoppingPreferencesService.AllowedShops(f.Db, f.Customer.Id).Select(s => s.Id).ToArrayAsync());
        Assert.False(await f.Db.ShopProductPrices.AnyAsync()); Assert.False(await f.Db.ProductImportJobs.AnyAsync());
        var json = JsonSerializer.Serialize(saved);
        foreach (var privateField in new[] { "SourceReference", "VerifiedByAccountId", "ReviewerAccountId", "Evidence", "private test evidence" })
            Assert.DoesNotContain(privateField, json);
    }

    [PostgreSqlFact]
    public async Task Omitted_or_null_branches_preserve_selections_and_explicit_empty_clears_them()
    {
        await using var f = await Fixture.CreateAsync(); var id = await f.BranchAsync("compatibility");
        var saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(await f.ViewAsync(), [new(1, id)]), default);
        // Deserializing an older request has the same null branch semantics as an explicit null.
        var oldJson = JsonSerializer.Serialize(new { expectedAccountId = saved.AccountId, compareOnlyPreferredShops = true,
            includeDifferentPackSizes = false, includeDifferentBrands = true, shops = new[] { new { shopId = 2, isEnabled = true } }, expectedUpdatedDate = saved.UpdatedDate });
        var oldRequest = JsonSerializer.Deserialize<ShoppingPreferencesUpdate>(oldJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        saved = await f.Preferences.SaveAsync(f.Customer.Id, oldRequest, default);
        Assert.Equal(id, Assert.Single(saved.Branches).Location.Id); Assert.True(saved.IncludeDifferentBrands); Assert.False(saved.IncludeDifferentPackSizes);
        saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(saved), default);
        Assert.Equal(id, Assert.Single(saved.Branches).Location.Id);
        saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(saved, []), default);
        Assert.Empty(saved.Branches); Assert.Empty(await f.Db.UserPreferredShopLocations.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Selections_can_be_reordered_and_removed_without_changing_branch_records()
    {
        await using var f = await Fixture.CreateAsync(); var a = await f.BranchAsync("a"); var b = await f.BranchAsync("b"); var c = await f.BranchAsync("c");
        var saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(await f.ViewAsync(), [new(1, a), new(1, b), new(1, c)]), default);
        saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(saved, [new(1, c), new(1, a)]), default);
        Assert.Equal(new[] { c, a }, saved.Branches.Select(x => x.Location.Id));
        Assert.Equal(new[] { 0, 1 }, await f.Db.UserPreferredShopLocations.OrderBy(x => x.DisplayOrder).Select(x => x.DisplayOrder).ToArrayAsync());
        Assert.Equal(3, await f.Db.ShopLocations.CountAsync()); Assert.Equal(3, await f.Db.ShopLocationReviews.CountAsync());
    }

    [PostgreSqlFact]
    public async Task Unavailable_selections_remain_identifiable_but_other_accounts_cannot_newly_select_them()
    {
        await using var f = await Fixture.CreateAsync(); var id = await f.BranchAsync("availability");
        var saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(await f.ViewAsync(), [new(1, id)]), default);
        foreach (var state in new[] { "retired", "future", "unverified", "inactive-retailer" })
        {
            var branch = await f.Db.ShopLocations.SingleAsync(l => l.Id == id);
            branch.IsActive = state != "retired";
            branch.VerifiedAt = state == "unverified" ? null : state == "future" ? f.Clock.Now.UtcDateTime.AddDays(1) : f.Clock.Now.UtcDateTime;
            branch.VerifiedByAccountId = state == "unverified" ? null : f.Admin.Id;
            branch.SourceReference = state == "unverified" ? null : "Private verification reference";
            branch.DirectoryKey = state == "unverified" ? null : "availability-key";
            await f.Db.Shops.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, state != "inactive-retailer"));
            await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
            var view = await f.ViewAsync(); var retained = Assert.Single(view.Branches);
            Assert.Equal(id, retained.Location.Id); Assert.Equal("Branch availability", retained.Location.Name);
            Assert.False(retained.IsAvailable); Assert.NotEmpty(retained.UnavailableReason!);
            // The old branch may be retained even when its parent chain is no longer offered.
            PreferredShopSelection[] shops = state == "inactive-retailer" ? [new(2, true)] : [new(1, true), new(2, false)];
            saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(view, [new(1, id)], shops), default);
            Assert.False(Assert.Single(saved.Branches).IsAvailable);
            var other = await f.Preferences.ReadAsync(f.Trial.Id, default);
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Preferences.SaveAsync(f.Trial.Id, Request(other, [new(1, id)], shops), default))).Status);
            Assert.Empty((await f.Preferences.ReadAsync(f.Trial.Id, default)).Branches);
        }
        saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(saved, [], [new(2, true)]), default);
        Assert.Empty(saved.Branches);
    }

    [PostgreSqlFact]
    public async Task Invalid_selections_are_rejected_atomically_without_changing_settings()
    {
        await using var f = await Fixture.CreateAsync(); var id = await f.BranchAsync("validation");
        var saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(await f.ViewAsync(), [new(1, id)]), default);
        foreach (var branches in new PreferredShopLocationSelection[][] {
            [new(0, id)], [new(1, 0)], [new(1, -1)], [null!], [new(2, id)], [new(1, long.MaxValue)],
            [new(1, id), new(1, id)], Enumerable.Range(1, 101).Select(n => new PreferredShopLocationSelection(1, n)).ToArray() })
        {
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Preferences.SaveAsync(f.Customer.Id,
                Request(saved, branches) with { IncludeDifferentBrands = true }, default))).Status);
            Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(await f.ViewAsync()));
        }
        var unconfigured = await f.BranchAsync("not-configured", 3);
        Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Preferences.SaveAsync(f.Customer.Id, Request(saved, [new(3, unconfigured)]), default))).Status);
        Assert.Equal(id, Assert.Single((await f.ViewAsync()).Branches).Location.Id);
    }

    [PostgreSqlFact]
    public async Task Trial_accounts_can_save_branches_and_expired_or_disabled_accounts_cannot_read_or_write()
    {
        await using var f = await Fixture.CreateAsync(); var id = await f.BranchAsync("trial");
        var trial = await f.Preferences.ReadAsync(f.Trial.Id, default);
        var saved = await f.Preferences.SaveAsync(f.Trial.Id, Request(trial, [new(1, id)]), default);
        Assert.Equal(id, Assert.Single(saved.Branches).Location.Id);
        await f.Db.UserAccounts.Where(a => a.Id == f.Trial.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresDate, f.Clock.Now.UtcDateTime.AddMinutes(-1)));
        Assert.Equal(401, (await Assert.ThrowsAsync<UserImportException>(() => f.Preferences.ReadAsync(f.Trial.Id, default))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<UserImportException>(() => f.Preferences.SaveAsync(f.Trial.Id, Request(saved, []), default))).Status);
        var customer = await f.ViewAsync();
        await f.Db.UserAccounts.Where(a => a.Id == f.Customer.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
        Assert.Equal(401, (await Assert.ThrowsAsync<UserImportException>(() => f.Preferences.ReadAsync(f.Customer.Id, default))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<UserImportException>(() => f.Preferences.SaveAsync(f.Customer.Id, Request(customer, [new(1, id)]), default))).Status);
    }

    [PostgreSqlFact]
    public async Task Account_mismatch_and_stale_settings_cannot_overwrite_branch_preferences()
    {
        await using var f = await Fixture.CreateAsync(); var id = await f.BranchAsync("conflict"); var before = await f.ViewAsync();
        var saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(before, [new(1, id)]), default);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Preferences.SaveAsync(f.Customer.Id, Request(before, []), default))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Preferences.SaveAsync(f.Trial.Id, Request(saved, []), default))).Status);
        Assert.Equal(id, Assert.Single((await f.ViewAsync()).Branches).Location.Id);
        Assert.Empty((await f.Preferences.ReadAsync(f.Trial.Id, default)).Branches);
    }

    [PostgreSqlFact]
    public async Task Concurrent_preference_updates_accept_only_one_current_version()
    {
        await using var f = await Fixture.CreateAsync(); var a = await f.BranchAsync("concurrent-a"); var b = await f.BranchAsync("concurrent-b");
        var view = await f.ViewAsync();
        async Task<int> Save(long id)
        {
            await using var db = PersistenceScope.Context();
            try { await f.Service(db).SaveAsync(f.Customer.Id, Request(view, [new(1, id)]), default); return 200; }
            catch (UserImportException error) { return error.Status; }
        }
        Assert.Equal(new[] { 200, 409 }, (await Task.WhenAll(Save(a), Save(b))).Order().ToArray());
        var final = await f.ViewAsync(); Assert.Single(final.Branches);
        Assert.Contains(final.Branches[0].Location.Id, new[] { a, b });
    }

    [PostgreSqlFact]
    public async Task Database_enforces_unique_ownership_branch_references_and_account_cleanup()
    {
        await using var f = await Fixture.CreateAsync(); var id = await f.BranchAsync("constraints");
        await f.Preferences.SaveAsync(f.Customer.Id, Request(await f.ViewAsync(), [new(1, id)]), default);
        f.Db.UserPreferredShopLocations.Add(new() { UserAccountId = f.Customer.Id, ShopLocationId = id });
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync()); f.Db.ChangeTracker.Clear();
        f.Db.UserPreferredShopLocations.Add(new() { UserAccountId = f.Trial.Id, ShopLocationId = long.MaxValue });
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync()); f.Db.ChangeTracker.Clear();
        f.Db.UserPreferredShopLocations.Add(new() { UserAccountId = f.Trial.Id, ShopLocationId = id, DisplayOrder = -1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync()); f.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => f.Db.ShopLocations.Where(l => l.Id == id).ExecuteDeleteAsync());
        await f.Db.UserAccounts.Where(a => a.Id == f.Customer.Id).ExecuteDeleteAsync();
        Assert.Empty(await f.Db.UserPreferredShopLocations.ToArrayAsync()); Assert.True(await f.Db.ShopLocations.AnyAsync(l => l.Id == id));
    }

    [PostgreSqlFact]
    public async Task Api_enforces_authentication_ownership_browser_protection_and_private_response_shapes()
    {
        await using var f = await Fixture.CreateAsync(); var id = await f.BranchAsync("api");
        var token = new string('d', 64); var otherToken = new string('e', 64);
        foreach (var pair in new[] { (f.Customer.Id, token), (f.Trial.Id, otherToken) })
            f.Db.UserSessions.Add(new() { UserAccountId = pair.Id, TokenHash = SessionToken.Hash(pair.Item2), CreatedDate = f.Clock.Now.UtcDateTime, ExpiresDate = f.Clock.Now.UtcDateTime.AddHours(2) });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        await using var app = new ContributionTestApp(f.Admin.Id); using var client = app.CreateClient();
        const string endpoint = "/api/account/shopping-preferences";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(endpoint)).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", "myshoppinglist_session=" + token);
        var request = Request(await f.ViewAsync(), [new(1, id)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(endpoint, request)).StatusCode);
        client.DefaultRequestHeaders.Add("X-MyShoppingList-Request", "1");
        using var saved = await client.PutAsJsonAsync(endpoint, request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode); Assert.True(saved.Headers.CacheControl?.NoStore);
        var body = (await saved.Content.ReadFromJsonAsync<ShoppingPreferencesResponse>())!;
        Assert.Equal(id, Assert.Single(body.Branches).Location.Id);
        var json = await saved.Content.ReadAsStringAsync();
        foreach (var secret in new[] { "sourceReference", "verifiedByAccountId", "reviewerAccountId", "private test evidence" }) Assert.DoesNotContain(secret, json);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        Assert.Empty((await client.GetFromJsonAsync<ShoppingPreferencesResponse>(endpoint))!.Branches);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(endpoint, Request(body, []))).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Migration_upgrade_and_rollback_preserve_existing_accounts_stores_branches_and_products()
    {
        await using var f = await Fixture.CreateAsync(); var id = await f.BranchAsync("migration");
        f.Db.Products.Add(new() { Name = "Existing product", CreatedDate = f.Clock.Now.UtcDateTime, UpdatedDate = f.Clock.Now.UtcDateTime });
        await f.Db.SaveChangesAsync();
        var saved = await f.Preferences.SaveAsync(f.Customer.Id, Request(await f.ViewAsync(), [new(1, id)]), default);
        var migrations = f.Db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, m => m.EndsWith("_AddPreferredShopLocations", StringComparison.Ordinal)); Assert.True(index > 0);
        var current = migrations[index]; var previous = migrations[index - 1];
        await f.Db.Database.ExecuteSqlRawAsync(f.Db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript());
        var migrator = f.Db.GetService<IMigrator>();
        await f.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(current, previous));
        await f.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(previous, current)); f.Db.ChangeTracker.Clear();
        var view = await f.ViewAsync(); Assert.Empty(view.Branches);
        Assert.Equal(saved.AccountId, view.AccountId); Assert.Equal(saved.Shops, view.Shops); Assert.Equal(saved.UpdatedDate, view.UpdatedDate);
        Assert.Equal(id, (await f.Db.ShopLocations.SingleAsync()).Id);
        Assert.Equal("Existing product", (await f.Db.Products.SingleAsync()).Name);
        Assert.Single(await f.Db.ShopLocationReviews.ToArrayAsync());
        Assert.False(f.Db.Database.HasPendingModelChanges());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IsolatedDatabaseScope isolation;
        public MyShoppingListDbContext Db { get; } = PersistenceScope.Context();
        public PersistenceClock Clock { get; } = new() { Now = DateTimeOffset.UtcNow };
        public UserAccount Admin { get; } = AdminAccountTests.TestAccount();
        public UserAccount Customer { get; } = AdminAccountTests.TestAccount();
        public UserAccount Trial { get; } = new() { IsTrial = true, DisplayName = "Branch trial", ExpiresDate = DateTime.UtcNow.AddHours(1), CreatedDate = DateTime.UtcNow, UpdatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        public ShoppingPreferencesService Preferences => Service(Db);
        public ShoppingPreferencesService Service(MyShoppingListDbContext db) => new(db, Clock, new RetailerProviderRegistry([]));
        private Fixture(IsolatedDatabaseScope isolation) => this.isolation = isolation;
        public static async Task<Fixture> CreateAsync()
        {
            var isolation = await IsolatedDatabaseScope.CreateAsync(); var f = new Fixture(isolation);
            try { f.Db.AddRange(f.Admin, f.Customer, f.Trial); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear(); return f; }
            catch { await f.DisposeAsync(); throw; }
        }
        public Task<ShoppingPreferencesResponse> ViewAsync() => Preferences.ReadAsync(Customer.Id, default);
        public async Task<long> BranchAsync(string code, long shop = 1)
        {
            var service = new ShopLocationService(Db, Clock, Options.Create(new AdminOptions { AccountIds = [Admin.Id] }));
            var created = await service.CreateAsync(Admin.Id, new(new(shop, code, "Branch " + code, code + " Example Street", null, "Adelaide", "SA", "5000", true), "Private verification reference", "private test evidence"), default);
            Db.ChangeTracker.Clear(); return created.Branch.Location.Id;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await isolation.DisposeAsync(); }
    }
}
