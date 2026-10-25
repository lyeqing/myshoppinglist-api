using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using Npgsql;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class PersonalProductReadIsolationTests
{
    [PostgreSqlFact]
    public async Task Library_rejects_foreign_products_even_when_preferences_activity_and_list_links_exist()
    {
        await using var f = await ReadScope.CreateAsync();
        await f.ForgeForeignLinksAsync();
        var library = new ProductLibraryService(f.Db, f.Clock);
        foreach (var view in new[] { "history", "favorites", "essentials" })
        {
            var page = await library.ReadAsync(f.First.Id, view, "all", null, 0, 24, default);
            Assert.Equal(f.OwnSmall.Id, Assert.Single(page.Items).Product.Id);
        }
        Assert.Empty(await library.PreferencesAsync(f.First.Id, [f.ForeignSmall.Id], default));
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => library.SavePreferenceAsync(f.First.Id,
            f.ForeignSmall.Id, new(f.First.Id, "favorite", false), default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => library.AddAsync(f.First.Id,
            f.ForeignSmall.Id, f.FirstList.Id, new(f.First.Id), default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => library.AddAsync(f.First.Id,
            f.OwnSmall.Id, f.SecondList.Id, new(f.First.Id), default))).Status);
    }

    [PostgreSqlFact]
    public async Task Essential_seeding_preserves_own_and_legacy_products_and_skips_foreign_references()
    {
        await using var f = await ReadScope.CreateAsync();
        await f.ForgeForeignLinksAsync();
        f.Db.UserProductPreferences.Add(new() { UserAccountId = f.First.Id, ProductId = f.LegacySmall.Id, IsEssential = true, UpdatedDate = f.Now });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var list = await f.Lists.CreateAsync(f.First.Id, new("Seeded list"), "UTC", default);
        var page = (await f.Lists.ReadAsync(f.First.Id, list.Id, null, 50, true, true, default))!;
        Assert.Equal(new[] { f.OwnSmall.Id, f.LegacySmall.Id }.Order(), page.Items.Select(i => i.Product.Id).Order());
        Assert.All(page.Items, i => Assert.Equal(1, i.Quantity));
    }

    [PostgreSqlFact]
    public async Task Lists_planning_and_in_store_hide_foreign_links_and_keep_legacy_prices_separate()
    {
        await using var f = await ReadScope.CreateAsync();
        await f.ForgeForeignLinksAsync();
        var legacyItem = await f.ItemAsync(f.FirstList, f.LegacySmall);
        var expected = new[] { f.OwnSmall.Id, f.LegacySmall.Id }.Order().ToArray();
        var page = (await f.Lists.ReadAsync(f.First.Id, f.FirstList.Id, null, 50, true, true, default))!;
        Assert.Equal(expected, page.Items.Select(i => i.Product.Id).Order());
        var plan = (await f.Lists.PlanAsync(f.First.Id, f.FirstList.Id, default))!;
        Assert.Equal(expected, plan.Items.Select(i => i.Item.Product.Id).Order());
        Assert.Equal(10m, Assert.Single(plan.Items.Single(i => i.Item.Product.Id == f.OwnSmall.Id).Prices).Price);
        Assert.Equal(30m, Assert.Single(plan.Items.Single(i => i.Item.Product.Id == f.LegacySmall.Id).Prices).Price);
        Assert.Null(plan.Items.Single(i => i.Item.Product.Id == f.OwnSmall.Id).Comparison);
        Assert.All(plan.Items.Single(i => i.Item.Product.Id == f.OwnSmall.Id).Prices, p => Assert.Null(p.RefreshStatus));
        var stores = new InStoreShoppingService(f.Db, f.Clock, new());
        var detail = (await stores.DetailAsync(f.First.Id, f.FirstList.Id, default))!;
        Assert.Equal(expected, detail.Items.Select(i => i.Item.Product.Id).Order());
        Assert.Equal(2, (await stores.ListsAsync(f.First.Id, default)).Single(l => l.Id == f.FirstList.Id).RemainingCount);
        Assert.Null(await stores.DetailAsync(f.Second.Id, f.FirstList.Id, default));
        Assert.Null(await f.Lists.PlanAsync(f.Second.Id, f.FirstList.Id, default));
        Assert.DoesNotContain("Foreign secret", JsonSerializer.Serialize(plan));
        Assert.DoesNotContain("Foreign secret", JsonSerializer.Serialize(detail));
    }

    [PostgreSqlFact]
    public async Task Foreign_list_item_cannot_be_edited_deleted_or_exposed_in_archived_history()
    {
        await using var f = await ReadScope.CreateAsync();
        var foreign = await f.ItemAsync(f.FirstList, f.ForeignSmall);
        Assert.Equal(404, (await f.Lists.UpdateAsync(f.First.Id, f.FirstList.Id, foreign.Id,
            new(7, "Changed", false, false, f.Now), default)).StatusCode);
        Assert.Equal(404, (await f.Lists.DeleteAsync(f.First.Id, f.FirstList.Id, foreign.Id, new(f.Now), default)).StatusCode);
        Assert.Equal(1, (await f.Db.ShoppingListProducts.SingleAsync(i => i.Id == foreign.Id)).Quantity);
        f.Db.ChangeTracker.Clear();
        await f.Db.ShoppingLists.Where(l => l.Id == f.FirstList.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        var history = await f.Lists.HistoryDetailAsync(f.First.Id, f.FirstList.Id, default);
        Assert.Equal(f.OwnSmall.Id, Assert.Single(history.Items).Product.Id);
    }

    [PostgreSqlFact]
    public async Task Pack_suggestions_use_only_the_originals_ownership_group()
    {
        await using var f = await ReadScope.CreateAsync();
        var suggestions = await new PackAlternativeService(f.Db, f.Clock).ReadAsync(f.First.Id,
            [f.OwnSmall, f.LegacySmall, f.ForeignSmall], default);
        Assert.Equal(f.OwnLarge.Id, Assert.Single(suggestions[f.OwnSmall.Id]).Product.Id);
        Assert.Equal(f.LegacyLarge.Id, Assert.Single(suggestions[f.LegacySmall.Id]).Product.Id);
        Assert.False(suggestions.ContainsKey(f.ForeignSmall.Id));
        var forged = new Product { Id = f.ForeignSmall.Id, UserAccountId = f.First.Id };
        Assert.Empty(await new PackAlternativeService(f.Db, f.Clock).ReadAsync(f.First.Id, [forged], default));
    }

    [PostgreSqlFact]
    public async Task Brand_suggestions_reject_foreign_and_legacy_candidates_for_a_private_original()
    {
        await using var f = await ReadScope.CreateAsync();
        var job = new BrandDiscoveryJob { UserAccountId = f.First.Id, ShoppingListId = f.FirstList.Id,
            ShoppingListProductId = f.OwnItem.Id, ProductId = f.OwnSmall.Id, ShopId = 1,
            Status = "Completed", CreatedDate = f.Now, UpdatedDate = f.Now, CompletedDate = f.Now };
        foreach (var candidate in new[] { f.OwnOtherBrand, f.ForeignOtherBrand, f.LegacyOtherBrand })
            f.Db.ProductBrandSuggestions.Add(new() { BrandDiscoveryJob = job, AlternativeProductId = candidate.Id,
                ShopProductId = f.Mapping(candidate).Id, DiscoveredDate = f.Now });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var suggestions = await BrandAlternativeService.ReadForProductsAsync(f.Db, f.Clock, f.First.Id,
            [f.OwnSmall, f.ForeignSmall], default);
        Assert.Equal(f.OwnOtherBrand.Id, Assert.Single(suggestions[f.OwnSmall.Id]).Product.Id);
        Assert.False(suggestions.ContainsKey(f.ForeignSmall.Id));
    }

    [PostgreSqlFact]
    public async Task Substitutions_hide_foreign_choices_and_allow_undo_without_disclosing_the_product()
    {
        await using var f = await ReadScope.CreateAsync();
        f.Db.ShoppingListItemSubstitutions.Add(new() { UserAccountId = f.First.Id, ShoppingListId = f.FirstList.Id,
            ShoppingListProductId = f.OwnItem.Id, ProductId = f.OwnSmall.Id, ShopId = 1,
            ShopProductId = f.Mapping(f.ForeignOtherBrand).Id, Quantity = 1,
            OriginalFingerprint = new string('a', 64), AlternativeFingerprint = new string('b', 64), UpdatedDate = f.Now });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var service = new ShoppingListSubstitutionService(f.Db, f.Clock);
        Assert.Empty(await service.ReadAsync(f.First.Id, f.FirstList.Id, [f.OwnItem], [], default));
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.ChangeAsync(f.First.Id,
            f.FirstList.Id, f.OwnItem.Id, 1, new(f.First.Id, f.ForeignOtherBrand.Id, 1, f.Now), default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.ChangeAsync(f.First.Id,
            f.FirstList.Id, f.OwnItem.Id, 1, new(f.First.Id, f.LegacyOtherBrand.Id, 1, f.Now), default))).Status);
        await service.ChangeAsync(f.First.Id, f.FirstList.Id, f.OwnItem.Id, 1, new(f.First.Id, null, 1, f.Now), default);
        Assert.Empty(await f.Db.ShoppingListItemSubstitutions.ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Personal_capture_status_is_complete_and_foreign_product_jobs_and_mappings_are_hidden()
    {
        await using var f = await ReadScope.CreateAsync();
        await f.Db.UserAccounts.Where(a => a.Id == f.First.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.CompareOnlyPreferredShops, true));
        f.Db.UserPreferredShops.AddRange(new UserPreferredShop { UserAccountId = f.First.Id, ShopId = 1 }, new UserPreferredShop { UserAccountId = f.First.Id, ShopId = 2 });
        var job = f.Job(f.OwnSmall, f.OwnItem);
        f.Db.ProductImportRetailerResults.AddRange(
            new ProductImportRetailerResult { ProductImportJob = job, ShopId = 1, ShopProductId = f.Mapping(f.OwnSmall).Id, Status = RetailerLookupStatus.Exact, MatchType = MatchType.Exact, CreatedDate = f.Now, UpdatedDate = f.Now },
            new ProductImportRetailerResult { ProductImportJob = job, ShopId = 2, Status = RetailerLookupStatus.Pending, CreatedDate = f.Now, UpdatedDate = f.Now });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var service = new ProductImportStatusService(f.Db, f.Clock);
        var status = (await service.ReadAsync(f.First.Id, job.Id, default))!;
        Assert.Equal(ProductImportJobStatus.Completed, status.Status);
        Assert.Equal(1, Assert.Single(status.Retailers).ShopId);
        Assert.Equal(10m, Assert.Single(status.Retailers[0].Prices).Price);
        Assert.Null(await service.ReadAsync(f.Second.Id, job.Id, default));

        await f.Db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == job.Id && r.ShopId == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ShopProductId, f.Mapping(f.ForeignSmall).Id));
        Assert.Empty((await service.ReadAsync(f.First.Id, job.Id, default))!.Retailers);
        var forgedItem = await f.ItemAsync(f.FirstList, f.ForeignSmall);
        var forgedJob = f.Job(f.ForeignSmall, forgedItem);
        f.Db.Add(forgedJob); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        Assert.Null(await service.ReadAsync(f.First.Id, forgedJob.Id, default));
        Assert.Equal(job.Id, Assert.Single((await service.ListAsync(f.First.Id, f.FirstList.Id, null, 50, default))!.Items).JobId);
    }

    [PostgreSqlFact]
    public async Task Administrator_product_search_and_reviews_cannot_read_even_the_administrators_private_products()
    {
        await using var f = await ReadScope.CreateAsync();
        var service = new BrandAlternativeService(f.Db, f.Clock, Options.Create(new AdminOptions { AccountIds = [f.First.Id] }));
        var search = await service.SearchAsync(f.First.Id, "Coffee", default);
        Assert.NotEmpty(search.Items);
        Assert.All(search.Items, i => Assert.Contains(i.Product.Id, new[] { f.LegacySmall.Id, f.LegacyLarge.Id, f.LegacyOtherBrand.Id }));
        Assert.Empty((await service.SearchAsync(f.First.Id, f.OwnSmall.Id.ToString(), default)).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.ContextAsync(f.First.Id, f.OwnSmall.Id, f.OwnOtherBrand.Id, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.HistoryAsync(f.First.Id, f.LegacySmall.Id, f.ForeignSmall.Id, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.ReviewAsync(f.First.Id, f.OwnSmall.Id,
            f.OwnOtherBrand.Id, new(false, "Not permitted", 0, false, false, null, null), default))).Status);
    }

    [PostgreSqlFact]
    public async Task Authenticated_endpoints_hide_private_capture_evidence_but_keep_shared_review_and_account_audit()
    {
        await using var f = await ReadScope.CreateAsync();
        await f.ForgeForeignLinksAsync();
        f.Db.ContributionObservations.AddRange(
            new ContributionObservation { UserAccountId = f.Second.Id, Source = "PersonalAdd", Url = "https://www.coles.com.au/product/private-secret", CollectedAt = f.Now, ReceivedAt = f.Now },
            new ContributionObservation { UserAccountId = f.Second.Id, Source = "SharedCustomer", Url = "https://www.coles.com.au/product/shared", CollectedAt = f.Now, ReceivedAt = f.Now });
        f.Db.AccountAdminAudits.Add(new() { AccountId = f.Second.Id, AdministratorId = f.First.Id, Reason = "Account review", BeforeJson = "{}", AfterJson = "{}", CreatedAt = f.Now });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        await using var app = new ReaderApiFactory(f);
        using var admin = await app.ClientAsync(f.First.Id);
        using var ordinary = await app.ClientAsync(f.Second.Id);
        using var anonymous = app.CreateClient(new() { BaseAddress = new("https://localhost") });
        var path = $"/api/admin/accounts/{f.Second.Id}/review";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync(path)).StatusCode);
        var response = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var review = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("SharedCustomer", Assert.Single(review.RootElement.GetProperty("observations").EnumerateArray()).GetProperty("source").GetString());
        Assert.Single(review.RootElement.GetProperty("actions").EnumerateArray());
        Assert.DoesNotContain("private-secret", review.RootElement.GetRawText());
        var library = await admin.GetStringAsync("/api/account/products?view=favorites");
        using var libraryJson = JsonDocument.Parse(library);
        Assert.Equal(f.OwnSmall.Id, Assert.Single(libraryJson.RootElement.GetProperty("items").EnumerateArray()).GetProperty("product").GetProperty("id").GetInt64());
        Assert.DoesNotContain("Foreign secret", library);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/product-alternatives/{f.OwnSmall.Id}/{f.OwnOtherBrand.Id}")).StatusCode);
    }

    private sealed class ReaderApiFactory(ReadScope fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(fixture.Clock);
                services.Configure<AdminOptions>(o => o.AccountIds = [fixture.First.Id]);
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
                services.PostConfigure<PriceRefreshOptions>(o => o.Enabled = false);
            });
        }
        public async Task<HttpClient> ClientAsync(long account)
        {
            var token = SessionToken.Create();
            fixture.Db.UserSessions.Add(new() { UserAccountId = account, TokenHash = SessionToken.Hash(token), CreatedDate = fixture.Now, ExpiresDate = fixture.Now.AddHours(1) });
            await fixture.Db.SaveChangesAsync(); fixture.Db.ChangeTracker.Clear();
            var client = CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add("Cookie", "myshoppinglist_session=" + token);
            client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
            return client;
        }
    }

    private sealed class ReadScope : IAsyncDisposable
    {
        private readonly string original;
        private readonly string schema = "codex_reader_" + Guid.NewGuid().ToString("N");
        private readonly List<ShopProduct> mappings = [];
        public string ConnectionString { get; }
        public MyShoppingListDbContext Db { get; }
        public PersistenceClock Clock { get; } = new() { Now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero) };
        public DateTime Now => Clock.Now.UtcDateTime;
        public UserAccount First { get; private set; } = null!;
        public UserAccount Second { get; private set; } = null!;
        public ShoppingList FirstList { get; private set; } = null!;
        public ShoppingList SecondList { get; private set; } = null!;
        public ShoppingListProduct OwnItem { get; private set; } = null!;
        public Product OwnSmall { get; private set; } = null!;
        public Product OwnLarge { get; private set; } = null!;
        public Product ForeignSmall { get; private set; } = null!;
        public Product ForeignLarge { get; private set; } = null!;
        public Product LegacySmall { get; private set; } = null!;
        public Product LegacyLarge { get; private set; } = null!;
        public Product OwnOtherBrand { get; private set; } = null!;
        public Product ForeignOtherBrand { get; private set; } = null!;
        public Product LegacyOtherBrand { get; private set; } = null!;
        public ShoppingListService Lists => new(Db, Clock);
        private ReadScope(string connection)
        {
            original = connection;
            ConnectionString = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, Pooling = false }.ConnectionString;
            Db = new(new DbContextOptionsBuilder<MyShoppingListDbContext>().UseNpgsql(ConnectionString).Options);
        }
        public static async Task<ReadScope> CreateAsync()
        {
            var f = new ReadScope(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!);
            await f.SchemaCommand($"CREATE SCHEMA \"{f.schema}\"");
            try
            {
                await f.Db.Database.MigrateAsync();
                f.First = f.Account("reader-a@example.test"); f.Second = f.Account("reader-b@example.test");
                f.Db.AddRange(f.First, f.Second); await f.Db.SaveChangesAsync();
                f.FirstList = new() { UserAccountId = f.First.Id, Name = "First list", CreatedDate = f.Now, UpdatedDate = f.Now };
                f.SecondList = new() { UserAccountId = f.Second.Id, Name = "Second list", CreatedDate = f.Now, UpdatedDate = f.Now };
                f.Db.AddRange(f.FirstList, f.SecondList); await f.Db.SaveChangesAsync();
                f.OwnSmall = await f.ProductAsync(f.First.Id, "Acme", 500, 10);
                f.OwnLarge = await f.ProductAsync(f.First.Id, "Acme", 1000, 12);
                f.ForeignSmall = await f.ProductAsync(f.Second.Id, "Acme", 500, 1);
                f.ForeignLarge = await f.ProductAsync(f.Second.Id, "Acme", 1000, 1);
                f.LegacySmall = await f.ProductAsync(null, "Acme", 500, 30);
                f.LegacyLarge = await f.ProductAsync(null, "Acme", 1000, 25);
                f.OwnOtherBrand = await f.ProductAsync(f.First.Id, "Other", 1000, 8);
                f.ForeignOtherBrand = await f.ProductAsync(f.Second.Id, "Other", 1000, 1);
                f.LegacyOtherBrand = await f.ProductAsync(null, "Other", 1000, 20);
                f.OwnItem = await f.ItemAsync(f.FirstList, f.OwnSmall);
                await f.ItemAsync(f.SecondList, f.ForeignSmall);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private UserAccount Account(string email) => new() { Email = email, PasswordHash = "fixture", PasswordSalt = "fixture",
            DisplayName = email, IsPaid = true, IncludeDifferentBrands = true, CreatedDate = Now, UpdatedDate = Now };
        private async Task<Product> ProductAsync(long? owner, string brand, int size, decimal price)
        {
            var product = new Product { UserAccountId = owner, Name = $"{brand} Original Coffee {size}g", Brand = brand,
                Description = owner == Second.Id ? "Foreign secret" : "Own or legacy product", PackQuantity = 1, PackSize = size,
                PackUnit = "g", Category = "Drinks", SubCategory = "Ground coffee", CreatedDate = Now, UpdatedDate = Now };
            var code = (10000 + mappings.Count).ToString();
            var mapping = new ShopProduct { UserAccountId = owner, Product = product, ShopId = 1, ShopProductCode = code,
                ProductUrl = "https://www.coles.com.au/product/" + code, NameAtShop = product.Name,
                MatchType = MatchType.Exact, FirstFoundDate = Now, LastFoundDate = Now, LastCheckedDate = Now };
            Db.ShopProductPrices.Add(new() { UserAccountId = owner, ShopProduct = mapping, Price = price,
                SourceUrl = mapping.ProductUrl, PriceScope = PriceScope.Unknown, SourceType = SourceType.BrowserExtension,
                CheckedDate = Now, CreatedDate = Now, UpdatedDate = Now });
            await Db.SaveChangesAsync(); Db.ChangeTracker.Clear(); mappings.Add(mapping); return product;
        }
        public ShopProduct Mapping(Product product) => mappings.Single(m => m.ProductId == product.Id);
        public async Task<ShoppingListProduct> ItemAsync(ShoppingList list, Product product)
        {
            var item = new ShoppingListProduct { ShoppingListId = list.Id, ProductId = product.Id, Product = product,
                Quantity = 1, AddedDate = Now, UpdatedDate = Now };
            // The product is already persisted; do not reinsert its detached navigation.
            item.Product = null!;
            Db.Add(item); await Db.SaveChangesAsync(); Db.ChangeTracker.Clear(); item.Product = product; return item;
        }
        public async Task ForgeForeignLinksAsync()
        {
            await ItemAsync(FirstList, ForeignSmall);
            foreach (var product in new[] { OwnSmall, ForeignSmall })
            {
                Db.UserProductPreferences.Add(new() { UserAccountId = First.Id, ProductId = product.Id, IsFavorite = true, IsEssential = true, UpdatedDate = Now });
                Db.UserProductActivities.Add(new() { UserAccountId = First.Id, ProductId = product.Id, Quantity = 1, AddedDate = Now });
            }
            await Db.SaveChangesAsync(); Db.ChangeTracker.Clear();
        }
        public ProductImportJob Job(Product product, ShoppingListProduct item) => new() { UserAccountId = First.Id,
            ShoppingListId = FirstList.Id, ProductId = product.Id, ShoppingListProductId = item.Id, SourceShopId = 1,
            SourceUrl = Mapping(product).ProductUrl, NormalisedSourceUrl = Mapping(product).ProductUrl, RequestedQuantity = 1,
            Status = ProductImportJobStatus.Completed, ProgressStage = ProductImportProgressStage.Completed,
            CreatedDate = Now, CompletedDate = Now };
        private async Task SchemaCommand(string sql)
        {
            await using var connection = new NpgsqlConnection(original);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await SchemaCommand($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }
}
