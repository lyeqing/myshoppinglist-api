using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ShoppingListSubstitutionTests
{
    [PostgreSqlFact]
    public async Task Alternatives_are_visible_but_only_explicit_store_choices_enter_totals_and_undo_restores_original()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.List.ItemAsync(); var originalId = before.Product.Id;
        var plan = (await f.List.Service.PlanAsync(f.Account, f.List.Scope.ListId, default))!;
        var coles = plan.Retailers.Single(b => b.ShopId == 1);
        Assert.Equal(0, coles.Subtotal); Assert.Equal(1, coles.NeedsChoiceCount);
        Assert.Equal("Alternative available — choose a product", Assert.Single(coles.MissingItems).Reason);
        var alternative = Assert.Single(plan.Items[0].BrandAlternatives!, a => a.Offer.ShopId == 1);
        Assert.Equal(3, alternative.Original!.ShopId); Assert.Equal(2.50m, alternative.Original.Price);
        await f.Choose(1, f.Coles.Id, 3);
        plan = (await f.List.Service.PlanAsync(f.Account, f.List.Scope.ListId, default))!;
        coles = plan.Retailers.Single(b => b.ShopId == 1);
        Assert.Equal(9m, coles.Subtotal); Assert.Equal(0, coles.NeedsChoiceCount); Assert.Equal(1, coles.SubstitutionCount);
        Assert.Equal(originalId, plan.Items[0].Item.Product.Id); Assert.Equal(before.Quantity, plan.Items[0].Item.Quantity);
        Assert.Equal(before.Notes, plan.Items[0].Item.Notes);
        var price = Assert.Single(plan.Items[0].Prices, p => p.ShopId == 1);
        Assert.Equal(f.Coles.Id, price.Substitution!.Product.Id); Assert.Equal(3, price.QuantityPrice!.Quantity);
        Assert.Equal(0, plan.Retailers.Single(b => b.ShopId == 2).Subtotal);
        var detail = (await f.InStore.DetailAsync(f.Account, f.List.Scope.ListId, default))!;
        Assert.Equal(9m, detail.Baskets.Single(b => b.ShopId == 1).Subtotal); Assert.Equal(0, detail.ComparableCount);
        await f.Choose(2, f.Woolworths.Id, 2);
        detail = (await f.InStore.DetailAsync(f.Account, f.List.Scope.ListId, default))!;
        Assert.Equal(9m, detail.Baskets.Single(b => b.ShopId == 1).Subtotal);
        Assert.Equal(8m, detail.Baskets.Single(b => b.ShopId == 2).Subtotal);
        Assert.Equal(8m, detail.SplitSubtotal); Assert.Equal(0, detail.ComparableCount);
        await f.Choose(1, null, 1);
        plan = (await f.List.Service.PlanAsync(f.Account, f.List.Scope.ListId, default))!;
        Assert.Equal(0, plan.Retailers.Single(b => b.ShopId == 1).Subtotal);
        Assert.Equal(8m, plan.Retailers.Single(b => b.ShopId == 2).Subtotal);
        Assert.Equal(originalId, plan.Items[0].Item.Product.Id); Assert.Single(plan.Items[0].Substitutions!);
    }

    [PostgreSqlFact]
    public async Task Invalid_or_unowned_choices_stale_prices_and_old_versions_are_rejected()
    {
        await using var f = await Fixture.CreateAsync(); var item = await f.List.ItemAsync();
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.ChangeAsync(-1, f.List.Scope.ListId, item.Id, 1, new(-1, f.Coles.Id, 1, item.UpdatedDate), default))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.ChangeAsync(f.Account, f.List.Scope.ListId, item.Id, 1, new(-1, f.Coles.Id, 1, item.UpdatedDate), default))).Status);
        foreach (var q in new[] { 0, -1, 1000 }) Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Choose(1, f.Coles.Id, q))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Choose(1, f.Woolworths.Id, 1))).Status);
        await f.Choose(1, f.Coles.Id, 1);
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.ChangeAsync(f.Account, f.List.Scope.ListId, item.Id, 1, new(f.Account, null, 1, item.UpdatedDate), default))).Status);
        await f.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == f.Woolworths.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.List.Scope.Clock.Now.UtcDateTime.AddDays(-30)));
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Choose(2, f.Woolworths.Id, 1))).Status);
        Assert.Null(await f.List.Service.PlanAsync(-1, f.List.Scope.ListId, default));
    }

    [PostgreSqlFact]
    public async Task Stale_or_changed_selected_products_are_excluded_and_choice_can_still_be_undone()
    {
        await using var f = await Fixture.CreateAsync(); await f.Choose(1, f.Coles.Id, 2);
        await f.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == f.Coles.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.List.Scope.Clock.Now.UtcDateTime.AddDays(-30)));
        var plan = (await f.List.Service.PlanAsync(f.Account, f.List.Scope.ListId, default))!;
        Assert.Equal(0, plan.Retailers.Single(b => b.ShopId == 1).Subtotal);
        Assert.Equal("Stale price", Assert.Single(plan.Items[0].Substitutions!).Status);
        await f.Db.Products.Where(p => p.Id == f.Coles.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ItemDetail, "Changed formulation"));
        plan = (await f.List.Service.PlanAsync(f.Account, f.List.Scope.ListId, default))!;
        Assert.Equal("Choice needs review", Assert.Single(plan.Items[0].Substitutions!).Status);
        await f.Db.UserAccounts.Where(a => a.Id == f.Account).ExecuteUpdateAsync(s => s.SetProperty(a => a.IncludeDifferentBrands, false));
        await f.Choose(1, null, 1); Assert.Empty(await f.Db.ShoppingListItemSubstitutions.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Retailers_with_only_suggestions_can_be_selected_and_multibuy_uses_alternative_package_count()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.UserAccounts.Where(a => a.Id == f.Account).ExecuteUpdateAsync(s => s.SetProperty(a => a.CompareOnlyPreferredShops, false));
        var list = Assert.Single(await f.InStore.ListsAsync(f.Account, default)); Assert.Contains(list.Retailers, s => s.Id == 1);
        var detail = (await f.InStore.DetailAsync(f.Account, list.Id, default))!; Assert.Contains(detail.Retailers, s => s.Id == 1);
        await f.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == f.Coles.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.SpecialType, "MULTI_SAVE").SetProperty(p => p.SpecialDescription, "2 for $5"));
        await f.Choose(1, f.Coles.Id, 3);
        var plan = (await f.List.Service.PlanAsync(f.Account, list.Id, default))!;
        Assert.Equal(8m, plan.Retailers.Single(b => b.ShopId == 1).Subtotal);
        Assert.Equal(8m, (await f.InStore.DetailAsync(f.Account, list.Id, default))!.Baskets.Single(b => b.ShopId == 1).Subtotal);
    }

    [PostgreSqlFact]
    public async Task Hiding_suggestions_preserves_explicit_choices_and_totals()
    {
        await using var f = await Fixture.CreateAsync(); await f.Choose(1, f.Coles.Id, 2);
        await f.Db.UserAccounts.Where(a => a.Id == f.Account).ExecuteUpdateAsync(s => s.SetProperty(a => a.IncludeDifferentBrands, false));
        var plan = (await f.List.Service.PlanAsync(f.Account, f.List.Scope.ListId, default))!;
        Assert.Empty(plan.Items[0].BrandAlternatives!); Assert.Equal("Fresh", Assert.Single(plan.Items[0].Substitutions!).Status);
        Assert.Equal(6m, plan.Retailers.Single(b => b.ShopId == 1).Subtotal);
        await f.Choose(1, null, 1); Assert.Empty(await f.Db.ShoppingListItemSubstitutions.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Migration_round_trips_and_parent_item_deletion_removes_choices()
    {
        await using var f = await Fixture.CreateAsync(); var before = await f.List.ItemAsync();
        var migrations = f.Db.Database.GetMigrations().ToArray(); var index = Array.FindIndex(migrations, m => m.EndsWith("_AddShoppingListItemSubstitutions"));
        Assert.True(index > 0); var history = f.Db.GetService<IHistoryRepository>();
        await f.Db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript()); var migrator = f.Db.GetService<IMigrator>();
        await f.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(migrations[index], migrations[index - 1]));
        await f.Db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(migrations[index - 1], migrations[index]));
        Assert.Equal(before, await f.List.ItemAsync()); await f.Choose(1, f.Coles.Id, 1);
        await f.Db.ShoppingListProducts.Where(i => i.Id == before.Id).ExecuteDeleteAsync();
        Assert.Empty(await f.Db.ShoppingListItemSubstitutions.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Endpoint_requires_session_browser_protection_and_explicit_version()
    {
        await using var f = await Fixture.CreateAsync(); await using var app = new ApiFactory(f); using var client = await app.ClientAsync();
        using var anonymous = app.CreateClient(); var path = $"/api/shopping-lists/{f.List.Scope.ListId}/items/{f.List.ItemId}/substitutions/1";
        var item = await f.List.ItemAsync(); var request = new ShoppingListSubstitutionRequest(f.Account, f.Coles.Id, 2, item.UpdatedDate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, request)).StatusCode);
        client.DefaultRequestHeaders.Remove(CookieRequestProtection.HeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, request)).StatusCode);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new {})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, request with { ExpectedAccountId = -1 })).StatusCode);
        var response = await client.PutAsJsonAsync(path, request); Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, request)).StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IsolatedDatabaseScope isolated;
        public ListFixture List { get; }
        public MyShoppingListDbContext Db => List.Scope.Db;
        public long Account => List.Scope.UserId;
        public Product Coles { get; private set; } = null!;
        public Product Woolworths { get; private set; } = null!;
        public ShoppingListSubstitutionService Service => new(Db, List.Scope.Clock);
        public InStoreShoppingService InStore => new(Db, List.Scope.Clock, new());
        private Fixture(IsolatedDatabaseScope isolated, ListFixture list) { this.isolated = isolated; List = list; }
        public static async Task<Fixture> CreateAsync()
        {
            var isolated = await IsolatedDatabaseScope.CreateAsync(); var f = new Fixture(isolated, await ListFixture.CreateAsync());
            var now = f.List.Scope.Clock.Now.UtcDateTime; var item = await f.Db.ShoppingListProducts.Include(i => i.Product).SingleAsync(i => i.Id == f.List.ItemId);
            item.Product.Name = "Bakers Life Light Rye Bread 680g"; item.Product.Brand = "Bakers Life"; item.Product.Variant = null;
            item.Product.PackSize = 680; item.Product.PackUnit = "g"; item.Product.PackQuantity = 1;
            var original = item.Product; await f.Db.ShopProducts.Where(m => m.ProductId == original.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false));
            var account = await f.Db.UserAccounts.SingleAsync(a => a.Id == f.Account); account.IncludeDifferentBrands = true; account.CompareOnlyPreferredShops = true;
            foreach (var shop in new[] { 1L, 2L, 3L }) f.Db.UserPreferredShops.Add(new() { UserAccountId = f.Account, ShopId = shop, IsEnabled = true });
            var originalMapping = new ShopProduct { Product = original, ShopId = 3, ShopProductCode = "400367", NameAtShop = original.Name, ProductUrl = "https://www.aldi.com.au/product/light-rye-bread-400367", MatchType = MatchType.Exact, IsActive = true, FirstFoundDate = now, LastFoundDate = now };
            f.Db.ShopProducts.Add(originalMapping); await f.Db.SaveChangesAsync();
            f.Db.ShopProductPrices.Add(new() { ShopProductId = originalMapping.Id, Price = 2.50m, Currency = "AUD", InStock = true, CheckedDate = now, CreatedDate = now, UpdatedDate = now, SourceType = SourceType.BrowserExtension, PriceScope = PriceScope.Unknown, SourceUrl = originalMapping.ProductUrl });
            foreach (var shop in new[] { 1L, 2L })
            {
                var p = new Product { Name = $"Other{shop} Light Rye Bread 800g", Brand = $"Other{shop}", PackSize = 800, PackUnit = "g", PackQuantity = 1, CreatedDate = now, UpdatedDate = now };
                var mapping = new ShopProduct { Product = p, ShopId = shop, ShopProductCode = "1234567", NameAtShop = p.Name,
                    ProductUrl = shop == 1 ? "https://www.coles.com.au/product/light-rye-bread-1234567" : "https://www.woolworths.com.au/shop/productdetails/1234567", MatchType = MatchType.Exact, IsActive = true, FirstFoundDate = now, LastFoundDate = now };
                var job = new BrandDiscoveryJob { UserAccountId = f.Account, ShoppingListId = f.List.Scope.ListId, ShoppingListProductId = item.Id, ProductId = original.Id, ShopId = shop, Status = "Completed", FoundCount = 1, CreatedDate = now, UpdatedDate = now, CompletedDate = now };
                f.Db.ShopProducts.Add(mapping); f.Db.BrandDiscoveryJobs.Add(job); await f.Db.SaveChangesAsync();
                f.Db.ShopProductPrices.Add(new() { ShopProductId = mapping.Id, Price = shop == 1 ? 3m : 4m, Currency = "AUD", InStock = true, CheckedDate = now, CreatedDate = now, UpdatedDate = now, SourceType = SourceType.RetailerPage, PriceScope = PriceScope.Online, SourceUrl = mapping.ProductUrl });
                f.Db.ProductBrandSuggestions.Add(new() { BrandDiscoveryJobId = job.Id, AlternativeProductId = p.Id, ShopProductId = mapping.Id, DiscoveredDate = now });
                if (shop == 1) f.Coles = p; else f.Woolworths = p;
            }
            await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear(); return f;
        }
        public async Task Choose(long shop, long? product, int quantity)
        {
            var item = await List.ItemAsync(); await Service.ChangeAsync(Account, List.Scope.ListId, item.Id, shop, new(Account, product, quantity, item.UpdatedDate), default);
        }
        public async ValueTask DisposeAsync() { await List.Scope.DisposeAsync(); await isolated.DisposeAsync(); }
    }
    private sealed class ApiFactory(Fixture f) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>(); services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(f.List.Scope.Clock);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
        public async Task<HttpClient> ClientAsync()
        {
            var token = SessionToken.Create(); f.Db.UserSessions.Add(new() { UserAccountId = f.Account, TokenHash = SessionToken.Hash(token), CreatedDate = f.List.Scope.Clock.Now.UtcDateTime, ExpiresDate = f.List.Scope.Clock.Now.UtcDateTime.AddHours(3) });
            await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false });
            client.DefaultRequestHeaders.Add("Cookie", "myshoppinglist_session=" + token); client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1"); return client;
        }
    }
}
