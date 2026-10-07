using Microsoft.Playwright;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Providers.Models;
using Xunit.Abstractions;

namespace myshoppinglist_api.Tests;

// Public-page smoke checks, not authenticated extension or exact-match proofs.
// No API writes, browser profile reuse, purchases, or access-challenge bypassing.
public class RetailerLiveRegressionTests(ITestOutputHelper output)
{
    [LiveProductAuditFact]
    public async Task Thirty_products_across_two_retailers_expose_search_candidates()
    {
        string[] products =
        [
            "yopro mango yoghurt pouch 150g", "mccain superfries shoestring 900g",
            "palmolive milk honey body wash 1l", "red island olive oil 1l",
            "hans twiggy sticks mild 500g", "mount franklin blood orange 375ml",
            "cadbury creme brulee caramel 195g", "panadol rapid 16 pack",
            "pickers cheesy garlic bread dippers 230g", "four n twenty meat pies 700g",
            "john west tuna olive oil 95g", "cocobella coconut water 1l",
            "cadbury favourites ultimate share 700g", "activia strawberry yoghurt 500g",
            "banana boat sport clear spray 175g", "coles kitchen supreme pizza 445g",
            "birds eye golden crunch straight cut 900g", "cadbury mcobeauty watermelon 180g",
            "coca cola classic 1.25l", "coca cola classic 375ml 10 pack",
            "weet bix 575g", "nutella 400g", "vegemite 380g", "tim tam original 200g",
            "heinz baked beans 420g", "kelloggs corn flakes 380g", "bega peanut butter 375g",
            "arnotts shapes barbecue 175g", "smiths original chips 170g", "milo 200g"
        ];
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "chrome", Headless = true });
        await using var context = await browser.NewContextAsync();
        var problems = new List<string>();
        foreach (var shop in new[] { "coles", "woolworths" })
        {
            var consecutiveRestrictions = 0;
            foreach (var query in products)
            {
                if (consecutiveRestrictions >= 3)
                {
                    output.WriteLine($"DEFERRED {shop}: {query}; three consecutive access restrictions");
                    continue;
                }
                var page = await context.NewPageAsync();
                try
                {
                    var url = ProductSearchQueryBuilder.SearchUrl(shop, query);
                    var response = await page.GotoAsync(url.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                    if (response?.Status is 403 or 429)
                    {
                        consecutiveRestrictions++;
                        problems.Add($"BLOCKED {shop}: {query}; HTTP {response.Status}");
                        output.WriteLine(problems[^1]);
                        continue;
                    }
                    try
                    {
                        await page.WaitForFunctionAsync("() => /no results|no products|access denied|robot or human|verify you are human/i.test(document.body?.innerText || '') || !!document.querySelector('a[href*=\"/product/\"],a[href*=\"/shop/productdetails/\"],iframe[src*=\"_Incapsula_Resource\"]')", null, new() { Timeout = 10000 });
                    }
                    catch (TimeoutException) { }
                    var snapshot = new myshoppinglist_api.Providers.Http.RetailerPage(new(page.Url), await page.ContentAsync(), DateTimeOffset.UtcNow);
                    var result = shop == "coles"
                        ? await new ColesSearchParser().ParseAsync(snapshot, query, 5, default)
                        : await new WoolworthsSearchParser().ParseAsync(snapshot, query, 5, default);
                    if (result is ProviderResult<IReadOnlyList<Uri>>.Failure failed)
                    {
                        consecutiveRestrictions = failed.Error.Kind == ProviderFailureKind.AccessRestricted ? consecutiveRestrictions + 1 : 0;
                        problems.Add($"{failed.Error.Kind} {shop}: {query}; {failed.Error.Code}");
                        output.WriteLine(problems[^1]);
                    }
                    else
                    {
                        consecutiveRestrictions = 0;
                        var links = ((ProviderResult<IReadOnlyList<Uri>>.Success)result).Value;
                        output.WriteLine($"SEARCH {shop}: {query}; {links.Count} candidates (not verified matches)");
                        Assert.InRange(links.Count, 0, 5);
                    }
                }
                catch (Microsoft.Playwright.PlaywrightException ex)
                {
                    problems.Add($"BROWSER {shop}: {query}; {ex.Message.Split('\n')[0]}");
                    output.WriteLine(problems[^1]);
                }
                finally { await page.CloseAsync(); }
                await Task.Delay(1000);
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}

public sealed class LiveProductAuditFactAttribute : FactAttribute
{
    public LiveProductAuditFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYSHOPPINGLIST_LIVE_PRODUCT_AUDIT") != "1")
            Skip = "Opt-in: MYSHOPPINGLIST_LIVE_PRODUCT_AUDIT=1 reads public retailer pages sequentially.";
    }
}
