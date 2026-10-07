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
    [PostgreSqlFact]
    public async Task In_flight_success_does_not_cancel_pause_and_probe_timeout_does_not_resume_work()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = new Fixture();
        var first = await QueueAsync(f);
        var second = await QueueAsync(f);
        var firstClaim = (await f.Service.ClaimAsync(first, "first", default))!;
        var secondClaim = (await f.Service.ClaimAsync(second, "second", default))!;
        await f.Db.RetailerWorkloadStates.ExecuteUpdateAsync(s => s.SetProperty(x => x.BlockedAt,
            new[] { f.Clock.Now.UtcDateTime, f.Clock.Now.UtcDateTime }));
        Assert.Null(await f.Service.SubmitAsync(first, new("first", firstClaim.ClaimToken, firstClaim.Url, false, ErrorCode: "retailer_access_restricted"), default));
        Assert.Null(await f.Service.SubmitAsync(second, new("second", secondClaim.ClaimToken, secondClaim.Url, true, Links: [], EmptyConfirmed: true), default));
        var waiting = await QueueAsync(f);
        Assert.Null(await f.Service.ClaimAsync(waiting, "next", default));
        f.Clock.Now += TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1);
        Assert.Null(await f.Service.ClaimAsync(waiting, "next", default));
        f.Clock.Now += TimeSpan.FromTicks(1);
        var probe = (await f.Service.ClaimAsync(waiting, "next", default))!;
        Assert.Null(await f.Service.SubmitAsync(waiting, new("next", probe.ClaimToken, probe.Url, false, ErrorCode: "read_timeout"), default));
        Assert.NotNull((await f.Db.RetailerWorkloadStates.AsNoTracking().SingleAsync()).PausedUntil);
        var replacement = await QueueAsync(f);
        Assert.NotNull(await f.Service.ClaimAsync(replacement, "replacement", default));
        Assert.Null(await f.Service.ClaimAsync(await QueueAsync(f), "extra", default));
    }

    private static async Task<long> QueueAsync(Fixture f, string retailer = "coles")
    {
        var task = new myshoppinglist_api.Models.ColesExtensionTask
        {
            Key = retailer + ":test:" + Guid.NewGuid(),
            Kind = "search",
            Url = retailer == "coles" ? "https://www.coles.com.au/search/products?q=test" : "https://www.woolworths.com.au/shop/search/products?searchTerm=test",
            CreatedAt = f.Clock.Now.UtcDateTime
        };
        f.Db.Add(task); await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear(); return task.Id;
    }

    [PostgreSqlFact]
    public async Task Concurrent_workers_share_retailer_capacity_and_expired_leases_release_slots()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = new Fixture();
        var ids = new List<long>();
        for (var i = 0; i < 6; i++) ids.Add(await QueueAsync(f));
        var claims = await Task.WhenAll(ids.Select(async id =>
        {
            await using var db = PersistenceScope.Context();
            return await Fixture.CreateService(db, f.Clock).ClaimAsync(id, "worker-" + id, default);
        }));
        Assert.Equal(2, claims.Count(c => c is not null));
        var wool = await QueueAsync(f, "woolworths");
        Assert.NotNull(await f.Service.ClaimAsync(wool, "wool-worker", default));
        var active = claims.First(c => c is not null)!;
        Assert.Equal(active.ClaimToken, (await f.Service.ClaimAsync(active.Id, "worker-" + active.Id, default))!.ClaimToken);
        Assert.DoesNotContain(await f.Service.ListAsync(default), t => ids.Contains(t.Id));
        f.Clock.Now += TimeSpan.FromMinutes(4);
        var waiting = ids.First(id => !claims.Any(c => c?.Id == id));
        Assert.NotNull(await f.Service.ClaimAsync(waiting, "next-worker", default));
    }

    [PostgreSqlFact]
    public async Task Repeated_blocks_persist_pause_ignore_duplicates_and_resume_only_after_successful_probe()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = new Fixture();
        for (var i = 0; i < 3; i++)
        {
            var id = await QueueAsync(f);
            var claim = (await f.Service.ClaimAsync(id, "worker", default))!;
            var failure = new ColesTaskSubmission("worker", claim.ClaimToken, claim.Url, false, ErrorCode: "retailer_access_restricted");
            Assert.Null(await f.Service.SubmitAsync(id, failure, default));
            Assert.Null(await f.Service.SubmitAsync(id, failure, default));
            Assert.Equal(i + 1, (await f.Db.RetailerWorkloadStates.AsNoTracking().SingleAsync()).BlockedAt.Length);
        }
        var waiting = await QueueAsync(f);
        var another = await QueueAsync(f);
        Assert.Null(await f.Service.ClaimAsync(waiting, "worker", default));
        Assert.DoesNotContain(await f.Service.ListAsync(default), t => t.Id == waiting);
        Assert.Equal(0, (await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Id == waiting)).Attempts);
        Assert.NotNull(await f.Service.ClaimAsync(await QueueAsync(f, "woolworths"), "other-retailer", default));
        await using var restartedDb = PersistenceScope.Context();
        var restarted = Fixture.CreateService(restartedDb, f.Clock);
        Assert.Null(await restarted.ClaimAsync(waiting, "after-restart", default));
        f.Clock.Now += TimeSpan.FromMinutes(15);
        var probe = (await restarted.ClaimAsync(waiting, "probe", default))!;
        Assert.Null(await f.Service.ClaimAsync(another, "second-probe", default));
        Assert.Null(await restarted.SubmitAsync(waiting, new("probe", probe.ClaimToken, probe.Url, false, ErrorCode: "retailer_access_restricted"), default));
        Assert.Null(await f.Service.ClaimAsync(another, "second-probe", default));
        f.Clock.Now += TimeSpan.FromMinutes(15);
        probe = (await f.Service.ClaimAsync(another, "probe", default))!;
        Assert.Null(await f.Service.SubmitAsync(another, new("probe", probe.ClaimToken, probe.Url, true, Links: [], EmptyConfirmed: true), default));
        var state = await f.Db.RetailerWorkloadStates.AsNoTracking().SingleAsync(s => s.Retailer == "coles");
        Assert.Null(state.PausedUntil); Assert.Empty(state.BlockedAt); Assert.Null(state.ProbeToken);
        Assert.NotNull(await f.Service.ClaimAsync(waiting, "resumed", default));
    }

    [PostgreSqlFact]
    public async Task Old_blocks_expire_and_abandoned_probe_can_be_replaced()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = new Fixture();
        f.Db.RetailerWorkloadStates.Add(new() { Retailer = "coles", BlockedAt = [f.Clock.Now.AddMinutes(-11).UtcDateTime, f.Clock.Now.AddMinutes(-11).UtcDateTime] });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var id = await QueueAsync(f);
        var claim = (await f.Service.ClaimAsync(id, "worker", default))!;
        Assert.Null(await f.Service.SubmitAsync(id, new("worker", claim.ClaimToken, claim.Url, false, ErrorCode: "retailer_access_restricted"), default));
        var state = await f.Db.RetailerWorkloadStates.SingleAsync();
        Assert.Single(state.BlockedAt); Assert.Null(state.PausedUntil);
        state.PausedUntil = f.Clock.Now.UtcDateTime;
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var probeId = await QueueAsync(f);
        var probe = (await f.Service.ClaimAsync(probeId, "abandoned", default))!;
        f.Clock.Now += TimeSpan.FromMinutes(4);
        var next = await QueueAsync(f);
        Assert.NotNull(await f.Service.ClaimAsync(next, "replacement", default));
        Assert.Equal("claim_lost", await f.Service.SubmitAsync(probeId, new("abandoned", probe.ClaimToken, probe.Url, true, Links: [], EmptyConfirmed: true), default));
    }
    [PostgreSqlFact]
    public async Task Restricted_task_obeys_cooldowns_duplicates_and_three_attempt_limit()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = new Fixture();
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.Url, default));
        var id = await f.Db.ColesExtensionTasks.Select(t => t.Id).SingleAsync();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claim = Assert.IsType<ColesTaskClaim>(await f.Service.ClaimAsync(id, "worker-test", default));
            var failure = new ColesTaskSubmission("worker-test", claim.ClaimToken, f.Url.AbsoluteUri, false,
                ErrorCode: "retailer_access_restricted");
            var submittedAt = f.Clock.Now;
            Assert.Null(await f.Service.SubmitAsync(id, failure, default));
            f.Clock.Now += TimeSpan.FromSeconds(1);
            Assert.Null(await f.Service.SubmitAsync(id, failure, default));
            var saved = await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Id == id);
            Assert.Equal(attempt, saved.Attempts);
            Assert.Equal("retailer_access_restricted", saved.ErrorCode);
            Assert.Null(saved.LeaseExpiresAt);
            if (attempt == 3)
            {
                Assert.Equal("Failed", saved.Status);
                Assert.NotNull(saved.CompletedAt);
                Assert.Null(saved.NextAttemptAt);
                Assert.Null(await f.Service.ClaimAsync(id, "another-worker", default));
                await Assert.ThrowsAsync<ColesWorkFailedException>(() => f.Service.ProductAsync(f.Url, default));
                break;
            }
            var due = submittedAt.AddMinutes(attempt == 1 ? 5 : 15);
            Assert.Equal("Waiting", saved.Status);
            Assert.Equal(due.UtcDateTime, saved.NextAttemptAt);
            Assert.Null(saved.CompletedAt);
            f.Clock.Now = due.AddTicks(-1);
            Assert.DoesNotContain(await f.Service.ListAsync(default), t => t.Id == id);
            Assert.Null(await f.Service.ClaimAsync(id, "another-worker", default));
            f.Clock.Now = due;
            Assert.Contains(await f.Service.ListAsync(default), t => t.Id == id);
        }
    }

    [PostgreSqlFact]
    public async Task Restricted_task_can_succeed_after_cooldown_and_reuse_fresh_price()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = new Fixture();
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.Url, default));
        var id = await f.Db.ColesExtensionTasks.Select(t => t.Id).SingleAsync();
        var claim = Assert.IsType<ColesTaskClaim>(await f.Service.ClaimAsync(id, "worker-test", default));
        Assert.Null(await f.Service.SubmitAsync(id, new("worker-test", claim.ClaimToken, f.Url.AbsoluteUri,
            false, ErrorCode: "retailer_access_restricted"), default));
        f.Clock.Now += TimeSpan.FromMinutes(5);
        var retry = Assert.IsType<ColesTaskClaim>(await f.Service.ClaimAsync(id, "another-worker", default));
        Assert.Null(await f.Service.SubmitAsync(id, new("another-worker", retry.ClaimToken, f.Url.AbsoluteUri,
            true, Evidence(f.Code)), default));
        var saved = await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal("Completed", saved.Status);
        Assert.Null(saved.ErrorCode);
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(await f.Service.ProductAsync(f.Url, default));
        Assert.Equal(7m, (await f.Db.ShopProductPrices.SingleAsync()).Price);
    }

    [PostgreSqlFact]
    public async Task Priority_orders_existing_worker_tasks_and_submission_still_persists_prices()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = new Fixture();
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.Url, default));
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.OtherUrl, default));
        var otherKey = ColesExtensionTaskService.ProductKey(f.OtherUrl);
        await f.Db.ColesExtensionTasks.Where(t => t.Key == otherKey).ExecuteUpdateAsync(s => s.SetProperty(t => t.Priority, 2));
        var tasks = (await f.Service.ListAsync(default)).Where(t => t.Url == f.Url.AbsoluteUri || t.Url == f.OtherUrl.AbsoluteUri).ToArray();
        Assert.Equal(f.OtherUrl.AbsoluteUri, tasks[0].Url);
        var claim = (await f.Service.ClaimAsync(tasks[0].Id, "refresh-test", default))!;
        var code = (long.Parse(f.Code) + 1).ToString();
        Assert.Null(await f.Service.SubmitAsync(claim.Id, new("refresh-test", claim.ClaimToken, f.OtherUrl.AbsoluteUri, true, Evidence(code)), default));
        Assert.Equal(7m, (await f.Db.ShopProductPrices.SingleAsync(p => p.ShopProduct.ShopProductCode == code)).Price);
    }

    [PostgreSqlFact]
    public async Task Result_wakes_waiting_import_and_result_before_deferral_is_not_lost()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var imports = await ImportFixture.CreateAsync();
        await using var f = new Fixture();
        f.Clock.Now = imports.Scope.Clock.Now;
        var jobs = imports.Jobs();
        var claim = (await jobs.ClaimNextAsync(default))!;
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.Url, default));
        var task = await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Key == ColesExtensionTaskService.ProductKey(f.Url));
        var worker = (await f.Service.ClaimAsync(task.Id, "worker-test", default))!;
        Assert.Equal(1, await jobs.DeferForExtensionAsync(claim, default));
        Assert.True((await imports.ReadAsync()).NextAttemptDate > imports.Scope.Clock.Now.UtcDateTime);
        f.Clock.Now += TimeSpan.FromSeconds(1);
        imports.Scope.Clock.Now = f.Clock.Now;
        Assert.Null(await f.Service.SubmitAsync(task.Id, new("worker-test", worker.ClaimToken, f.Url.AbsoluteUri, true, Evidence(f.Code)), default));
        Assert.True((await imports.ReadAsync()).NextAttemptDate <= imports.Scope.Clock.Now.UtcDateTime);

        claim = (await jobs.ClaimNextAsync(default))!;
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.OtherUrl, default));
        task = await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Key == ColesExtensionTaskService.ProductKey(f.OtherUrl));
        worker = (await f.Service.ClaimAsync(task.Id, "worker-test", default))!;
        f.Clock.Now += TimeSpan.FromSeconds(1);
        imports.Scope.Clock.Now = f.Clock.Now;
        Assert.Null(await f.Service.SubmitAsync(task.Id, new("worker-test", worker.ClaimToken, f.OtherUrl.AbsoluteUri, true, Evidence((long.Parse(f.Code) + 1).ToString())), default));
        Assert.Equal(1, await jobs.DeferForExtensionAsync(claim, default));
        Assert.True((await imports.ReadAsync()).NextAttemptDate <= imports.Scope.Clock.Now.UtcDateTime);
    }

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
    [Fact]
    public async Task Woolworths_evidence_roundtrips_and_task_keys_cannot_collide_with_Coles()
    {
        var url = new Uri("https://www.woolworths.com.au/shop/productdetails/916772");
        Assert.NotEqual(ColesExtensionTaskService.ProductKey(url), ColesExtensionTaskService.ProductKey(new("https://www.coles.com.au/product/916772")));
        var main = JsonSerializer.Serialize(new { Stockcode = 916772, DisplayName = "Panadol Rapid 16 pack", Price = 6, Unit = "Each" });
        var ld = """{"@type":"Product","sku":"916772","name":"Panadol Rapid 16 pack","offers":{"price":6,"priceCurrency":"AUD"}}""";
        var html = ColesExtensionTaskService.EvidenceHtml(new(main, [ld]), "woolworths")!;
        var parser = new myshoppinglist_api.Providers.Woolworths.WoolworthsProductParser();
        var result = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(await parser.ParseAsync(new(url, html, DateTimeOffset.UtcNow), default));
        Assert.Equal("woolworths", result.Value.ShopCode);
        Assert.Equal(6m, Assert.IsType<ProviderResult<ShopProductOffer>.Success>(result.Value.Offer).Value.Price);
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await parser.ParseAsync(new(new("https://www.woolworths.com.au/shop/productdetails/999"), html, DateTimeOffset.UtcNow), default));
    }
    [PostgreSqlFact]
    public async Task Woolworths_queue_rejects_Coles_links_and_persists_under_Woolworths()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = new Fixture();
        IWoolworthsExtensionQueue queue = f.Service;
        var query = "queue-test-" + f.Code;
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => queue.SearchAsync(query, default));
        var search = await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Query == query);
        var claim = (await f.Service.ClaimAsync(search.Id, "worker-test", default))!;
        Assert.Equal("invalid_product_link", await f.Service.SubmitAsync(search.Id, new("worker-test", claim.ClaimToken, search.Url, true, Links: [f.Url.AbsoluteUri]), default));
        f.Db.ChangeTracker.Clear();
        var url = new Uri("https://www.woolworths.com.au/shop/productdetails/" + f.Code);
        Assert.Null(await f.Service.SubmitAsync(search.Id, new("worker-test", claim.ClaimToken, search.Url, true, Links: [url.AbsoluteUri]), default));
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => queue.SearchAsync(query, default));
        var task = await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Key == ColesExtensionTaskService.ProductKey(url));
        var productClaim = (await f.Service.ClaimAsync(task.Id, "worker-test", default))!;
        var evidence = Evidence(f.Code) with { JsonLd = [Evidence(f.Code).JsonLd![0].Replace(f.Url.AbsoluteUri, url.AbsoluteUri)] };
        Assert.Null(await f.Service.SubmitAsync(task.Id, new("worker-test", productClaim.ClaimToken, url.AbsoluteUri, true, evidence), default));
        var product = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(await queue.ProductAsync(url, default));
        Assert.Equal("woolworths", product.Value.ShopCode);
        Assert.Single(await f.Db.ShopProducts.Where(p => p.ShopProductCode == f.Code && p.Shop.Code == "woolworths").ToListAsync());
    }
    [PostgreSqlFact]
    public async Task Product_deduplicates_claims_saves_atomically_and_reuses_fresh_cache()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
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
        await using var database = await IsolatedDatabaseScope.CreateAsync();
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
        await using var database = await IsolatedDatabaseScope.CreateAsync();
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
    [PostgreSqlFact]
    public async Task Server_rejection_reason_is_preserved_for_worker_retry_display()
    {
        await using var database = await IsolatedDatabaseScope.CreateAsync();
        await using var f = new Fixture();
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => f.Service.ProductAsync(f.Url, default));
        var task = await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Key == ColesExtensionTaskService.ProductKey(f.Url));
        var claim = (await f.Service.ClaimAsync(task.Id, "worker-test", default))!;
        Assert.Null(await f.Service.SubmitAsync(task.Id, new("worker-test", claim.ClaimToken, f.Url.AbsoluteUri, false, ErrorCode: "same_barcode_conflict"), default));
        var failed = await f.Db.ColesExtensionTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal("Failed", failed.Status);
        Assert.Equal("same_barcode_conflict", failed.ErrorCode);
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
            Service = CreateService(Db, Clock);
        }
        public static ColesExtensionTaskService CreateService(MyShoppingListDbContext db, PersistenceClock clock)
        {
            var normal = new ProductNormalisationService(); var freshness = new CatalogueFreshnessService();
            return new(db, new(db, freshness, clock), freshness, new(), new(db, normal, new(normal)), new(db),
                new(db, Options.Create(new PriceOptions()), clock, new(new RetailerCatalog())), clock);
        }
        public async ValueTask DisposeAsync()
        {
            Db.ChangeTracker.Clear(); var other = (long.Parse(Code) + 1).ToString();
            await Db.ColesExtensionTasks.Where(t => t.Key == "woolworths:product:" + Code || t.Key == "product:" + Code || t.Key == "product:" + other || t.Query == "queue-test-" + Code).ExecuteDeleteAsync();
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
