using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class PersonalRetailerComparisonTests
{
    private const string Path = "/api/user-extension/comparisons";
    private const string Woolworths = "https://www.woolworths.com.au/shop/productdetails/123456";
    [Theory]
    [InlineData("NoMatch", false, "no_search_results")]
    [InlineData("NoMatch", true, "candidates_rejected")]
    [InlineData("UnableToCheck", true, "unable_to_check")]
    [InlineData("Partial", true, "unable_to_check")]
    [InlineData("NeedsVerification", false, "verification_required")]
    [InlineData("Alternatives", true, "no_confirmed_exact_match")]
    public void Unsuccessful_outcomes_pass_the_log_filter_with_only_attempted_searches(string status, bool seen, string outcome)
    {
        using var logs = new ComparisonLogs();
        var retailer = new PersonalRetailerComparisonService.RetailerState {
            Code = "woolworths", Status = status, Queries = ["acme coffee", "unused fallback"], QueryIndex = 1,
            Seen = seen ? [Woolworths] : [], Url = Woolworths
        };
        if (status == "NoMatch" && seen) retailer.RejectedCandidates.Add(new(Woolworths, "contradictory_identity"));
        if (status is "UnableToCheck" or "Partial" or "NeedsVerification")
            PersonalRetailerComparisonService.RecordPageFailure(retailer, "page_timeout");
        LogOutcome(logs, retailer, new Dictionary<string, string> { [retailer.Code] = "Checking" });
        var entry = Assert.Single(logs.Sink.Entries);
        Assert.Equal(LogEventLevel.Information, entry.Level);
        Assert.Equal(outcome, Assert.IsType<ScalarValue>(entry.Properties["Outcome"]).Value);
        Assert.Equal("coles", Assert.IsType<ScalarValue>(entry.Properties["SourceRetailer"]).Value);
        Assert.Equal("woolworths", Assert.IsType<ScalarValue>(entry.Properties["CompetitorRetailer"]).Value);
        Assert.Equal(9L, Assert.IsType<ScalarValue>(entry.Properties["ProductId"]).Value);
        Assert.Single(Assert.IsType<SequenceValue>(entry.Properties["SearchQueries"]).Elements);
        Assert.DoesNotContain("unused fallback", entry.RenderMessage());
        Assert.DoesNotContain("private list notes", entry.RenderMessage());
        if (seen) Assert.Contains(Woolworths, entry.RenderMessage());
        if (status == "NoMatch" && seen) Assert.Contains("contradictory_identity", entry.RenderMessage());
        if (status is "UnableToCheck" or "Partial" or "NeedsVerification") Assert.Contains("page_timeout", entry.RenderMessage());
    }

    [Fact]
    public void Replayed_outcomes_do_not_log_again_but_a_failed_retry_does()
    {
        using var logs = new ComparisonLogs();
        var retailer = new PersonalRetailerComparisonService.RetailerState { Code = "aldi", Status = "UnableToCheck" };
        LogOutcome(logs, retailer, new Dictionary<string, string> { ["aldi"] = "Checking" });
        LogOutcome(logs, retailer, new Dictionary<string, string> { ["aldi"] = "UnableToCheck" });
        Assert.Single(logs.Sink.Entries);
        foreach (var status in new[] { "Pending", "Checking", "Exact", "Cancelled" })
        {
            retailer.Status = status;
            LogOutcome(logs, retailer, new Dictionary<string, string>());
        }
        Assert.Single(logs.Sink.Entries);
        retailer.Status = "UnableToCheck";
        LogOutcome(logs, retailer, new Dictionary<string, string> { ["aldi"] = "Checking" });
        Assert.Equal(2, logs.Sink.Entries.Count);
    }

    [Fact]
    public void Page_diagnostics_are_bounded_deduplicated_and_exclude_client_error_text()
    {
        var retailer = new PersonalRetailerComparisonService.RetailerState { Code = "aldi", Url = "https://www.aldi.com.au/results?q=coffee" };
        PersonalRetailerComparisonService.RecordPageFailure(retailer, "page_timeout");
        PersonalRetailerComparisonService.RecordPageFailure(retailer, "page_timeout");
        Assert.Single(retailer.PageFailures);
        foreach (var error in new[] { "secret token\r\nraw content", "secret_token_value", new string('x', 65), "", null })
            PersonalRetailerComparisonService.RecordPageFailure(retailer, error);
        Assert.Equal("unknown_error", retailer.PageFailures[1].ErrorCode);
        Assert.Equal(2, retailer.PageFailures.Count);
        for (var n = 0; n < 30; n++)
        {
            retailer.Url = $"https://www.aldi.com.au/product/coffee-{n}";
            PersonalRetailerComparisonService.RecordPageFailure(retailer, "page_timeout");
        }
        Assert.Equal(20, retailer.PageFailures.Count);
        using var logs = new ComparisonLogs();
        retailer.Status = "UnableToCheck";
        LogOutcome(logs, retailer, new Dictionary<string, string>());
        Assert.DoesNotContain("secret token", Assert.Single(logs.Sink.Entries).RenderMessage());
        var legacy = JsonSerializer.Deserialize<PersonalRetailerComparisonService.RetailerState>("{\"Code\":\"aldi\",\"Status\":\"NoMatch\"}")!;
        Assert.Empty(legacy.PageFailures); Assert.Empty(legacy.RejectedCandidates);
    }

    [PostgreSqlFact]
    public async Task Rejections_and_failures_log_after_commit_and_result_replays_do_not_duplicate_logs()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        using var logs = new ComparisonLogs();
        var saved = (await (await client.PostAsJsonAsync("/api/user-extension/captures", f.Request()))
            .Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        using var scope = f.Api.Services.CreateScope();
        var service = ActivatorUtilities.CreateInstance<PersonalRetailerComparisonService>(scope.ServiceProvider, logs.Logger);
        var run = await service.StartAsync(f.First.Id, saved.JobId, default);
        var committedStatuses = new List<(string Logged, string Saved)>();
        logs.Sink.OnEmit = entry => {
            var stored = f.Db.PersonalRetailerComparisons.AsNoTracking().Single(r => r.Id == run.Id);
            var state = JsonSerializer.Deserialize<PersonalRetailerComparisonService.RunState>(stored.StateJson)!;
            var code = (string)((ScalarValue)entry.Properties["CompetitorRetailer"]).Value!;
            committedStatuses.Add(((string)((ScalarValue)entry.Properties["Status"]).Value!, state.Retailers.Single(r => r.Code == code).Status));
        };
        run = await service.UpdateAsync(f.First.Id, run.Id, "result", new(run.Work!.StepToken, run.Work.Url, true, Links: [Woolworths]), default);
        var evidence = CaptureApiScope.Evidence(Woolworths);
        evidence = evidence with { JsonLd = [evidence.JsonLd![0].Replace("Original Coffee", "Cherry Coffee")] };
        run = await service.UpdateAsync(f.First.Id, run.Id, "result",
            new(run.Work!.StepToken, run.Work.Url, true, evidence, ObservedAt: f.Clock.Now.AddSeconds(-1)), default);
        Assert.Empty(logs.Sink.Entries);
        PersonalComparisonResult? last = null;
        while (run.Work?.Retailer == "woolworths")
        {
            last = new(run.Work.StepToken, run.Work.Url, true, Links: [], EmptyConfirmed: true);
            run = await service.UpdateAsync(f.First.Id, run.Id, "result", last, default);
        }
        Assert.NotNull(last);
        Assert.Contains("contradictory_identity", Assert.Single(logs.Sink.Entries).RenderMessage());
        await service.UpdateAsync(f.First.Id, run.Id, "result", last, default);
        Assert.Single(logs.Sink.Entries);
        while (run.Work is { } work)
        {
            last = new(work.StepToken, work.Url, false, ErrorCode: "page_timeout");
            run = await service.UpdateAsync(f.First.Id, run.Id, "result", last, default);
        }
        Assert.Equal(2, logs.Sink.Entries.Count);
        Assert.Contains("unable_to_check", logs.Sink.Entries[1].RenderMessage());
        await service.UpdateAsync(f.First.Id, run.Id, "result", last, default);
        Assert.Equal(2, logs.Sink.Entries.Count);
        Assert.Equal(2, committedStatuses.Count);
        Assert.All(committedStatuses, pair => Assert.Equal(pair.Logged, pair.Saved));
    }
    [Theory]
    [InlineData("Cheezels Cheese Box 125g", "Cheezels Cheese Snacks Share Pack 125g", "Cheezels", 125, "g", 1)]
    [InlineData("Zooper Dooper Cosmic Ice Blocks Mix 70mL x 24 pack", "Zooper Dooper 8 Cosmic Flavours Water Ice 70mL Tubes 24 Pack", "Zooper Dooper", 70, "mL", 24)]
    public async Task Different_retailer_titles_remain_visible_without_becoming_exact_matches(string source, string target, string brand, int size, string unit, int count)
    {
        var original = new ProductIdentity { Name = source, Brand = brand, PackSize = size, PackUnit = unit, PackQuantity = count };
        var candidate = original with { Name = target };
        Assert.Equal("Unconfirmed match", PersonalRetailerComparisonService.Relationship(original, candidate));
        Assert.Equal("Unconfirmed match", PersonalRetailerComparisonService.Relationship(candidate, original));
        Assert.Equal("Exact", PersonalRetailerComparisonService.Relationship(original with { GTIN = "9300675014779" },
            candidate with { GTIN = "9300675014779" }));
        static RetailerPage Page(string name, string brand, string url)
        {
            var json = JsonSerializer.Serialize(new { type = "Product", name, sku = "123456", brand = new { name = brand },
                offers = new { url, price = 3.5m, priceCurrency = "AUD" } }).Replace("\"type\"", "\"@type\"");
            return new(new(url), $"<script type='application/ld+json'>{json}</script>", DateTimeOffset.UtcNow);
        }
        var woolworths = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(await new WoolworthsProductParser()
            .ParseAsync(Page(source, brand, Woolworths), default)).Value;
        var coles = Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(await new ColesProductParser()
            .ParseAsync(Page(target, brand, "https://www.coles.com.au/product/123456"), default)).Value;
        Assert.Equal("Unconfirmed match", PersonalRetailerComparisonService.Relationship(woolworths.Identity, coles.Identity));
        Assert.Equal(3.5m, Assert.IsType<ProviderResult<ShopProductOffer>.Success>(coles.Offer).Value.Price);
        Assert.Equal(2, ProductSearchQueryBuilder.BuildComparisonSearches(woolworths.Identity).Count);
    }

    [Fact]
    public void Packaging_fallback_rejects_conflicting_flavours_packs_barcodes_and_unrelated_products()
    {
        var original = new ProductIdentity { Name = "Cheezels Cheese Box 125g", Brand = "Cheezels", PackSize = 125, PackUnit = "g", PackQuantity = 1 };
        var candidate = original with { Name = "Cheezels Cheese Snacks Share Pack 125g" };
        foreach (var wrong in new[] {
            candidate with { Name = "Cheezels Chicken Snacks Share Pack 125g" },
            candidate with { Name = "Cheezels Cheese Snacks Share Pack 190g", PackSize = 190 },
            candidate with { PackQuantity = 6 }, candidate with { PackUnit = "mL" },
            candidate with { Brand = "Other" }, candidate with { Variant = "Spicy" },
            candidate with { Name = "Cheezels Cheese Corn Chips 125g" }, candidate with { PackSize = null },
            candidate with { Name = "Cheezels Cheese Snacks No Sugar Share Pack 125g" },
            candidate with { Name = "Cheezels Cheese Snacks Multipack 125g" }
        }) Assert.Null(PersonalRetailerComparisonService.Relationship(original, wrong));
        Assert.Null(PersonalRetailerComparisonService.Relationship(original with { GTIN = "9300675014779" },
            candidate with { GTIN = "4006381333931" }));
        var cosmic = new ProductIdentity { Name = "Zooper Dooper 8 Cosmic Flavours Ice Blocks Mix 70mL x 24 pack",
            Brand = "Zooper Dooper", PackSize = 70, PackUnit = "mL", PackQuantity = 24 };
        var ice = cosmic with { Name = "Zooper Dooper 12 Cosmic Flavours Water Ice 70mL Tubes 24 Pack" };
        Assert.Null(PersonalRetailerComparisonService.Relationship(cosmic, ice));
        Assert.Null(PersonalRetailerComparisonService.Relationship(cosmic, ice with { Name = ice.Name.Replace("Cosmic", "Cola") }));
        Assert.Null(PersonalRetailerComparisonService.Relationship(original with { Name = "Cheezels Box 125g" },
            candidate with { Name = "Cheezels Snacks Share Pack 125g" }));
    }

    [PostgreSqlFact]
    public async Task Branded_fallback_surfaces_Coles_prices_as_unconfirmed_without_saving_exact_mappings()
    {
        foreach (var (source, target, brand, sourceCode, targetCode, size, unit) in new[] {
            ("Cheezels Cheese Box 125g", "Cheezels Cheese Snacks Share Pack 125g", "Cheezels", "660337", "7879468", 125, "g"),
            ("Zooper Dooper Cosmic Ice Blocks Mix 70mL x 24 pack", "Zooper Dooper 8 Cosmic Flavours Water Ice 70mL Tubes 24 Pack", "Zooper Dooper", "21662", "198393", 70, "mL") })
        {
            await using var f = await CaptureApiScope.CreateAsync();
            using var client = await f.ClientAsync(f.First.Id);
            var sourceUrl = $"https://www.woolworths.com.au/shop/productdetails/{sourceCode}";
            var targetUrl = $"https://www.coles.com.au/product/{targetCode}";
            static ColesEvidence Evidence(string name, string brand, string url, string sku, decimal price) => new(null,
                [JsonSerializer.Serialize(new { type = "Product", name, sku, brand = new { name = brand },
                    offers = new { url, price, priceCurrency = "AUD" } }).Replace("\"type\"", "\"@type\"")]);
            var request = f.Request("woolworths") with { Url = sourceUrl, Evidence = Evidence(source, brand, sourceUrl, sourceCode, 4m) };
            var capture = await client.PostAsJsonAsync("/api/user-extension/captures", request);
            capture.EnsureSuccessStatusCode();
            var saved = (await capture.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
            var start = await client.PostAsJsonAsync(Path, new PersonalComparisonStart(saved.JobId));
            start.EnsureSuccessStatusCode();
            var run = (await start.Content.ReadFromJsonAsync<PersonalComparisonResponse>())!;
            var queries = ProductSearchQueryBuilder.BuildComparisonSearches(new() { Name = source, Brand = brand, PackSize = size, PackUnit = unit });
            Assert.Equal(2, queries.Count);
            Assert.Equal(ProductSearchQueryBuilder.SearchUrl("coles", queries[0]).AbsoluteUri, run.Work!.Url);
            run = await Result(client, run.Id, new(run.Work.StepToken, run.Work.Url, true, Links: [], EmptyConfirmed: true));
            Assert.Equal(ProductSearchQueryBuilder.SearchUrl("coles", queries[1]).AbsoluteUri, run.Work!.Url);
            run = await Result(client, run.Id, new(run.Work.StepToken, run.Work.Url, true, Links: [targetUrl]));
            run = await Result(client, run.Id, new(run.Work!.StepToken, run.Work.Url, true,
                Evidence(target, brand, targetUrl, targetCode, 3.5m), ObservedAt: f.Clock.Now.AddSeconds(-1)));
            var coles = run.Retailers.Single(r => r.Code == "coles");
            Assert.Equal("Alternatives", coles.Status);
            Assert.Empty(coles.ExactMatches);
            var candidate = Assert.Single(coles.Alternatives);
            Assert.Equal("Unconfirmed match", candidate.Relationship);
            Assert.Equal(3.5m, candidate.Price);
            Assert.Equal(targetUrl, candidate.Url);
            Assert.Single(await f.Db.ShopProducts.ToArrayAsync());
            Assert.Single(await f.Db.ShopProductPriceHistory.ToArrayAsync());
            Assert.Equal(saved.ShoppingListProductId, (await f.Db.ShoppingListProducts.SingleAsync()).Id);
        }
    }
    [PostgreSqlFact]
    public async Task Each_source_checks_only_the_other_two_retailers_and_preserves_the_saved_item()
    {
        foreach (var source in new[] { "coles", "woolworths", "aldi" })
        {
            await using var f = await CaptureApiScope.CreateAsync();
            using var client = await f.ClientAsync(f.First.Id);
            var capture = await client.PostAsJsonAsync("/api/user-extension/captures", f.Request(source));
            capture.EnsureSuccessStatusCode();
            var saved = (await capture.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
            Assert.True(saved.ComparisonAvailable);
            var start = await client.PostAsJsonAsync(Path, new PersonalComparisonStart(saved.JobId));
            start.EnsureSuccessStatusCode();
            var run = (await start.Content.ReadFromJsonAsync<PersonalComparisonResponse>())!;
            var expected = new[] { "coles", "woolworths", "aldi" }.Where(r => r != source).ToArray();
            Assert.Equal(expected, run.Retailers.Select(r => r.Code));
            var visited = new List<string>();
            while (run.Work is { } work)
            {
                Assert.DoesNotContain(source, work.Retailer);
                Assert.Equal("search", work.Kind);
                Assert.Equal($"www.{work.Retailer}.com.au", new Uri(work.Url).Host);
                if (!visited.Contains(work.Retailer)) visited.Add(work.Retailer);
                run = await Result(client, run.Id, new(work.StepToken, work.Url, true, Links: [], EmptyConfirmed: true));
            }
            Assert.Equal(expected, visited);
            Assert.Equal("Completed", run.Status);
            Assert.All(run.Retailers, r => Assert.Equal("NoMatch", r.Status));
            Assert.Equal(saved.ShoppingListProductId, (await f.Db.ShoppingListProducts.SingleAsync()).Id);
            Assert.Single(await f.Db.ShopProducts.ToArrayAsync());
            Assert.Single(await f.Db.ShopProductPrices.ToArrayAsync());
        }
    }
    [PostgreSqlFact]
    public async Task Woolworths_capture_saves_a_private_Coles_exact_match_before_checking_Aldi()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var capture = await client.PostAsJsonAsync("/api/user-extension/captures", f.Request("woolworths"));
        capture.EnsureSuccessStatusCode();
        var saved = (await capture.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        var start = await client.PostAsJsonAsync(Path, new PersonalComparisonStart(saved.JobId));
        start.EnsureSuccessStatusCode();
        var run = (await start.Content.ReadFromJsonAsync<PersonalComparisonResponse>())!;
        Assert.Equal("coles", run.Work!.Retailer);
        const string coles = "https://www.coles.com.au/product/123456";
        run = await Result(client, run.Id, new(run.Work.StepToken, run.Work.Url, true, Links: [coles]));
        Assert.Equal("product", run.Work!.Kind);
        run = await Result(client, run.Id, new(run.Work.StepToken, run.Work.Url, true,
            CaptureApiScope.Evidence(coles, 3m), ObservedAt: f.Clock.Now.AddSeconds(-1)));
        Assert.Equal("Exact", run.Retailers[0].Status);
        Assert.Equal("aldi", run.Work!.Retailer);
        Assert.Equal(2, await f.Db.ShopProducts.CountAsync(m => m.UserAccountId == f.First.Id));
        Assert.Equal(2, await f.Db.ShopProductPriceHistory.CountAsync());
        Assert.Single(await f.Db.ShoppingListProducts.ToArrayAsync());
        Assert.Empty(await f.Db.ShopProducts.Where(m => m.UserAccountId == null).ToArrayAsync());
    }
    [Fact]
    public void Exact_and_alternative_classification_preserves_brand_size_and_variety()
    {
        var rice = new ProductIdentity { Name = "Coles Basmati Rice 5kg", Brand = "Coles", PackSize = 5, PackUnit = "kg", PackQuantity = 1 };
        Assert.Equal("Different brand", PersonalRetailerComparisonService.Relationship(rice,
            rice with { Name = "Imperial Grain Basmati Rice 5kg", Brand = "Imperial Grain" }));
        Assert.Null(PersonalRetailerComparisonService.Relationship(rice,
            rice with { Name = "Imperial Grain Jasmine Rice 5kg", Brand = "Imperial Grain" }));
        Assert.NotEqual("Exact", PersonalRetailerComparisonService.Relationship(rice, rice with { PackSize = 1, Name = "Coles Basmati Rice 1kg" }));
        Assert.Equal("Unconfirmed match · brand unavailable", PersonalRetailerComparisonService.Relationship(rice,
            rice with { Name = "Basmati Rice 5kg", Brand = null }));
        var coffee = new ProductIdentity { Name = "Acme Original Coffee 500g", Brand = "Acme", GTIN = "9300000000002" };
        Assert.Equal("Exact", PersonalRetailerComparisonService.Relationship(coffee, coffee));
        Assert.NotEqual("Exact", PersonalRetailerComparisonService.Relationship(coffee, coffee with { GTIN = "9300000000019" }));
    }
    [PostgreSqlFact]
    public async Task Committed_source_survives_checks_exact_mapping_is_private_and_steps_replay_safely()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var captureResponse = await client.PostAsJsonAsync("/api/user-extension/captures", f.Request());
        captureResponse.EnsureSuccessStatusCode();
        var saved = (await captureResponse.Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        Assert.True(saved.ComparisonAvailable);
        Assert.Single(await f.Db.ShoppingListProducts.ToArrayAsync());
        var start = new PersonalComparisonStart(saved.JobId);
        var response = await client.PostAsJsonAsync(Path, start); response.EnsureSuccessStatusCode();
        var run = (await response.Content.ReadFromJsonAsync<PersonalComparisonResponse>())!;
        var repeated = (await (await client.PostAsJsonAsync(Path, start)).Content.ReadFromJsonAsync<PersonalComparisonResponse>())!;
        Assert.Equal(run.Id, repeated.Id);
        var search = new PersonalComparisonResult(run.Work!.StepToken, run.Work.Url, true, Links: [Woolworths]);
        run = await Result(client, run.Id, search);
        Assert.Equal("product", run.Work!.Kind);
        var evidence = new PersonalComparisonResult(run.Work.StepToken, run.Work.Url, true,
            CaptureApiScope.Evidence(Woolworths, 3m), ObservedAt: f.Clock.Now.AddSeconds(-1));
        var after = await Result(client, run.Id, evidence);
        var replay = await Result(client, run.Id, evidence);
        Assert.Equal(after.Work!.StepToken, replay.Work!.StepToken);
        Assert.Equal("aldi", after.Work.Retailer);
        Assert.Equal("Exact", after.Retailers[0].Status);
        Assert.Equal(2, await f.Db.ShopProducts.CountAsync(m => m.UserAccountId == f.First.Id));
        Assert.Equal(2, await f.Db.ShopProductPriceHistory.CountAsync());
        Assert.Single(await f.Db.Products.ToArrayAsync());
        Assert.Single(await f.Db.ShoppingListProducts.ToArrayAsync());
        Assert.Empty(await f.Db.ShopProducts.Where(m => m.UserAccountId == null).ToArrayAsync());
        var cancel = await client.PostAsJsonAsync($"{Path}/{run.Id}/cancel", new { }); cancel.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Path}/{run.Id}/result",
            new PersonalComparisonResult(after.Work.StepToken, after.Work.Url, true, Links: [], EmptyConfirmed: true))).StatusCode);
        Assert.Single(await f.Db.ShoppingListProducts.ToArrayAsync());
        var retry = (await (await client.PostAsJsonAsync($"{Path}/{run.Id}/retry", new { })).Content.ReadFromJsonAsync<PersonalComparisonResponse>())!;
        Assert.Equal("Exact", retry.Retailers[0].Status); Assert.Equal("aldi", retry.Work!.Retailer);
    }
    [PostgreSqlFact]
    public async Task Account_boundaries_allowlist_and_failed_checks_preserve_the_saved_source()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        using var other = await f.ClientAsync(f.Second.Id);
        var saved = (await (await client.PostAsJsonAsync("/api/user-extension/captures", f.Request())).Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(Path, new PersonalComparisonStart(saved.JobId))).StatusCode);
        var run = (await (await client.PostAsJsonAsync(Path, new PersonalComparisonStart(saved.JobId))).Content.ReadFromJsonAsync<PersonalComparisonResponse>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Path}/{run.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Path}/{run.Id}/cancel", new { })).StatusCode);
        run = await Result(client, run.Id, new(run.Work!.StepToken, run.Work.Url, false, ErrorCode: "retailer_access_restricted"));
        Assert.Equal("Paused", run.Status);
        Assert.Equal("NeedsVerification", run.Retailers[0].Status);
        while (run.Work is not null) run = await Result(client, run.Id,
            new(run.Work.StepToken, run.Work.Url, false, ErrorCode: "page_timeout"));
        Assert.Equal("Partial", run.Status);
        Assert.All(run.Retailers, r => Assert.Equal("UnableToCheck", r.Status));
        Assert.Single(await f.Db.ShoppingListProducts.ToArrayAsync());
        Assert.Single(await f.Db.ShopProductPrices.ToArrayAsync());
        client.DefaultRequestHeaders.Remove("X-MyShoppingList-Extension");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Path}/{run.Id}")).StatusCode);
    }
    [PostgreSqlFact]
    public async Task Aldi_alternatives_and_missing_prices_never_become_exact_mappings_or_change_the_source_item()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        var saved = (await (await client.PostAsJsonAsync("/api/user-extension/captures", f.Request())).Content.ReadFromJsonAsync<PersonalProductCaptureResponse>())!;
        await f.Db.ShoppingListProducts.Where(i => i.Id == saved.ShoppingListProductId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Quantity, 7).SetProperty(i => i.Notes, "Keep my notes"));
        var run = (await (await client.PostAsJsonAsync(Path, new PersonalComparisonStart(saved.JobId))).Content.ReadFromJsonAsync<PersonalComparisonResponse>())!;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Path}/{run.Id}/result",
            new PersonalComparisonResult(run.Work!.StepToken, run.Work.Url, true, Links: ["https://unrelated.example/product/1"]))).StatusCode);
        while (run.Work!.Retailer == "woolworths")
            run = await Result(client, run.Id, new(run.Work.StepToken, run.Work.Url, true, Links: [], EmptyConfirmed: true));
        const string aldi = "https://www.aldi.com.au/product/budget-original-coffee-500g-000000000123456";
        run = await Result(client, run.Id, new(run.Work.StepToken, run.Work.Url, true, Links: [aldi]));
        var name = "Budget Original Coffee 500g";
        var json = System.Text.Json.JsonSerializer.Serialize(new { type = "Product", name, brand = new { name = "Budget" },
            gtin = "9300000000019", offers = new { type = "Offer", url = aldi, priceCurrency = "AUD", price = (decimal?)null } }).Replace("\"type\"", "\"@type\"");
        var evidence = new ColesEvidence(null, [json], System.Text.Json.JsonSerializer.Serialize(new { canonicalUrl = aldi, name }));
        run = await Result(client, run.Id, new(run.Work!.StepToken, run.Work.Url, true, evidence, ObservedAt: f.Clock.Now.AddSeconds(-1)));
        while (run.Work is not null)
            run = await Result(client, run.Id, new(run.Work.StepToken, run.Work.Url, true, Links: [], EmptyConfirmed: true));
        var result = run.Retailers.Single(r => r.Code == "aldi");
        Assert.Equal("Alternatives", result.Status); Assert.Empty(result.ExactMatches);
        var candidate = Assert.Single(result.Alternatives); Assert.Null(candidate.Price);
        Assert.Equal("Different brand", candidate.Relationship);
        Assert.Single(await f.Db.ShopProducts.ToArrayAsync()); Assert.Single(await f.Db.ShopProductPrices.ToArrayAsync());
        var item = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync();
        Assert.Equal(7, item.Quantity); Assert.Equal("Keep my notes", item.Notes);
        var plan = await client.GetFromJsonAsync<ShoppingListPlan>($"/api/shopping-lists/{f.FirstList.Id}/plan",
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
        Assert.Single(plan!.Items.Single().Prices);
        Assert.Single(plan.Items.Single().PersonalComparison!.Retailers.Single(r => r.Code == "aldi").Alternatives);
        await f.Db.ShoppingListProducts.Where(i => i.Id == saved.ShoppingListProductId).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsHidden, true));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{Path}/{run.Id}/retry", new { })).StatusCode);
    }
    [PostgreSqlFact]
    public async Task Migration_upgrade_and_rollback_leave_existing_private_products_prices_and_list_items_unchanged()
    {
        await using var f = await CaptureApiScope.CreateAsync();
        using var client = await f.ClientAsync(f.First.Id);
        (await client.PostAsJsonAsync("/api/user-extension/captures", f.Request())).EnsureSuccessStatusCode();
        var product = await f.Db.Products.AsNoTracking().SingleAsync();
        var item = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync();
        var price = await f.Db.ShopProductPrices.AsNoTracking().SingleAsync();
        await f.Db.GetService<IMigrator>().MigrateAsync("20261101001539_AddAccountRecoveryAndEmailOutbox");
        await f.Db.Database.MigrateAsync();
        Assert.Empty(await f.Db.PersonalRetailerComparisons.ToArrayAsync());
        var afterProduct = await f.Db.Products.AsNoTracking().SingleAsync();
        var afterItem = await f.Db.ShoppingListProducts.AsNoTracking().SingleAsync();
        var afterPrice = await f.Db.ShopProductPrices.AsNoTracking().SingleAsync();
        Assert.Equal(product.Id, afterProduct.Id); Assert.Equal(product.Name, afterProduct.Name);
        Assert.Equal(item.Id, afterItem.Id); Assert.Equal(item.Quantity, afterItem.Quantity);
        Assert.Equal(item.UpdatedDate, afterItem.UpdatedDate);
        Assert.Equal(price.Id, afterPrice.Id); Assert.Equal(price.Price, afterPrice.Price);
        Assert.Equal(price.CheckedDate, afterPrice.CheckedDate);
        Assert.Single(await f.Db.ShopProductPriceHistory.ToArrayAsync());
    }
    private static void LogOutcome(ComparisonLogs logs, PersonalRetailerComparisonService.RetailerState retailer,
        IReadOnlyDictionary<string, string> previous) => PersonalRetailerComparisonService.LogOutcomes(logs.Logger,
            new myshoppinglist_api.Models.PersonalRetailerComparison { Id = 7, CaptureJobId = 8 },
            new myshoppinglist_api.Models.ProductImportJob { Id = 8, ProductId = 9, SourceShop = new() { Code = "coles" } },
            new() { ProductName = "private list notes", Retailers = [retailer] }, previous);

    private sealed class ComparisonLogs : IDisposable
    {
        public Events Sink { get; } = new();
        private readonly Logger root;
        private readonly ILoggerFactory factory;
        public ILogger<PersonalRetailerComparisonService> Logger { get; }
        public ComparisonLogs()
        {
            root = new LoggerConfiguration().MinimumLevel.Error()
                .MinimumLevel.Override(PersonalRetailerComparisonService.LogSource, LogEventLevel.Information)
                .Filter.ByIncludingOnly(Program.IncludeLogEvent).WriteTo.Sink(Sink).CreateLogger();
            factory = LoggerFactory.Create(builder => builder.AddSerilog(root));
            Logger = factory.CreateLogger<PersonalRetailerComparisonService>();
        }
        public void Dispose() { factory.Dispose(); root.Dispose(); }
    }
    private sealed class Events : ILogEventSink
    {
        public List<LogEvent> Entries { get; } = [];
        public Action<LogEvent>? OnEmit { get; set; }
        public void Emit(LogEvent entry) { Entries.Add(entry); OnEmit?.Invoke(entry); }
    }

    private static async Task<PersonalComparisonResponse> Result(HttpClient client, long id, PersonalComparisonResult result)
    {
        var response = await client.PostAsJsonAsync($"{Path}/{id}/result", result);
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<PersonalComparisonResponse>())!;
    }
}
