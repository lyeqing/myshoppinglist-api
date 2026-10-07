using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class UserExtensionTests
{
    private const string ExtensionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    [PostgreSqlFact]
    public async Task Yoghurt_comparison_advances_through_three_queries_without_repeating_completed_steps()
    {
        foreach (var ambiguous in new[] { false, true })
        {
            await using var f = await ListFixture.CreateAsync();
            var item = await f.ItemAsync();
            await f.Scope.Db.Products.Where(p => p.Id == item.Product.Id).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Name, "YoPro High Protein Yoghurt Pouch No Added Sugar Mango 150g")
                .SetProperty(p => p.Brand, "YoPro").SetProperty(p => p.Variant, (string?)null)
                .SetProperty(p => p.PackSize, 150m).SetProperty(p => p.PackUnit, "g").SetProperty(p => p.PackQuantity, 1));
            var full = "yopro high protein yoghurt pouch no added sugar mango 150g";
            f.Scope.Db.UserExtensionImportTasks.Add(new()
            {
                ProductImportJobId = f.Scope.Job.Id,
                RequestId = Guid.NewGuid(),
                Stage = "search",
                Query = full,
                Url = myshoppinglist_api.Providers.ProductSearchQueryBuilder.SearchUrl("woolworths", full).AbsoluteUri,
                UpdatedDate = f.Scope.Clock.Now.UtcDateTime
            });
            await f.Scope.Db.SaveChangesAsync(); f.Scope.Db.ChangeTracker.Clear();
            await using var app = new ExtensionFactory(f);
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<UserExtensionImportService>();
            var work = await service.ReadAsync(f.Scope.UserId, f.Scope.Job.Id, default);
            foreach (var expected in new[] { "yopro mango yoghurt pouch 150g", "yopro mango" })
            {
                var result = new UserExtensionResult(work.StepToken, work.Url!, true, Links: [], EmptyConfirmed: true);
                work = await service.ResultAsync(f.Scope.UserId, work.JobId, result, default);
                Assert.Equal("search", work.Stage);
                Assert.Equal(myshoppinglist_api.Providers.ProductSearchQueryBuilder.SearchUrl("woolworths", expected).AbsoluteUri, work.Url);
                Assert.Equal(work.StepToken, (await service.ResultAsync(f.Scope.UserId, work.JobId, result, default)).StepToken);
            }
            if (ambiguous)
            {
                var links = new[] { "https://www.woolworths.com.au/shop/productdetails/794937", "https://www.woolworths.com.au/shop/productdetails/794938" };
                work = await service.ResultAsync(f.Scope.UserId, work.JobId, new(work.StepToken, work.Url!, true, Links: links), default);
                foreach (var link in links)
                {
                    Assert.Equal(link, work.Url);
                    var json = JsonSerializer.Serialize(new { type = "Product", sku = link.Split('/').Last(), name = "YoPRO Protein Yoghurt Pouch Mango 150g", brand = new { name = "YoPro" }, offers = new { price = 3, priceCurrency = "AUD", url = link } }).Replace("\"type\"", "\"@type\"");
                    work = await service.ResultAsync(f.Scope.UserId, work.JobId, new(work.StepToken, work.Url!, true, new(null, [json])), default);
                }
                Assert.True(await f.Scope.Db.ProductImportRetailerResults.AnyAsync(r => r.ProductImportJobId == work.JobId && r.Status == RetailerLookupStatus.Possible));
            }
            else work = await service.ResultAsync(f.Scope.UserId, work.JobId, new(work.StepToken, work.Url!, true, Links: [], EmptyConfirmed: true), default);
            Assert.Equal("Completed", work.Status);
        }
    }

    [PostgreSqlFact]
    public async Task Browser_import_validates_evidence_owns_steps_reuses_cache_and_survives_duplicate_deliveries()
    {
        await using var f = await ListFixture.CreateAsync();
        var code = Random.Shared.NextInt64(10000000000, 99999999999).ToString();
        var sourceUrl = "https://www.coles.com.au/product/" + code;
        var otherUrl = "https://www.woolworths.com.au/shop/productdetails/" + code;
        await f.Scope.Db.ShopProducts.Where(m => m.ShopProductCode == f.Scope.Code).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.ShopProductCode, code).SetProperty(m => m.ProductUrl, sourceUrl));
        ColesEvidence Evidence(string url) => new(null, [JsonSerializer.Serialize(new {
            @type = "Product", sku = code, name = f.Scope.Source.Identity.Name,
            gtin = f.Scope.Source.Identity.GTIN, brand = new { name = f.Scope.Source.Identity.Brand },
            offers = new { price = 7, priceCurrency = "AUD", url }
        }).Replace("\"type\"", "\"@type\"")]);
        try
        {
            await using var app = new ExtensionFactory(f);
            using var scope = app.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<UserExtensionImportService>();
            var start = new UserExtensionStart(f.Scope.ListId, sourceUrl, 2, Evidence(sourceUrl), Guid.NewGuid());
            var work = await service.StartAsync(f.Scope.UserId, start, default);
            Assert.True(work.SourceSaved); Assert.Equal("search", work.Stage);
            // A manual quantity edit must not cause a fresh Add to be mistaken for
            // a delivery retry of the already-running comparison.
            await f.Scope.Db.ShoppingListProducts.Where(i => i.ShoppingListId == f.Scope.ListId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Quantity, 9));
            var addedAgain = await service.StartAsync(f.Scope.UserId, start with { RequestId = Guid.NewGuid() }, default);
            Assert.NotEqual(work.JobId, addedAgain.JobId);
            Assert.Equal(2, await f.Scope.Db.ShoppingListProducts.Where(i => i.ShoppingListId == f.Scope.ListId).Select(i => i.Quantity).SingleAsync());
            Assert.Equal(404, (await Assert.ThrowsAsync<UserImportException>(() => service.ReadAsync(-1, work.JobId, default))).Status);
            var invalid = new UserExtensionResult(work.StepToken, work.Url!, true, Links: [sourceUrl]);
            Assert.Equal(400, (await Assert.ThrowsAsync<UserImportException>(() => service.ResultAsync(f.Scope.UserId, work.JobId, invalid, default))).Status);
            var search = new UserExtensionResult(work.StepToken, work.Url!, true, Links: [otherUrl]);
            var product = await service.ResultAsync(f.Scope.UserId, work.JobId, search, default);
            Assert.Equal("product", product.Stage);
            Assert.Equal(product.StepToken, (await service.ResultAsync(f.Scope.UserId, work.JobId, search, default)).StepToken);
            var done = await service.ResultAsync(f.Scope.UserId, work.JobId, new(product.StepToken, product.Url!, true, Evidence(otherUrl)), default);
            Assert.Equal("Completed", done.Status);
            Assert.Equal(work.JobId, (await service.StartAsync(f.Scope.UserId, start, default)).JobId);
            var cached = await service.StartAsync(f.Scope.UserId, start with { RequestId = Guid.NewGuid(), Quantity = 5 }, default);
            Assert.Equal("Completed", cached.Status);
            Assert.True(await f.Scope.Db.ProductImportRetailerResults.AnyAsync(r => r.ProductImportJobId == cached.JobId && r.IsFromCache));
            Assert.Equal(1, await f.Scope.Db.ShoppingListProducts.CountAsync(i => i.ShoppingListId == f.Scope.ListId));
            Assert.Equal(5, await f.Scope.Db.ShoppingListProducts.Where(i => i.ShoppingListId == f.Scope.ListId).Select(i => i.Quantity).SingleAsync());
            await service.StartAsync(f.Scope.UserId, start, default);
            Assert.Equal(5, await f.Scope.Db.ShoppingListProducts.Where(i => i.ShoppingListId == f.Scope.ListId).Select(i => i.Quantity).SingleAsync());

            // Once comparison data becomes stale, the browser is asked again; failure preserves the source.
            await f.Scope.Db.ShopProductPrices.Where(p => p.ShopProduct.ShopProductCode == code && p.ShopProduct.Shop.Code == "woolworths")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CheckedDate, f.Scope.Clock.Now.UtcDateTime.AddDays(-8)));
            var stale = await service.StartAsync(f.Scope.UserId, start with { RequestId = Guid.NewGuid() }, default);
            Assert.Equal("search", stale.Stage);
            var failed = await service.ResultAsync(f.Scope.UserId, stale.JobId, new(stale.StepToken, stale.Url!, false), default);
            Assert.True(failed.SourceSaved); Assert.Equal("Failed", failed.Status);
            Assert.Equal("search", (await service.RetryAsync(f.Scope.UserId, stale.JobId, default)).Stage);
            Assert.True(await f.Scope.Db.ContributionObservations.AnyAsync(o => o.UserAccountId == f.Scope.UserId && o.Source == "PersonalAdd" && o.Outcome == "Accepted"));
            await f.Scope.Db.UserAccounts.Where(a => a.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContributionBlocked, true).SetProperty(a => a.IsPaid, true));
            var trusted = await service.RetryAsync(f.Scope.UserId, stale.JobId, default);
            Assert.Equal("Queued", trusted.Status);
            Assert.False(await f.Scope.Db.UserExtensionImportTasks.AnyAsync(t => t.ProductImportJobId == stale.JobId));
            Assert.Equal(ProductImportJobStatus.Queued, await f.Scope.Db.ProductImportJobs.Where(j => j.Id == stale.JobId).Select(j => j.Status).SingleAsync());
            var blockedAdd = await service.StartAsync(f.Scope.UserId, start with { RequestId = Guid.NewGuid(), Evidence = new(null, null) }, default);
            Assert.Equal("Queued", blockedAdd.Status);
        }
        finally
        {
            await f.Scope.Db.ShopProducts.Where(m => m.ShopProductCode == code).ExecuteUpdateAsync(s => s.SetProperty(m => m.ShopProductCode, f.Scope.Code));
        }
    }

    [Fact]
    public async Task Extension_login_exception_requires_configured_id_and_does_not_weaken_website_protection()
    {
        var options = new UserExtensionOptions { AllowedExtensionIds = [ExtensionId] };
        foreach (var (path, id, origin, expected) in new[] {
            ("/api/user-extension/login", ExtensionId, "chrome-extension://" + ExtensionId, true),
            ("/api/user-extension/login", ExtensionId, "https://evil.test", false),
            ("/api/user-extension/login", new string('b', 32), "chrome-extension://" + new string('b', 32), false),
            ("/api/auth/login", ExtensionId, "chrome-extension://" + ExtensionId, false) })
        {
            var services = new ServiceCollection().AddLogging().AddOptions().AddSingleton(Options.Create(options)).BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = "POST"; context.Request.Path = path; context.Request.Scheme = "http"; context.Request.Host = new("localhost:5392");
            context.Request.Headers.Origin = origin; context.Request.Headers["X-MyShoppingList-Extension"] = id;
            context.Request.Headers[CookieRequestProtection.HeaderName] = "1";
            var passed = false;
            await new CookieRequestProtection(_ => { passed = true; return Task.CompletedTask; }).InvokeAsync(context);
            Assert.Equal(expected, passed);
        }
        Assert.False(new UserExtensionOptions().Allows(new DefaultHttpContext().Request));
    }

    [PostgreSqlFact]
    public async Task Extension_login_issues_separate_bearer_session_and_ownership_still_applies()
    {
        await using var f = await ListFixture.CreateAsync();
        var password = "TestPassword1!";
        var email = Guid.NewGuid() + "@example.test";
        var hash = PasswordHasher.Hash(password);
        await f.Scope.Db.UserAccounts.Where(u => u.Id == f.Scope.UserId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.IsTrial, false).SetProperty(u => u.Email, email)
            .SetProperty(u => u.PasswordHash, hash.Hash).SetProperty(u => u.PasswordSalt, hash.Salt));
        await using var app = new ExtensionFactory(f);
        using var client = app.CreateClient(new() { BaseAddress = new("http://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-MyShoppingList-Extension", ExtensionId);
        client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1");
        client.DefaultRequestHeaders.Add("Origin", "chrome-extension://" + ExtensionId);
        var login = await client.PostAsJsonAsync("/api/user-extension/login", new SignInRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));
        Assert.True(login.Headers.CacheControl!.NoStore);
        var payload = await login.Content.ReadFromJsonAsync<JsonElement>();
        var token = payload.GetProperty("token").GetString()!;
        Assert.True(SessionToken.IsValidFormat(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/shopping-lists")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/shopping-lists/9223372036854775807/items")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/shopping-lists/default", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/shopping-lists")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Default_creation_is_idempotent_concurrent_and_owned()
    {
        await using var f = await ListFixture.CreateAsync();
        Assert.Null(await f.Service.EnsureDefaultAsync(-1, default));
        Assert.Equal(f.Scope.ListId, await f.Service.EnsureDefaultAsync(f.Scope.UserId, default));
        await f.Scope.Db.ShoppingLists.Where(l => l.Id == f.Scope.ListId).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsArchived, true));
        async Task<long?> Create()
        {
            await using var db = PersistenceScope.Context();
            return await new ShoppingListService(db, f.Scope.Clock).EnsureDefaultAsync(f.Scope.UserId, default, "Australia/Adelaide");
        }
        var ids = await Task.WhenAll(Create(), Create());
        Assert.NotNull(ids[0]); Assert.Equal(ids[0], ids[1]); Assert.NotEqual(f.Scope.ListId, ids[0]);
        Assert.Equal(1, await f.Scope.Db.ShoppingLists.CountAsync(l => l.UserAccountId == f.Scope.UserId && !l.IsArchived));
        Assert.Equal(ShoppingListNameService.First(f.Scope.Clock.Now.UtcDateTime, "Australia/Adelaide"), await f.Scope.Db.ShoppingLists.Where(l => l.Id == ids[0]).Select(l => l.Name).SingleAsync());
    }

    private sealed class ExtensionFactory(ListFixture fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(fixture.Scope.Clock);
                services.PostConfigure<ProductImportOptions>(o => o.Enabled = false);
                services.PostConfigure<UserExtensionOptions>(o => o.AllowedExtensionIds = [ExtensionId]);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>();
                services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
    }
}
