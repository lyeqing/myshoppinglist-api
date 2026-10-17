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
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ShopLocationTests
{
    private static ShopLocationDetails Details(string code = "test-branch-1", string address = "10 Example Street", long shop = 1) =>
        new(shop, code, "Example branch", address, null, "Marion", "SA", "5043", true);
    private static ShopLocationCreateRequest Create(ShopLocationDetails? details = null) =>
        new(details ?? Details(), "Manual directory reference 123", "Address, retailer code and operating status reviewed manually.");
    private static ShopLocationUpdateRequest Update(ShopLocationAdminDetail branch, ShopLocationDetails details) =>
        new(details, "Manual directory reference 456", "Corrected or reconfirmed details after review.", branch.Branch.UpdatedDate);

    [PostgreSqlFact]
    public async Task Reviewed_create_correct_retire_and_reactivate_keep_identity_and_private_history()
    {
        await using var f = await Fixture.CreateAsync();
        var created = await f.Service.CreateAsync(f.Admin.Id, Create(), default);
        var id = created.Branch.Location.Id;
        Assert.Equal(f.Admin.Id, created.Branch.VerifiedByAccountId);
        Assert.Equal(f.Clock.Now.UtcDateTime, created.Branch.Location.VerifiedAt);
        var first = Assert.Single(created.Reviews);
        Assert.Equal("Created", first.Action); Assert.Null(first.BeforeJson);
        Assert.Equal(f.Admin.Id, first.ReviewerAccountId);
        var summary = Assert.Single((await f.Service.SearchAsync(f.Customer.Id, 1, "marion", default)).Items);
        Assert.Equal(id, summary.Id); Assert.False(summary.BranchPricesSupported);
        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("Manual directory", json); Assert.DoesNotContain("ReviewerAccountId", json); Assert.DoesNotContain("VerifiedByAccountId", json);
        Assert.Equal(summary, await f.Service.FindAsync(f.Customer.Id, id, default));
        var corrected = await f.Service.UpdateAsync(f.Admin.Id, id, Update(created, Details() with { Name = "Corrected branch" }), default);
        Assert.Equal(id, corrected.Branch.Location.Id); Assert.Equal(created.Branch.CreatedDate, corrected.Branch.CreatedDate);
        Assert.True(corrected.Branch.UpdatedDate > created.Branch.UpdatedDate);
        Assert.Equal("Updated", corrected.Reviews[0].Action);
        Assert.Contains("Example branch", corrected.Reviews[0].BeforeJson!);
        Assert.Contains("Corrected branch", corrected.Reviews[0].AfterJson);
        var retired = await f.Service.UpdateAsync(f.Admin.Id, id, Update(corrected, Details() with { Name = "Corrected branch", IsActive = false }), default);
        Assert.Equal("Retired", retired.Reviews[0].Action);
        Assert.Empty((await f.Service.SearchAsync(f.Customer.Id, 1, null, default)).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.FindAsync(f.Customer.Id, id, default))).Status);
        Assert.False(Assert.Single((await f.Service.SearchAdminAsync(f.Admin.Id, 1, null, default)).Items).IsActive);
        var restored = await f.Service.UpdateAsync(f.Admin.Id, id, Update(retired, Details()), default);
        Assert.Equal("Reactivated", restored.Reviews[0].Action); Assert.Equal(4, restored.Reviews.Length);
        Assert.Equal(id, Assert.Single((await f.Service.SearchAsync(f.Customer.Id, 1, null, default)).Items).Id);
        Assert.Equal(1, await f.Db.ShopLocations.CountAsync());
        Assert.False(await f.Db.ProductImportJobs.AnyAsync()); Assert.False(await f.Db.ColesExtensionTasks.AnyAsync());
        Assert.False(await f.Db.ShopProductPrices.AnyAsync());
    }

    [PostgreSqlFact]
    public async Task Search_excludes_unverified_retired_inactive_and_future_records_but_admin_can_inspect_them()
    {
        await using var f = await Fixture.CreateAsync();
        var active = await f.Service.CreateAsync(f.Admin.Id, Create(), default);
        await f.Service.CreateAsync(f.Admin.Id, Create(Details("retired", "20 Example Street") with { IsActive = false }), default);
        await f.Service.CreateAsync(f.Admin.Id, Create(Details("other-chain", "30 Example Street", 2)), default);
        var legacy = new ShopLocation { ShopId = 1, Name = "Unverified legacy", StoreCode = "legacy", Suburb = "Marion", Postcode = "5043", CreatedDate = f.Clock.Now.UtcDateTime, UpdatedDate = f.Clock.Now.UtcDateTime };
        f.Db.ShopLocations.Add(legacy); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        Assert.Equal(active.Branch.Location.Id, Assert.Single((await f.Service.SearchAsync(f.Customer.Id, 1, null, default)).Items).Id);
        Assert.Equal(2, (await f.Service.SearchAsync(f.Customer.Id, null, "MARION", default)).Items.Length);
        Assert.Equal(2, (await f.Service.SearchAsync(f.Customer.Id, null, "5043", default)).Items.Length);
        Assert.Single((await f.Service.SearchAsync(f.Customer.Id, null, "coles", default)).Items);
        Assert.Equal(3, (await f.Service.SearchAdminAsync(f.Admin.Id, 1, null, default)).Items.Length);
        Assert.Empty((await f.Service.ReadAdminAsync(f.Admin.Id, legacy.Id, default)).Reviews);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.FindAsync(f.Customer.Id, legacy.Id, default))).Status);
        await f.Db.Shops.Where(s => s.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(s => s.IsActive, false));
        Assert.Single((await f.Service.SearchAsync(f.Customer.Id, null, "marion", default)).Items);
        Assert.Single((await f.Service.SearchAdminAsync(f.Admin.Id, 2, null, default)).Items);
        await f.Db.ShopLocations.Where(l => l.Id == active.Branch.Location.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.VerifiedAt, f.Clock.Now.UtcDateTime.AddMinutes(1)));
        Assert.Empty((await f.Service.SearchAsync(f.Customer.Id, 1, null, default)).Items);
    }

    [PostgreSqlFact]
    public async Task Duplicate_codes_addresses_and_stale_or_identity_changes_cannot_rewrite_a_branch()
    {
        await using var f = await Fixture.CreateAsync();
        var original = await f.Service.CreateAsync(f.Admin.Id, Create(), default);
        foreach (var duplicate in new[] { Details("TEST-BRANCH-1", "50 Other Road"), Details("different-code", "10 example street") with { Suburb = "MARION" } })
            Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.CreateAsync(f.Admin.Id, Create(duplicate), default))).Status);
        await f.Service.CreateAsync(f.Admin.Id, Create(Details("different-retailer", shop: 2)), default);
        var updated = await f.Service.UpdateAsync(f.Admin.Id, original.Branch.Location.Id, Update(original, Details() with { Name = "Revised" }), default);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.UpdateAsync(f.Admin.Id, original.Branch.Location.Id, Update(original, Details()), default))).Status);
        foreach (var changed in new[] { Details() with { ShopId = 2 }, Details() with { StoreCode = "changed-identity" } })
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.UpdateAsync(f.Admin.Id, original.Branch.Location.Id, Update(updated, changed), default))).Status);
        Assert.Equal(2, (await f.Service.ReadAdminAsync(f.Admin.Id, original.Branch.Location.Id, default)).Reviews.Length);
        var retired = await f.Service.UpdateAsync(f.Admin.Id, original.Branch.Location.Id, Update(updated, Details() with { IsActive = false }), default);
        var replacement = await f.Service.CreateAsync(f.Admin.Id, Create(Details("corrected-identity")), default);
        Assert.NotEqual(retired.Branch.Location.Id, replacement.Branch.Location.Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.UpdateAsync(f.Admin.Id, retired.Branch.Location.Id, Update(retired, Details()), default))).Status);

    }

    [PostgreSqlFact]
    public async Task Invalid_details_evidence_sources_and_timestamps_leave_no_partial_writes()
    {
        await using var f = await Fixture.CreateAsync();
        var good = Details();
        foreach (var details in new[] { good with { Name = " " }, good with { Name = new string('a', 201) }, good with { StoreCode = null },
            good with { Address1 = "" }, good with { Suburb = null }, good with { State = "XX" }, good with { Postcode = "50A3" },
            good with { Postcode = "123" }, good with { ShopId = -1 }, good with { ShopId = long.MaxValue }, good with { Name = "Bad\nName" } })
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.CreateAsync(f.Admin.Id, Create(details), default))).Status);
        foreach (var request in new[] { Create() with { Details = null }, Create() with { SourceReference = " " }, Create() with { SourceReference = new string('a', 1001) },
            Create() with { Evidence = " " }, Create() with { Evidence = new string('a', 2001) } })
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.CreateAsync(f.Admin.Id, request, default))).Status);
        Assert.False(await f.Db.ShopLocations.AnyAsync()); Assert.False(await f.Db.ShopLocationReviews.AnyAsync());
        var created = await f.Service.CreateAsync(f.Admin.Id, Create(), default);
        foreach (var expected in new[] { default(DateTime), DateTime.SpecifyKind(created.Branch.UpdatedDate, DateTimeKind.Unspecified) })
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.UpdateAsync(f.Admin.Id, created.Branch.Location.Id, Update(created, Details()) with { ExpectedUpdatedDate = expected }, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.UpdateAsync(f.Admin.Id, long.MaxValue, Update(created, Details()), default))).Status);
        Assert.Single(await f.Db.ShopLocationReviews.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Legacy_identity_address_and_price_associations_survive_manual_verification()
    {
        await using var f = await Fixture.CreateAsync();
        var legacy = await f.LegacyWithPriceAsync();
        Assert.Empty((await f.Service.SearchAsync(f.Customer.Id, 1, "Marion", default)).Items);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.CreateAsync(f.Admin.Id, Create(Details("different-code")), default))).Status);
        var before = await f.Service.ReadAdminAsync(f.Admin.Id, legacy.Id, default);
        var verified = await f.Service.UpdateAsync(f.Admin.Id, legacy.Id, Update(before, Details("legacy-code")), default);
        Assert.Equal(legacy.Id, verified.Branch.Location.Id);
        var price = await f.Db.ShopProductPrices.SingleAsync(); Assert.Equal(legacy.Id, price.ShopLocationId); Assert.Equal(12.34m, price.Price);
        var saved = await f.Db.ShopLocations.AsNoTracking().SingleAsync();
        Assert.Equal(legacy.Latitude, saved.Latitude); Assert.Equal(legacy.Longitude, saved.Longitude);
        Assert.Equal(legacy.CreatedDate, saved.CreatedDate);
        Assert.Equal(1, await f.Db.ShopProductPriceHistory.CountAsync());
        var retired = await f.Service.UpdateAsync(f.Admin.Id, legacy.Id, Update(verified, Details("legacy-code") with { IsActive = false }), default);
        Assert.Equal("Retired", retired.Reviews[0].Action);
        Assert.Equal(12.34m, (await f.Db.ShopProductPrices.SingleAsync()).Price);
        Assert.False((await f.Db.ShopLocations.AsNoTracking().SingleAsync()).IsActive);
    }

    [PostgreSqlFact]
    public async Task Concurrent_create_and_update_requests_have_one_winner()
    {
        await using var f = await Fixture.CreateAsync();
        async Task<int> CreateOnce()
        {
            await using var db = PersistenceScope.Context();
            try { await f.ServiceFor(db).CreateAsync(f.Admin.Id, Create(), default); return 201; }
            catch (UserImportException error) { return error.Status; }
        }
        Assert.Equal(new[] { 201, 409 }, (await Task.WhenAll(CreateOnce(), CreateOnce())).Order().ToArray());
        var branch = await f.Service.ReadAdminAsync(f.Admin.Id, (await f.Db.ShopLocations.SingleAsync()).Id, default);
        async Task<int> UpdateOnce(string name)
        {
            await using var db = PersistenceScope.Context();
            try { await f.ServiceFor(db).UpdateAsync(f.Admin.Id, branch.Branch.Location.Id, Update(branch, Details() with { Name = name }), default); return 200; }
            catch (UserImportException error) { return error.Status; }
        }
        Assert.Equal(new[] { 200, 409 }, (await Task.WhenAll(UpdateOnce("First"), UpdateOnce("Second"))).Order().ToArray());
        Assert.Equal(2, await f.Db.ShopLocationReviews.CountAsync());
    }

    [PostgreSqlFact]
    public async Task Searches_are_bounded_literal_and_authorization_checks_trial_expiry_and_suspension()
    {
        await using var f = await Fixture.CreateAsync();
        var branch = await f.Service.CreateAsync(f.Admin.Id, Create(), default);
        Assert.Single((await f.Service.SearchAsync(f.Trial.Id, 1, null, default)).Items);
        Assert.Empty((await f.Service.SearchAsync(f.Customer.Id, 1, "%_", default)).Items);
        foreach (var filter in new (long? Shop, string? Search)[] { (null, null), (null, "x"), (0, "Marion"), (1, new string('x', 101)), (1, "Ma\nrion"), (long.MaxValue, null) })
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.SearchAsync(f.Customer.Id, filter.Shop, filter.Search, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.CreateAsync(f.Customer.Id, Create(), default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.ReadAdminAsync(f.Trial.Id, branch.Branch.Location.Id, default))).Status);
        await f.Db.UserAccounts.Where(a => a.Id == f.Trial.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresDate, f.Clock.Now.UtcDateTime.AddMinutes(-1)));
        Assert.Equal(401, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.SearchAsync(f.Trial.Id, 1, null, default))).Status);
        await f.Db.UserAccounts.Where(a => a.Id == f.Customer.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
        Assert.Equal(401, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.FindAsync(f.Customer.Id, branch.Branch.Location.Id, default))).Status);
        var stored = await f.Db.ShopLocations.AsNoTracking().SingleAsync();
        f.Db.ShopLocations.AddRange(Enumerable.Range(0, 55).Select(i => new ShopLocation
        {
            ShopId = 1,
            StoreCode = "bounded-" + i,
            Name = "Bounded " + i.ToString("D2"),
            Suburb = "Marion",
            State = "SA",
            Postcode = "5043",
            Address1 = i + " Bound Street",
            SourceReference = "Test source",
            VerifiedByAccountId = f.Admin.Id,
            VerifiedAt = stored.VerifiedAt,
            DirectoryKey = "key-" + i,
            CreatedDate = f.Clock.Now.UtcDateTime,
            UpdatedDate = f.Clock.Now.UtcDateTime
        }));
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var result = await f.Service.SearchAsync(f.Admin.Id, 1, "bounded", default);
        Assert.Equal(50, result.Items.Length); Assert.True(result.HasMore); Assert.Equal("Bounded 00", result.Items[0].Name);
    }

    [PostgreSqlFact]
    public async Task Endpoints_enforce_roles_browser_protection_required_fields_and_private_response_shapes()
    {
        await using var f = await Fixture.CreateAsync();
        var admin = new string('a', 64); var customer = new string('b', 64);
        f.Db.UserSessions.AddRange(Session(f.Admin.Id, admin), Session(f.Customer.Id, customer)); await f.Db.SaveChangesAsync();
        await using var app = new ContributionTestApp(f.Admin.Id); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/shop-locations?shopId=1")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", customer);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/shop-locations", Create())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/shop-locations?shopId=1")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var created = await client.PostAsJsonAsync("/api/admin/shop-locations", Create());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.True(created.Headers.CacheControl?.NoStore);
        var body = (await created.Content.ReadFromJsonAsync<ShopLocationAdminDetail>())!;
        Assert.Equal(f.Admin.Id, body.Reviews[0].ReviewerAccountId);
        Assert.Equal($"/api/admin/shop-locations/{body.Branch.Location.Id}", created.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/shop-locations", new { details = new { shopId = 1, name = "Missing active flag" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/shop-locations?shopId=1&search=Marion&search=SA")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", customer);
        using var read = await client.GetAsync("/api/shop-locations?shopId=1");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.True(read.Headers.CacheControl?.NoStore);
        var json = await read.Content.ReadAsStringAsync(); Assert.DoesNotContain("sourceReference", json); Assert.DoesNotContain("reviewerAccountId", json);
        Assert.False(Assert.Single((await read.Content.ReadFromJsonAsync<ShopLocationSearchResult>())!.Items).BranchPricesSupported);
        using var browser = app.CreateClient(); browser.DefaultRequestHeaders.Add("Cookie", $"myshoppinglist_session={admin}");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsJsonAsync("/api/admin/shop-locations", Create(Details("new", "99 Example Street")))).StatusCode);
        browser.DefaultRequestHeaders.Add("X-MyShoppingList-Request", "1");
        Assert.Equal(HttpStatusCode.Created, (await browser.PostAsJsonAsync("/api/admin/shop-locations", Create(Details("new", "99 Example Street")))).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Migration_preserves_legacy_branches_and_price_history_through_upgrade_and_rollback()
    {
        await using var f = await Fixture.CreateAsync();
        var legacy = await f.LegacyWithPriceAsync();
        var migrations = f.Db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, m => m.EndsWith("_AddVerifiedShopLocations", StringComparison.Ordinal));
        Assert.True(index > 0);
        var previous = migrations[index - 1]; var current = migrations[index];
        await f.Db.Database.ExecuteSqlRawAsync(f.Db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript());
        var migrator = f.Db.GetService<IMigrator>();
        await f.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(current, previous));
        await f.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(previous, current));
        f.Db.ChangeTracker.Clear();
        var after = await f.Db.ShopLocations.AsNoTracking().SingleAsync();
        Assert.Equal(legacy.Id, after.Id); Assert.Equal(legacy.StoreCode, after.StoreCode); Assert.Equal(legacy.CreatedDate, after.CreatedDate);
        Assert.Null(after.VerifiedAt); Assert.Null(after.SourceReference); Assert.Null(after.VerifiedByAccountId);
        Assert.Empty((await f.Service.SearchAsync(f.Customer.Id, 1, null, default)).Items);
        Assert.Equal(12.34m, (await f.Db.ShopProductPrices.SingleAsync()).Price);
        Assert.Equal(legacy.Id, (await f.Db.ShopProductPriceHistory.SingleAsync()).ShopLocationId);
        await f.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(current, previous));
        await f.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(previous, current));
        Assert.Equal(legacy.Id, (await f.Db.ShopLocations.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(await f.Db.ShopLocationReviews.ToArrayAsync());
    }

    private static UserSession Session(long id, string token) => new() { UserAccountId = id, TokenHash = SessionToken.Hash(token), CreatedDate = DateTime.UtcNow, ExpiresDate = DateTime.UtcNow.AddHours(1) };
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IsolatedDatabaseScope isolation;
        public MyShoppingListDbContext Db { get; }
        public PersistenceClock Clock { get; } = new() { Now = new DateTimeOffset(2026, 10, 17, 1, 0, 0, TimeSpan.Zero) };
        public UserAccount Admin { get; private set; } = null!;
        public UserAccount Customer { get; private set; } = null!;
        public UserAccount Trial { get; private set; } = null!;
        public ShopLocationService Service => ServiceFor(Db);
        public ShopLocationService ServiceFor(MyShoppingListDbContext db) => new(db, Clock, Options.Create(new AdminOptions { AccountIds = [Admin.Id] }));
        private Fixture(IsolatedDatabaseScope isolation) { this.isolation = isolation; Db = PersistenceScope.Context(); }
        public static async Task<Fixture> CreateAsync()
        {
            var isolation = await IsolatedDatabaseScope.CreateAsync();
            var f = new Fixture(isolation);
            try
            {
                f.Admin = AdminAccountTests.TestAccount(); f.Customer = AdminAccountTests.TestAccount();
                f.Trial = new() { IsTrial = true, DisplayName = "Trial", ExpiresDate = f.Clock.Now.UtcDateTime.AddHours(1), CreatedDate = f.Clock.Now.UtcDateTime, UpdatedDate = f.Clock.Now.UtcDateTime };
                f.Db.AddRange(f.Admin, f.Customer, f.Trial); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear(); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<ShopLocation> LegacyWithPriceAsync()
        {
            var now = Clock.Now.UtcDateTime;
            var branch = new ShopLocation { ShopId = 1, StoreCode = "legacy-code", Name = "Legacy branch", Address1 = "10 Example Street", Suburb = "Marion", State = "SA", Postcode = "5043", Latitude = -35m, Longitude = 138.5m, CreatedDate = now, UpdatedDate = now };
            var product = new Product { Name = "Branch test product", CreatedDate = now, UpdatedDate = now };
            var mapping = new ShopProduct { Product = product, ShopId = 1, NameAtShop = product.Name, ProductUrl = "https://www.coles.com.au/product/test", MatchType = MatchType.Exact, FirstFoundDate = now, LastFoundDate = now };
            Db.ShopProductPrices.Add(new() { ShopProduct = mapping, ShopLocation = branch, Price = 12.34m, Currency = "AUD", PriceScope = PriceScope.StoreSpecific, SourceUrl = mapping.ProductUrl, CheckedDate = now, CreatedDate = now, UpdatedDate = now });
            Db.ShopProductPriceHistory.Add(new() { ShopProduct = mapping, ShopLocation = branch, Price = 12.34m, Currency = "AUD", PriceScope = PriceScope.StoreSpecific, SourceUrl = mapping.ProductUrl, CheckedDate = now, CreatedDate = now });
            await Db.SaveChangesAsync(); Db.ChangeTracker.Clear(); return branch;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await isolation.DisposeAsync(); }
    }
}
