using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class ColesExtensionTaskTests
{
    [Fact]
    public async Task Evidence_is_bounded_escaped_and_identity_checked_by_server()
    {
        Assert.Null(ColesExtensionTaskService.EvidenceHtml(new("{broken", null)));
        Assert.Null(ColesExtensionTaskService.EvidenceHtml(new(null, new string[21])));
        Assert.Null(ColesExtensionTaskService.EvidenceHtml(new(new string('a', 2000001), null)));
        var html = ColesExtensionTaskService.EvidenceHtml(Evidence("123", "Safe </script><script>bad</script>"))!;
        Assert.DoesNotContain("<script>bad", html);
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await new ColesProductParser().ParseAsync(
            new(new("https://www.coles.com.au/product/999"), html, DateTimeOffset.UtcNow), default));
    }
    [PostgreSqlFact]
    public async Task Product_deduplicates_claims_saves_atomically_and_reuses_fresh_cache()
    {
        await using var f = new Fixture();
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.Url, default));
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(new(f.Url + "?pid=tracking"), default));
        var task = Assert.Single(await f.Db.ColesExtensionTasks.Where(t => t.Key == "product:" + f.Code).ToListAsync());
        f.Db.ChangeTracker.Clear();
        Assert.Equal(0, task.Attempts);
        var claim = Assert.IsType<ColesTaskClaim>(await f.Service.ClaimAsync(task.Id, "worker-test", default));
        Assert.Null(await f.Service.ClaimAsync(task.Id, "other-worker", default));
        Assert.Equal(claim.ClaimToken, (await f.Service.ClaimAsync(task.Id, "worker-test", default))!.ClaimToken);
        var submission = new ColesTaskSubmission("worker-test", claim.ClaimToken, f.Url.AbsoluteUri, true, Evidence(f.Code));
        Assert.Equal("product_identity_conflict", await f.Service.SubmitAsync(task.Id, submission with { Url = "https://www.coles.com.au/product/999" }, default));
        f.Db.ChangeTracker.Clear();
        Assert.False(await f.Db.ShopProducts.AnyAsync(p => p.ShopProductCode == f.Code));
        Assert.Null(await f.Service.SubmitAsync(task.Id, submission, default));
        Assert.Null(await f.Service.SubmitAsync(task.Id, submission, default));
        f.Db.ChangeTracker.Clear();
        Assert.Single(await f.Db.ShopProductPriceHistory.Where(p => p.ShopProduct.ShopProductCode == f.Code).ToListAsync());
        Assert.DoesNotContain(await f.Service.ListAsync(default), t => t.Id == task.Id);
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(await f.Service.ProductAsync(f.Url, default));
        f.Clock.Now += TimeSpan.FromDays(7);
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.Url, default));
        Assert.Equal(0, (await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Attempts);
    }
    [PostgreSqlFact]
    public async Task Offline_waiting_costs_no_attempts_and_failures_stop_after_three_with_manual_retry()
    {
        await using var f = new Fixture();
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.Url, default));
        var id = await f.Db.ColesExtensionTasks.Where(t => t.Key == "product:" + f.Code).Select(t => t.Id).SingleAsync();
        f.Clock.Now += TimeSpan.FromHours(2);
        await f.Service.ListAsync(default);
        Assert.Equal(0, (await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Id == id)).Attempts);
        for (var i = 1; i <= 3; i++)
        {
            var claim = (await f.Service.ClaimAsync(id, "worker-test", default))!;
            Assert.NotNull(claim);
            var failure = new ColesTaskSubmission("worker-test", claim.ClaimToken, f.Url.AbsoluteUri, false, ErrorCode: "network_error");
            Assert.Null(await f.Service.SubmitAsync(id, failure, default));
            Assert.Null(await f.Service.SubmitAsync(id, failure, default));
            f.Db.ChangeTracker.Clear(); f.Clock.Now += TimeSpan.FromMinutes(2);
        }
        Assert.Equal("Failed", (await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Id == id)).Status);
        Assert.Null(await f.Service.ClaimAsync(id, "worker-test", default)); f.Db.ChangeTracker.Clear();
        Assert.True(await f.Service.RetryAsync(id, default));
        var retry = (await f.Service.ClaimAsync(id, "worker-test", default))!;
        f.Clock.Now += TimeSpan.FromMinutes(4);
        Assert.Equal("claim_lost", await f.Service.SubmitAsync(id, new("worker-test", retry.ClaimToken, f.Url.AbsoluteUri, true, Evidence(f.Code)), default));
        f.Db.ChangeTracker.Clear(); await f.Service.ListAsync(default);
        Assert.Equal("Waiting", (await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Id == id)).Status);
    }
    [PostgreSqlFact]
    public async Task Search_queues_all_candidates_and_worker_claims_only_one_at_a_time()
    {
        await using var f = new Fixture(); var query = "queue-test-" + f.Code;
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.SearchAsync(query, default));
        var task = await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Query == query);
        var claim = (await f.Service.ClaimAsync(task.Id, "worker-test", default))!;
        Assert.Equal("invalid_product_link", await f.Service.SubmitAsync(task.Id, new("worker-test", claim.ClaimToken, task.Url, true, Links: ["https://evil.example/product/1"]), default));
        f.Db.ChangeTracker.Clear();
        Assert.Null(await f.Service.SubmitAsync(task.Id, new("worker-test", claim.ClaimToken, task.Url, true, Links: [f.Url.AbsoluteUri, f.OtherUrl.AbsoluteUri]), default));
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.SearchAsync(query, default));
        var candidates = (await f.Service.ListAsync(default)).Where(t => t.Url == f.Url.AbsoluteUri || t.Url == f.OtherUrl.AbsoluteUri).ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.NotNull(await f.Service.ClaimAsync(candidates[0].Id, "worker-test", default));
        Assert.Null(await f.Service.ClaimAsync(candidates[1].Id, "worker-test", default));
    }
    internal static ColesEvidence Evidence(string code, string name = "Queue Test Product 1L") => new(null,
        [JsonSerializer.Serialize(new { @type = "Product", sku = code, name, offers = new { price = 7, priceCurrency = "AUD", url = "https://www.coles.com.au/product/" + code } }).Replace("\"type\"", "\"@type\"")]);
    private sealed class Fixture : IAsyncDisposable
    {
        public MyShoppingListDbContext Db { get; } = PersistenceScope.Context();
        public PersistenceClock Clock { get; } = new() { Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero) };
        public string Code { get; } = Random.Shared.NextInt64(100000000000, 900000000000).ToString();
        public Uri Url => new("https://www.coles.com.au/product/" + Code);
        public Uri OtherUrl => new("https://www.coles.com.au/product/" + (long.Parse(Code) + 1));
        public ColesExtensionTaskService Service { get; }
        public Fixture()
        {
            var normal = new ProductNormalisationService(); var freshness = new CatalogueFreshnessService();
            Service = new(Db, new(Db, freshness, Clock), freshness, new(), new(Db, normal, new(normal)), new(Db),
                new(Db, Options.Create(new PriceOptions()), Clock, new(new RetailerCatalog())), Clock);
        }
        public async ValueTask DisposeAsync()
        {
            Db.ChangeTracker.Clear(); var other = (long.Parse(Code) + 1).ToString();
            await Db.ColesExtensionTasks.Where(t => t.Key == "product:" + Code || t.Key == "product:" + other || t.Query == "queue-test-" + Code).ExecuteDeleteAsync();
            var mappings = await Db.ShopProducts.Where(p => p.ShopProductCode == Code || p.ShopProductCode == other).ToListAsync();
            var ids = mappings.Select(p => p.Id).ToArray(); var products = mappings.Select(p => p.ProductId).ToArray();
            await Db.ShopProductPrices.Where(p => ids.Contains(p.ShopProductId)).ExecuteDeleteAsync();
            await Db.ShopProductPriceHistory.Where(p => ids.Contains(p.ShopProductId)).ExecuteDeleteAsync();
            await Db.ShopProducts.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync();
            await Db.Products.Where(p => products.Contains(p.Id) && !Db.ShopProducts.Any(m => m.ProductId == p.Id)).ExecuteDeleteAsync();
            await Db.DisposeAsync();
        }
    }
}
