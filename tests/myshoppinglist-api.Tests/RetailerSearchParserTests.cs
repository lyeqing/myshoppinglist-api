using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Tests;

public class RetailerSearchParserTests
{
    internal static Task<ProviderResult<IReadOnlyList<Uri>>> Parse(string shop, string html, string query = "coca-cola", int max = 5, string? actualQuery = null)
    {
        var page = new RetailerPage(ProductSearchQueryBuilder.SearchUrl(shop, actualQuery ?? query), html, DateTimeOffset.UtcNow);
        return shop == "coles" ? new ColesSearchParser().ParseAsync(page, query, max, CancellationToken.None)
            : new WoolworthsSearchParser().ParseAsync(page, query, max, CancellationToken.None);
    }
    [Theory]
    [InlineData("coles")]
    [InlineData("woolworths")]
    public async Task Observed_links_are_deduplicated_ranked_and_bounded(string shop)
    {
        var html = shop == "coles" ? SearchFixtures.Coles : SearchFixtures.Woolworths;
        var result = Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Success>(await Parse(shop, html, "coca-cola 1.25l", 1));
        Assert.Single(result.Value); Assert.Contains(shop == "coles" ? "123011" : "32731", result.Value[0].AbsolutePath);
        Assert.Empty(result.Value[0].Fragment);
        Assert.Equal(2, Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Success>(await Parse(shop, html)).Value.Count);
    }
    [Theory]
    [InlineData("coles")]
    [InlineData("woolworths")]
    public async Task No_results_requires_visible_evidence_and_challenges_are_not_empty(string shop)
    {
        Assert.Empty(Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Success>(await Parse(shop, "<h1>No results found</h1>")).Value);
        foreach (var html in new[] { "<script>var message='No results found'</script>", "<p>Loading...</p>", "<h1>Products</h1>" })
            Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Failure>(await Parse(shop, html));
        var challenge = Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Failure>(await Parse(shop, "<iframe src='/_Incapsula_Resource'>Request unsuccessful</iframe>"));
        Assert.Equal(ProviderFailureKind.AccessRestricted, challenge.Error.Kind);
    }
    [Theory]
    [InlineData("coles")]
    [InlineData("woolworths")]
    public async Task Unrelated_recommendations_unsafe_links_and_query_changes_are_rejected(string shop)
    {
        var html = shop == "coles" ? SearchFixtures.Coles : SearchFixtures.Woolworths;
        Assert.Equal("search_query_changed", Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Failure>(await Parse(shop, html, actualQuery: "other")).Error.Code);
        var prefix = shop == "coles" ? "/product/item-" : "/shop/productdetails/";
        var container = shop == "coles" ? "class='coles-targeting-search-content-container'" : "data-testid='search-results-product-scrollable-content'";
        var bad = $"<a href='{prefix}999'>Outside result container</a><section {container}><a href='https://127.0.0.1{prefix}999'>Private</a><a href='https://evil.example{prefix}999'>Other host</a></section>";
        Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Failure>(await Parse(shop, bad));
    }

    [Fact]
    public async Task Coles_best_guesses_below_observed_no_results_message_are_not_search_matches()
    {
        var result = await Parse("coles", "<h1>No results for &quot;coca-cola&quot;</h1><p>Here are our best guesses</p>" + SearchFixtures.Coles);
        Assert.Empty(Assert.IsType<ProviderResult<IReadOnlyList<Uri>>.Success>(result).Value);
    }
}
