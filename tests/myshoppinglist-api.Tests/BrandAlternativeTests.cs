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
public class BrandAlternativeTests
{
    private static BrandAlternativeReviewRequest Approval(long expected = 0) => new(true,
        "Reviewed manufacturer details: same intended use and format; dietary claims and composition checked. Taste and quality can differ.",
        expected, true, true, null, null);

    [PostgreSqlFact]
    public async Task Both_views_keep_alternatives_separate_and_preserve_totals_and_private_evidence()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.List.ItemAsync();
        var importCount = await f.Db.ProductImportJobs.CountAsync();
        Assert.Empty(await f.ReadAsync()); // No relationship means no inferred substitute.
        await f.ReviewAsync();
        Assert.Empty(await f.ReadAsync()); // Independent setting defaults off.
        await f.EnableAsync();
        var packPreference = await f.Db.UserAccounts.Where(a => a.Id == f.AccountId).Select(a => a.IncludeDifferentPackSizes).SingleAsync();
        await f.Db.UserAccounts.Where(a => a.Id == f.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IncludeDifferentPackSizes, false));
        var plan = (await f.List.Service.PlanAsync(f.AccountId, f.List.Scope.ListId, default))!;
        var row = Assert.Single(plan.Items);
        var alternative = Assert.Single(row.BrandAlternatives!);
        Assert.Equal("EquivalentDifferentBrand", alternative.Relationship);
        Assert.Equal("Similar product — different brand", alternative.Label);
        Assert.Empty(row.PackAlternatives!);
        Assert.Equal(1000m, alternative.TotalQuantity); Assert.Equal(2m, alternative.QuantityRatio);
        Assert.Equal(1.3m, alternative.Offer.UnitPrice); Assert.Equal(-35m, alternative.UnitPriceDifferencePercent);
        Assert.Equal(3m, alternative.PackagePriceDifference);
        Assert.Equal(10m * before.Quantity, plan.Lowest.Subtotal);
        Assert.Equal(0m, plan.Retailers.Single(r => r.ShopId == 2).Subtotal);
        var detail = (await new InStoreShoppingService(f.Db, f.List.Scope.Clock, new()).DetailAsync(f.AccountId, f.List.Scope.ListId, default))!;
        Assert.Equal(alternative, Assert.Single(Assert.Single(detail.Items).BrandAlternatives!));
        Assert.Equal(before, detail.Items[0].Item);
        Assert.Equal(plan.Lowest.Subtotal, detail.SplitSubtotal);
        var json = JsonSerializer.Serialize(plan);
        Assert.DoesNotContain("Reviewed manufacturer", json); Assert.DoesNotContain("ReviewerAccountId", json);
        Assert.False(await f.Db.PackDiscoveryJobs.AnyAsync());
        Assert.Equal(importCount, await f.Db.ProductImportJobs.CountAsync());
        Assert.False(await f.Db.ColesExtensionTasks.AnyAsync());
        Assert.Null(await f.List.Service.PlanAsync(f.AdminId, f.List.Scope.ListId, default));
        Assert.Null(await new InStoreShoppingService(f.Db, f.List.Scope.Clock, new()).DetailAsync(f.AdminId, f.List.Scope.ListId, default));
        Assert.True(packPreference);
    }

    [PostgreSqlFact]
    public async Task Reviews_are_symmetric_append_only_and_withdrawal_wins_over_old_approval()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnableAsync();
        var approved = await f.ReviewAsync();
        Assert.Single((await f.ReadAsync())[f.Original.Id]);
        var reverse = await BrandAlternativeService.ReadForProductsAsync(f.Db, f.List.Scope.Clock, f.AccountId, [f.Alternative], default);
        Assert.Equal(f.Original.Id, Assert.Single(reverse[f.Alternative.Id]).Product.Id);
        var conflict = await Assert.ThrowsAsync<UserImportException>(() => f.ReviewAsync());
        Assert.Equal(409, conflict.Status);
        var withdrawn = await f.Service.ReviewAsync(f.AdminId, f.Alternative.Id, f.Original.Id,
            (await f.RequestAsync(approved.Id)) with { Approved = false, Evidence = "Withdrawn after suitability review.", SameUseAndFormatConfirmed = false, DietaryAndCompositionChecked = false }, default);
        Assert.Empty(await f.ReadAsync());
        Assert.Equal(approved.Id, withdrawn.PreviousReviewId);
        var history = await f.Service.HistoryAsync(f.AdminId, f.Alternative.Id, f.Original.Id, default);
        Assert.Equal(2, history.Length); Assert.False(history[0].Approved); Assert.True(history[1].Approved);
        Assert.Equal(approved.Evidence, history[1].Evidence); Assert.Equal(f.AdminId, history[1].ReviewerAccountId);
        var restored = await f.ReviewAsync(withdrawn.Id);
        Assert.Equal(withdrawn.Id, restored.PreviousReviewId);
        Assert.Single((await f.ReadAsync())[f.Original.Id]);
    }

    [PostgreSqlFact]
    public async Task Changed_identity_or_composition_requires_fresh_review_but_price_refresh_does_not()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnableAsync(); var review = await f.ReviewAsync();
        await f.Db.Products.Where(p => p.Id == f.Alternative.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedDate, f.List.Scope.Clock.Now.UtcDateTime.AddMinutes(1)));
        Assert.Single((await f.ReadAsync())[f.Original.Id]);
        var oldContext = await f.RequestAsync(review.Id);
        await f.Db.Products.Where(p => p.Id == f.Alternative.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Description, "Changed composition"));
        Assert.Equal(409, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.ReviewAsync(f.AdminId, f.Original.Id, f.Alternative.Id, oldContext, default))).Status);
        Assert.Empty(await f.ReadAsync());
        Assert.True(Assert.Single(await f.Service.HistoryAsync(f.AdminId, f.Original.Id, f.Alternative.Id, default)).NeedsReview);
        await f.ReviewAsync(review.Id);
        Assert.Single((await f.ReadAsync())[f.Original.Id]);
        await f.Db.Products.Where(p => p.Id == f.Original.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Renamed original coffee 500g"));
        Assert.Empty(await f.ReadAsync());
    }

    [PostgreSqlFact]
    public async Task Approval_rejects_unproven_or_contradictory_products_and_requires_both_confirmations()
    {
        await using var f = await Fixture.CreateAsync();
        var validRequest = await f.RequestAsync();
        foreach (var request in new[] { validRequest with { Evidence = " " }, validRequest with { Evidence = new string('x', 2001) },
            validRequest with { ExpectedReviewId = -1 }, validRequest with { SameUseAndFormatConfirmed = false },
            validRequest with { DietaryAndCompositionChecked = false }, validRequest with { Approved = false } })
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.ReviewAsync(f.AdminId, f.Original.Id, f.Alternative.Id, request, default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.ReviewAsync(f.AdminId, f.Original.Id, f.Original.Id, Approval(), default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.ReviewAsync(f.AdminId, f.Original.Id, long.MaxValue, Approval(), default))).Status);
        // These conflicts must not be overridden by a review checkbox.
        foreach (var problem in new[] { "same_brand", "no_brand", "category", "no_category", "subcategory", "variant", "units", "pack", "extreme", "deleted", "gtin", "model", "ambiguous_pack" })
        {
            var original = await f.Db.Products.SingleAsync(p => p.Id == f.Original.Id);
            var alternative = await f.Db.Products.SingleAsync(p => p.Id == f.Alternative.Id);
            alternative.Brand = problem == "same_brand" ? original.Brand : problem == "no_brand" ? null : "Other";
            alternative.Category = problem == "no_category" ? null : problem == "category" ? "Cleaning" : "Drinks";
            alternative.SubCategory = problem == "subcategory" ? "Instant coffee" : "Ground coffee";
            alternative.Variant = problem == "variant" ? "Decaffeinated" : null;
            alternative.PackUnit = problem == "units" ? "mL" : "kg";
            alternative.PackSize = problem == "pack" ? null : problem == "extreme" ? 100 : 1;
            alternative.Name = problem == "ambiguous_pack" ? "Other Coffee 2 pack 1kg" : problem == "units" ? "Other Coffee 1L" : problem == "extreme" ? "Other Coffee 100kg" : "Other Coffee 1kg";
            alternative.IsDeleted = problem == "deleted";
            original.GTIN = problem == "gtin" ? "4006381333931" : null;
            alternative.GTIN = original.GTIN;
            alternative.ModelNumber = problem == "model" ? "DIFFERENT" : null;
            await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
            var error = await Assert.ThrowsAsync<UserImportException>(() => f.ReviewAsync());
            Assert.Equal(400, error.Status);
        }
        Assert.False(await f.Db.ProductAlternativeRelationships.AnyAsync());
    }

    [PostgreSqlFact]
    public async Task Preferences_account_eligibility_and_preferred_store_filtering_apply_in_backend()
    {
        await using var f = await Fixture.CreateAsync();
        await f.ReviewAsync(); await f.EnableAsync();
        await f.Db.UserAccounts.Where(a => a.Id == f.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.CompareOnlyPreferredShops, true));
        f.Db.UserPreferredShops.Add(new() { UserAccountId = f.AccountId, ShopId = 1, IsEnabled = true });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        Assert.Empty((await f.ReadAsync())[f.Original.Id]);
        await f.Db.UserPreferredShops.Where(p => p.UserAccountId == f.AccountId).ExecuteUpdateAsync(s => s.SetProperty(p => p.ShopId, 2));
        var alternative = Assert.Single((await f.ReadAsync())[f.Original.Id]);
        Assert.Null(alternative.Original); Assert.Null(alternative.UnitPriceDifferencePercent);
        await f.Db.UserPreferredShops.Where(p => p.UserAccountId == f.AccountId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, false));
        Assert.Empty((await f.ReadAsync())[f.Original.Id]);
        await f.Db.UserAccounts.Where(a => a.Id == f.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.CompareOnlyPreferredShops, false).SetProperty(a => a.IncludeDifferentBrands, false));
        Assert.Empty(await f.ReadAsync());
        await f.EnableAsync();
        await f.Db.UserAccounts.Where(a => a.Id == f.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsTrial, true).SetProperty(a => a.ExpiresDate, f.List.Scope.Clock.Now.UtcDateTime.AddMinutes(-1)));
        Assert.Empty(await f.ReadAsync());
        await f.Db.UserAccounts.Where(a => a.Id == f.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsTrial, false).SetProperty(a => a.Email, "brand-user@example.test").SetProperty(a => a.PasswordHash, "test").SetProperty(a => a.PasswordSalt, "test"));
        Assert.Single((await f.ReadAsync())[f.Original.Id]);
        await f.Db.UserAccounts.Where(a => a.Id == f.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
        Assert.Empty(await f.ReadAsync());
    }

    [PostgreSqlFact]
    public async Task Unverified_inactive_ambiguous_and_restricted_retailer_mappings_are_excluded()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnableAsync(); await f.ReviewAsync();
        var mappings = f.Db.ShopProducts.Where(m => m.ProductId == f.Alternative.Id);
        await mappings.ExecuteUpdateAsync(s => s.SetProperty(m => m.MatchType, MatchType.Likely));
        Assert.Empty((await f.ReadAsync())[f.Original.Id]);
        await mappings.ExecuteUpdateAsync(s => s.SetProperty(m => m.MatchType, MatchType.Exact).SetProperty(m => m.IsActive, false));
        Assert.Empty((await f.ReadAsync())[f.Original.Id]);
        await mappings.ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, true).SetProperty(m => m.ShopId, 3));
        Assert.Empty((await f.ReadAsync())[f.Original.Id]);
        await mappings.ExecuteUpdateAsync(s => s.SetProperty(m => m.ShopId, 2));
        await f.Db.Shops.Where(s => s.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(s => s.IsActive, false));
        Assert.Empty((await f.ReadAsync())[f.Original.Id]);
        await f.Db.Shops.Where(s => s.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(s => s.IsActive, true));
        f.Db.ShopProducts.Add(new() { ProductId = f.Alternative.Id, ShopId = 2, NameAtShop = "Duplicate", ProductUrl = "https://www.woolworths.com.au/shop/productdetails/654321", MatchType = MatchType.Exact, FirstFoundDate = f.List.Scope.Clock.Now.UtcDateTime, LastFoundDate = f.List.Scope.Clock.Now.UtcDateTime });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        Assert.Empty((await f.ReadAsync())[f.Original.Id]);
    }

    [PostgreSqlFact]
    public async Task Stale_conditional_future_out_of_stock_and_wrong_scope_prices_never_claim_savings()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnableAsync(); await f.ReviewAsync();
        var prices = f.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == f.Alternative.Id);
        foreach (var condition in new[] { "stale", "conditional", "out_of_stock", "future", "scope" })
        {
            await prices.ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, condition == "stale" ? f.List.Scope.Clock.Now.UtcDateTime.AddDays(-8) : condition == "future" ? f.List.Scope.Clock.Now.UtcDateTime.AddDays(1) : f.List.Scope.Clock.Now.UtcDateTime)
                .SetProperty(p => p.InStock, condition != "out_of_stock").SetProperty(p => p.SpecialDescription, condition == "conditional" ? "Members only" : null)
                .SetProperty(p => p.PriceScope, condition == "scope" ? PriceScope.InStore : PriceScope.Online));
            var found = Assert.Single((await f.ReadAsync())[f.Original.Id]);
            Assert.NotEqual("Fresh", found.Offer.Status); Assert.Null(found.UnitPriceDifferencePercent); Assert.Null(found.PackagePriceDifference);
        }
        await prices.ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceScope, PriceScope.Online).SetProperty(p => p.Currency, "USD"));
        Assert.Empty((await f.ReadAsync())[f.Original.Id]);
        var invalidPrice = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => prices.ExecuteUpdateAsync(s => s.SetProperty(p => p.Currency, "AUD").SetProperty(p => p.Price, -1m)));
        Assert.Equal("23514", invalidPrice.SqlState);
        await prices.ExecuteUpdateAsync(s => s.SetProperty(p => p.Currency, "AUD"));
        await prices.ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 0m));
        var free = Assert.Single((await f.ReadAsync())[f.Original.Id]);
        Assert.Equal(-100m, free.UnitPriceDifferencePercent);
        await f.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == f.Original.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.List.Scope.Clock.Now.UtcDateTime.AddDays(-8)));
        Assert.Null(Assert.Single((await f.ReadAsync())[f.Original.Id]).UnitPriceDifferencePercent);
        await prices.ExecuteDeleteAsync();
        Assert.Empty((await f.ReadAsync())[f.Original.Id]);
    }

    [PostgreSqlFact]
    public async Task Concurrent_initial_reviews_have_one_winner_and_preserve_history()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.RequestAsync();
        async Task<int> Submit()
        {
            await using var db = PersistenceScope.Context();
            var service = new BrandAlternativeService(db, f.List.Scope.Clock, Options.Create(new AdminOptions { AccountIds = [f.AdminId] }));
            try { await service.ReviewAsync(f.AdminId, f.Original.Id, f.Alternative.Id, request, default); return 200; }
            catch (UserImportException error) { return error.Status; }
        }
        var responses = await Task.WhenAll(Submit(), Submit());
        Assert.Equal(new[] { 200, 409 }, responses.Order().ToArray());
        Assert.Single(await f.Service.HistoryAsync(f.AdminId, f.Original.Id, f.Alternative.Id, default));
    }

    [PostgreSqlFact]
    public async Task Endpoint_enforces_admin_identity_validates_requests_and_disables_caching()
    {
        await using var f = await Fixture.CreateAsync();
        var adminToken = new string('a', 64); var userToken = new string('b', 64);
        f.Db.UserSessions.AddRange(Session(f.AdminId, adminToken), Session(f.AccountId, userToken));
        await f.Db.SaveChangesAsync();
        await using var app = new ContributionTestApp(f.AdminId);
        using var client = app.CreateClient();
        var path = $"/api/admin/product-alternatives/{f.Original.Id}/{f.Alternative.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(path, Approval())).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, Approval())).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        client.DefaultRequestHeaders.Add("X-MyShoppingList-Request", "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new { approved = true, evidence = "Incomplete request" })).StatusCode);
        var context = (await client.GetFromJsonAsync<BrandAlternativeReviewContext>(path))!;
        Assert.Empty(context.Reviews);
        Assert.Equal(f.Original.Name, context.Product.Product.Name);
        Assert.Equal(f.Alternative.Name, context.AlternativeProduct.Product.Name);
        using var response = await client.PutAsJsonAsync(path, new
        {
            approved = true,
            evidence = Approval().Evidence,
            expectedReviewId = 0,
            sameUseAndFormatConfirmed = true,
            dietaryAndCompositionChecked = true,
            expectedProductFingerprint = context.Product.Fingerprint,
            expectedAlternativeFingerprint = context.AlternativeProduct.Fingerprint,
            reviewerAccountId = f.AccountId
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var saved = (await response.Content.ReadFromJsonAsync<BrandAlternativeReviewResponse>())!;
        Assert.Equal(f.AdminId, saved.ReviewerAccountId);
        using var history = await client.GetAsync(path);
        Assert.True(history.Headers.CacheControl?.NoStore);
        Assert.Equal(saved, Assert.Single((await history.Content.ReadFromJsonAsync<BrandAlternativeReviewContext>())!.Reviews));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, Approval())).StatusCode);
        var denial = await Assert.ThrowsAsync<UserImportException>(() => f.Service.ReviewAsync(f.AccountId, f.Original.Id, f.Alternative.Id, Approval(saved.Id), default));
        Assert.Equal(403, denial.Status);
    }

    [PostgreSqlFact]
    public async Task Migration_preserves_existing_list_data_through_upgrade_rollback_and_reapplication()
    {
        await using var isolation = await IsolatedDatabaseScope.CreateAsync();
        await using var list = await ListFixture.CreateAsync();
        var before = await list.ItemAsync();
        var db = list.Scope.Db;
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"ProductAlternativeRelationships\"");
        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, m => m.EndsWith("_AddVerifiedBrandAlternatives", StringComparison.Ordinal));
        Assert.True(index > 0);
        var previous = migrations[index - 1]; var current = migrations[index];
        await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript());
        var migrator = db.GetService<IMigrator>();
        await db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(previous, current));
        Assert.Empty(await db.ProductAlternativeRelationships.ToArrayAsync());
        Assert.Equal(before, await list.ItemAsync());
        await db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(current, previous));
        Assert.Equal(before, await list.ItemAsync());
        await db.Database.ExecuteSqlRawAsync(migrator.GenerateScript(previous, current));
        Assert.Empty(await db.ProductAlternativeRelationships.ToArrayAsync());
        Assert.Equal(before, await list.ItemAsync());
    }

    [PostgreSqlFact]
    public async Task Catalogue_search_is_literal_bounded_and_includes_retired_records_for_review()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal(f.Original.Id, Assert.Single((await f.Service.SearchAsync(f.AdminId, "ACME", default)).Items).Product.Id);
        Assert.Equal(f.Alternative.Id, Assert.Single((await f.Service.SearchAsync(f.AdminId, f.Alternative.Id.ToString(), default)).Items).Product.Id);
        Assert.Empty((await f.Service.SearchAsync(f.AdminId, "no-such-catalogue-product", default)).Items);
        Assert.Empty((await f.Service.SearchAsync(f.AdminId, "%_", default)).Items);
        Assert.Empty((await f.Service.SearchAsync(f.AdminId, "", default)).Items);
        foreach (var bad in new string?[] { null, "a", "0", new string('x', 101), "co\nffee" })
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.SearchAsync(f.AdminId, bad, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<UserImportException>(() => f.Service.SearchAsync(f.AccountId, "coffee", default))).Status);
        await f.Db.Products.Where(p => p.Id == f.Alternative.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDeleted, true));
        Assert.True(Assert.Single((await f.Service.SearchAsync(f.AdminId, "Other", default)).Items).IsDeleted);
        var now = f.List.Scope.Clock.Now.UtcDateTime;
        f.Db.Products.AddRange(Enumerable.Range(0, 55).Select(i => new Product { Name = "Bounded search " + i.ToString("D2"), Brand = "Bounded", CreatedDate = now, UpdatedDate = now }));
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var results = await f.Service.SearchAsync(f.AdminId, "bounded", default);
        Assert.Equal(50, results.Items.Length); Assert.True(results.HasMore);
        Assert.Equal("Bounded search 00", results.Items[0].Product.Name);
        Assert.False(await f.Db.PackDiscoveryJobs.AnyAsync());
    }

    [PostgreSqlFact]
    public async Task Search_endpoint_requires_administrator_and_rejects_ambiguous_queries()
    {
        await using var f = await Fixture.CreateAsync();
        var token = new string('a', 64); var user = new string('b', 64);
        f.Db.UserSessions.AddRange(Session(f.AdminId, token), Session(f.AccountId, user)); await f.Db.SaveChangesAsync();
        await using var app = new ContributionTestApp(f.AdminId); using var client = app.CreateClient();
        const string path = "/api/admin/product-alternatives/products?search=coffee";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<AdminAlternativeProductSearchResult>())!.Items.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "&search=tea")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/product-alternatives/products")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/product-alternatives/products?search=a")).StatusCode);
    }

    private static UserSession Session(long id, string token) => new() { UserAccountId = id, TokenHash = SessionToken.Hash(token), CreatedDate = DateTime.UtcNow, ExpiresDate = DateTime.UtcNow.AddHours(1) };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IsolatedDatabaseScope isolation;
        public ListFixture List { get; }
        public MyShoppingListDbContext Db => List.Scope.Db;
        public long AccountId => List.Scope.UserId;
        public long AdminId { get; private set; }
        public Product Original { get; private set; } = null!;
        public Product Alternative { get; private set; } = null!;
        public BrandAlternativeService Service => new(Db, List.Scope.Clock, Options.Create(new AdminOptions { AccountIds = [AdminId] }));
        private Fixture(IsolatedDatabaseScope isolation, ListFixture list) { this.isolation = isolation; List = list; }
        public static async Task<Fixture> CreateAsync()
        {
            var isolation = await IsolatedDatabaseScope.CreateAsync();
            try
            {
                var f = new Fixture(isolation, await ListFixture.CreateAsync());
                var now = f.List.Scope.Clock.Now.UtcDateTime;
                f.Original = await f.Db.Products.SingleAsync(p => p.Id == (f.Db.ShoppingListProducts.Where(i => i.Id == f.List.ItemId).Select(i => i.ProductId).Single()));
                f.Original.Name = "Acme Original Coffee 500g"; f.Original.Brand = "Acme"; f.Original.Variant = null;
                f.Original.Category = "Drinks"; f.Original.SubCategory = "Ground coffee";
                f.Original.PackQuantity = null; f.Original.PackSize = 500; f.Original.PackUnit = "g";
                f.Alternative = new() { Name = "Other Original Coffee 1kg", Brand = "Other", Category = "Drinks", SubCategory = "Ground coffee", PackSize = 1, PackUnit = "kg", CreatedDate = now, UpdatedDate = now };
                var admin = AdminAccountTests.TestAccount(); f.Db.UserAccounts.Add(admin);
                var mapping = new ShopProduct { Product = f.Alternative, ShopId = 2, NameAtShop = f.Alternative.Name, ProductUrl = "https://www.woolworths.com.au/shop/productdetails/123456", MatchType = MatchType.Exact, FirstFoundDate = now, LastFoundDate = now };
                f.Db.ShopProductPrices.Add(new() { ShopProduct = mapping, Price = 13, Currency = "AUD", CheckedDate = now, CreatedDate = now, UpdatedDate = now, InStock = true, PriceScope = PriceScope.Online });
                await f.Db.ShopProductPrices.Where(p => p.ShopProduct.ProductId == f.Original.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 10m).SetProperty(p => p.CheckedDate, now));
                await f.Db.SaveChangesAsync(); f.AdminId = admin.Id; f.Db.ChangeTracker.Clear();
                return f;
            }
            catch { await isolation.DisposeAsync(); throw; }
        }
        public Task EnableAsync() => Db.UserAccounts.Where(a => a.Id == AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IncludeDifferentBrands, true));
        public async Task<BrandAlternativeReviewRequest> RequestAsync(long expected = 0)
        {
            var context = await Service.ContextAsync(AdminId, Original.Id, Alternative.Id, default);
            return Approval(expected) with { ExpectedProductFingerprint = context.Product.Fingerprint, ExpectedAlternativeFingerprint = context.AlternativeProduct.Fingerprint };
        }
        public async Task<BrandAlternativeReviewResponse> ReviewAsync(long expected = 0) => await Service.ReviewAsync(AdminId, Original.Id, Alternative.Id, await RequestAsync(expected), default);
        public Task<Dictionary<long, BrandAlternative[]>> ReadAsync() => BrandAlternativeService.ReadForProductsAsync(Db, List.Scope.Clock, AccountId, [Original], default);
        public async ValueTask DisposeAsync()
        {
            try
            {
                Db.ChangeTracker.Clear();
                foreach (var id in await Db.ProductAlternativeRelationships.OrderByDescending(r => r.Id).Select(r => r.Id).ToArrayAsync())
                    await Db.ProductAlternativeRelationships.Where(r => r.Id == id).ExecuteDeleteAsync();
                await List.DisposeAsync();
            }
            finally { await isolation.DisposeAsync(); }
        }
    }
}
