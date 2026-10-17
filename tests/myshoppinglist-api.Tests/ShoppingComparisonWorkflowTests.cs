using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Workers;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ShoppingComparisonWorkflowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [PostgreSqlFact]
    public Task Registered_customer_completes_comparison_workflow() => WorkflowAsync(false);

    [PostgreSqlFact]
    public Task Trial_customer_completes_comparison_workflow() => WorkflowAsync(true);

    private static async Task WorkflowAsync(bool trial)
    {
        await using var isolation = await IsolatedDatabaseScope.CreateAsync();
        await using var db = PersistenceScope.Context();
        var clock = new PersistenceClock { Now = new DateTimeOffset(2026, 10, 17, 4, 0, 0, TimeSpan.Zero) };
        var now = clock.Now.UtcDateTime;
        var customer = AdminAccountTests.TestAccount(); customer.IsTrial = trial;
        customer.ExpiresDate = trial ? now.AddDays(1) : null;
        if (trial) { customer.Email = null; customer.PasswordHash = null; customer.PasswordSalt = null; }
        var other = AdminAccountTests.TestAccount(); var admin = AdminAccountTests.TestAccount();
        db.UserAccounts.AddRange(customer, other, admin);
        Product Product(string brand, int size) => new() { Name = $"{brand} Original Coffee {size}g", Brand = brand,
            PackSize = size, PackUnit = "g", Category = "Drinks", SubCategory = "Ground coffee", CreatedDate = now, UpdatedDate = now };
        var original = Product("Acme", 500); var pack = Product("Acme", 1000); var brand = Product("Other", 1000);
        var list = new ShoppingList { UserAccount = customer, Name = "Workflow fixture", CreatedDate = now, UpdatedDate = now };
        var item = new ShoppingListProduct { ShoppingList = list, Product = original, Quantity = 2,
            Notes = "Keep my usual product", AddedDate = now, UpdatedDate = now };
        db.ShoppingListProducts.Add(item);
        ShopProduct Mapping(Product product, long shop, string code) => new() { Product = product, ShopId = shop,
            NameAtShop = product.Name, MatchType = MatchType.Exact, FirstFoundDate = now, LastFoundDate = now,
            ProductUrl = shop == 1 ? "https://www.coles.com.au/product/coffee-" + code
                : "https://www.woolworths.com.au/shop/productdetails/" + code };
        var coles = Mapping(original, 1, "111111"); var woolworths = Mapping(original, 2, "222222");
        ShopProductPrice Price(ShopProduct mapping, int amount) => new() { ShopProduct = mapping, Price = amount,
            PriceScope = PriceScope.National, SourceType = SourceType.RetailerPage, SourceUrl = mapping.ProductUrl + "?token=private-token",
            CheckedDate = now, CreatedDate = now, UpdatedDate = now };
        var unavailable = Price(woolworths, 12); unavailable.InStock = false;
        db.ShopProductPrices.AddRange(Price(coles, 10), unavailable,
            Price(Mapping(pack, 2, "333333"), 13), Price(Mapping(brand, 2, "444444"), 14));
        await db.SaveChangesAsync();
        var branch = new ShopLocation { ShopId = 1, Name = "Fixture Marion", Suburb = "Marion", State = "SA", Postcode = "5043",
            Address1 = "1 Fixture Street", DirectoryKey = "acceptance-branch", SourceReference = "private-evidence",
            VerifiedByAccountId = admin.Id, VerifiedAt = now, CreatedDate = now, UpdatedDate = now };
        var branchPrice = Price(coles, 1); branchPrice.ShopLocation = branch; branchPrice.PriceScope = PriceScope.StoreSpecific;
        db.ShopProductPrices.Add(branchPrice); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await using var app = new WorkflowApp(clock, admin.Id);
        using var client = await ClientAsync(app, db, clock, customer.Id);
        using var adminClient = await ClientAsync(app, db, clock, admin.Id);
        using var otherClient = await ClientAsync(app, db, clock, other.Id);
        using var anonymous = app.CreateClient();
        var planPath = $"/api/shopping-lists/{list.Id}/plan";
        var storePath = $"/api/shopping-lists/{list.Id}/in-store";
        var refreshPath = $"/api/shopping-lists/{list.Id}/refresh-prices";
        var productRefreshPath = $"/api/shopping-lists/{list.Id}/items/{item.Id}/refresh-prices";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(planPath)).StatusCode);
        var reviewPath = $"/api/admin/product-alternatives/{original.Id}/{brand.Id}";
        var context = (await adminClient.GetFromJsonAsync<BrandAlternativeReviewContext>(reviewPath))!;
        using var review = await adminClient.PutAsJsonAsync(reviewPath, new BrandAlternativeReviewRequest(true,
            "private-evidence: checked use, format, ingredients and dietary claims", 0, true, true,
            context.Product.Fingerprint, context.AlternativeProduct.Fingerprint));
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(reviewPath)).StatusCode);
        var directory = await ReadAsync<ShopLocationSearchResult>(client, "/api/shop-locations?shopId=1&search=Marion");
        Assert.Equal(branch.Id, Assert.Single(directory.Items).Id);
        var preferences = await ReadAsync<ShoppingPreferencesResponse>(client, "/api/account/shopping-preferences");
        var aldi = Assert.Single(preferences.AvailableShops, s => s.Code == "aldi");
        Assert.False(aldi.ComparisonSupported); Assert.Equal("Restricted", aldi.CapabilityStatus);

        async Task SaveAsync(bool sizes, bool brands, PreferredShopSelection[] shops)
        {
            using var response = await client.PutAsJsonAsync("/api/account/shopping-preferences", new ShoppingPreferencesUpdate(
                customer.Id, true, sizes, brands, shops, preferences.UpdatedDate, [new(1, branch.Id)]));
            preferences = await ResponseAsync<ShoppingPreferencesResponse>(response);
        }
        async Task<ShoppingListPlan> CompareAsync(int sizeCount, int brandCount)
        {
            var plan = await ReadAsync<ShoppingListPlan>(client, planPath);
            var detail = await ReadAsync<InStoreDetail>(client, storePath);
            var row = Assert.Single(plan.Items); var storeRow = Assert.Single(detail.Items);
            Assert.Equal(item.Id, row.Item.Id); Assert.Equal(2, row.Item.Quantity);
            Assert.Equal("Keep my usual product", row.Item.Notes);
            Assert.Equal(20, plan.Lowest.Subtotal); Assert.Equal(20, detail.SplitSubtotal);
            var price = Assert.Single(row.Prices, p => p.ShopId == 1);
            Assert.Equal(10, price.Price); Assert.True(price.IncludedInTotal);
            Assert.Equal(PriceScope.National, price.Observation!.PriceScope);
            Assert.Equal(coles.ProductUrl, price.Observation.SourceUrl);
            Assert.Equal(price.Observation, Assert.Single(storeRow.Prices, p => p.ShopId == 1).Observation);
            Assert.Equal(sizeCount, row.PackAlternatives!.Length); Assert.Equal(brandCount, row.BrandAlternatives!.Length);
            Assert.Equal(sizeCount, storeRow.PackAlternatives!.Length); Assert.Equal(brandCount, storeRow.BrandAlternatives!.Length);
            if (sizeCount > 0) Assert.Equal(10, row.PackAlternatives[0].Original!.Price);
            if (brandCount > 0) Assert.Equal(10, row.BrandAlternatives[0].Original!.Price);
            var savedBranch = Assert.Single(plan.StoreContext!.Branches!);
            Assert.Equal(branch.Id, savedBranch.Location.Id); Assert.True(savedBranch.IsAvailable);
            Assert.False(savedBranch.Location.BranchPricesSupported); Assert.True(savedBranch.SupermarketEnabled);
            Assert.Equal(savedBranch, Assert.Single(detail.StoreContext!.Branches!));
            return plan;
        }

        await SaveAsync(true, true, [new(1, true), new(2, false), new(3, true)]);
        var filtered = await CompareAsync(0, 0);
        Assert.DoesNotContain(filtered.Items[0].Prices, p => p.ShopId == 2);
        foreach (var (sizes, brands) in new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            await SaveAsync(sizes, brands, [new(1, true), new(2, true), new(3, false)]);
            var reloaded = await ReadAsync<ShoppingPreferencesResponse>(client, "/api/account/shopping-preferences");
            Assert.Equal(sizes, reloaded.IncludeDifferentPackSizes); Assert.Equal(brands, reloaded.IncludeDifferentBrands);
            Assert.Equal(branch.Id, Assert.Single(reloaded.Branches).Location.Id);
            var plan = await CompareAsync(sizes ? 1 : 0, brands ? 1 : 0);
            var missing = Assert.Single(plan.Items[0].Prices, p => p.ShopId == 2);
            Assert.Equal("Out of stock", missing.Status); Assert.False(missing.IncludedInTotal);
        }
        foreach (var path in new[] { productRefreshPath, refreshPath })
        {
            using var response = await client.PostAsJsonAsync(path, new { });
            Assert.Equal(new PriceRefreshResult(0, 0, 1, 0, 1), await ResponseAsync<PriceRefreshResult>(response));
        }
        Assert.False(await db.ColesExtensionTasks.AnyAsync());
        await db.ShopProductPrices.Where(p => p.ShopProductId == coles.Id && p.ShopLocationId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, now.AddDays(-1)).SetProperty(p => p.PriceScope, PriceScope.Unknown));
        using (var response = await client.PostAsJsonAsync(productRefreshPath, new { }))
            Assert.Equal(new PriceRefreshResult(1, 0, 0, 0, 1), await ResponseAsync<PriceRefreshResult>(response));
        using (var response = await client.PostAsJsonAsync(refreshPath, new { }))
            Assert.Equal(new PriceRefreshResult(0, 1, 0, 0, 1), await ResponseAsync<PriceRefreshResult>(response));
        var pending = await ReadAsync<ShoppingListPlan>(client, planPath);
        var retained = Assert.Single(pending.Items[0].Prices, p => p.ShopId == 1);
        Assert.Equal(now.AddDays(-1), retained.CheckedDate); Assert.Equal(PriceScope.Unknown, retained.Observation!.PriceScope);
        Assert.Equal("Waiting", retained.RefreshStatus); Assert.Equal(20, pending.Lowest.Subtotal);
        Assert.Single(await db.ColesExtensionTasks.ToArrayAsync());
        foreach (var path in new[] { planPath, storePath })
            Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsJsonAsync(refreshPath, new { })).StatusCode);
        var otherPreferences = await ReadAsync<ShoppingPreferencesResponse>(otherClient, "/api/account/shopping-preferences");
        Assert.Empty(otherPreferences.Branches);
        using (var forged = await otherClient.PutAsJsonAsync("/api/account/shopping-preferences", new ShoppingPreferencesUpdate(
            customer.Id, true, true, true, [new(1, true)], otherPreferences.UpdatedDate)))
            Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);

        await SaveAsync(true, true, [new(1, false), new(2, false), new(3, true)]);
        var restricted = await ReadAsync<ShoppingListPlan>(client, planPath);
        Assert.Equal(0, restricted.Lowest.Subtotal);
        Assert.DoesNotContain(restricted.Items[0].Prices, p => p.IncludedInTotal);
        Assert.Empty(restricted.Items[0].PackAlternatives!); Assert.Empty(restricted.Items[0].BrandAlternatives!);
        Assert.False(Assert.Single(restricted.StoreContext!.Branches!).SupermarketEnabled);
        using (var response = await client.PostAsJsonAsync(refreshPath, new { }))
            Assert.Equal(new PriceRefreshResult(0, 0, 0, 0, 0), await ResponseAsync<PriceRefreshResult>(response));
        Assert.Single(await db.ColesExtensionTasks.ToArrayAsync());
        Assert.False(await db.ProductImportJobs.AnyAsync()); Assert.False(await db.PackDiscoveryJobs.AnyAsync());
        var savedItem = await db.ShoppingListProducts.AsNoTracking().SingleAsync();
        Assert.Equal(2, savedItem.Quantity); Assert.Equal(item.Notes, savedItem.Notes);
        await db.UserAccounts.Where(a => a.Id == customer.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(planPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/shopping-preferences")).StatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return await ResponseAsync<T>(response);
    }
    private static async Task<T> ResponseAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var raw = await response.Content.ReadAsStringAsync();
        foreach (var field in new[] { "sourceReference", "verifiedByAccountId", "reviewerAccountId", "contributionObservationId", "private-evidence", "private-token" })
            Assert.DoesNotContain(field, raw);
        return JsonSerializer.Deserialize<T>(raw, Json)!;
    }
    private static async Task<HttpClient> ClientAsync(WorkflowApp app, MyShoppingListDbContext db, PersistenceClock clock, long accountId)
    {
        var token = SessionToken.Create();
        db.UserSessions.Add(new() { UserAccountId = accountId, TokenHash = SessionToken.Hash(token),
            CreatedDate = clock.Now.UtcDateTime, ExpiresDate = clock.Now.UtcDateTime.AddHours(2) });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        client.DefaultRequestHeaders.Add("X-Client-Timezone", "UTC");
        return client;
    }
    private sealed class WorkflowApp(PersistenceClock clock, long admin) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
                services.PostConfigure<AdminOptions>(o => o.AccountIds = [admin]);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
                // Tests drive every queue transition explicitly; no external collection.
                foreach (var registration in services.Where(s => s.ServiceType == typeof(IHostedService)
                    && s.ImplementationType?.Namespace == typeof(PriceRefreshWorker).Namespace).ToArray()) services.Remove(registration);
            });
        }
    }
}
