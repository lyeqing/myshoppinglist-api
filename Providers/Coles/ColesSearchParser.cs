using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers.Coles;

public sealed class ColesSearchParser
{
    public Task<ProviderResult<IReadOnlyList<Uri>>> ParseAsync(RetailerPage page, string query, int maximum, CancellationToken token) =>
        RetailerSearchParsing.ParseAsync(page, "coles", query, ".coles-targeting-search-content-container", ColesProductParser.ProductCode, maximum, token);
}

internal static class RetailerSearchParsing
{
    internal static async Task<ProviderResult<IReadOnlyList<Uri>>> ParseAsync(RetailerPage page, string shop, string query,
        string selector, Func<Uri, string?> productCode, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (maximum is < 1 or > 8 || page.Html.Length > 2_097_152 || !RetailerBrowserNetworkGuard.IsSearchPage(shop, page.Url))
            return Fail(ProviderFailureKind.InvalidUrl, "invalid_search_page");
        var args = QueryHelpers.ParseQuery(page.Url.Query);
        var actual = args.GetValueOrDefault(shop == "coles" ? "q" : "searchTerm").ToString();
        var original = args.GetValueOrDefault("originalSearchTerm").ToString();
        if (!query.Equals(actual, StringComparison.OrdinalIgnoreCase) && !query.Equals(original, StringComparison.OrdinalIgnoreCase))
            return Fail(ProviderFailureKind.ParseError, "search_query_changed");
        using var document = await new HtmlParser().ParseDocumentAsync(page.Html, token);
        foreach (var node in document.QuerySelectorAll("script,style,template")) node.Remove();
        var text = document.Body?.TextContent ?? string.Empty;
        if (new[] { "access denied", "request unsuccessful", "verify you are human", "robot or human", "just a moment" }
            .Any(s => (document.Title + " " + text).Contains(s, StringComparison.OrdinalIgnoreCase))
            || document.QuerySelector("iframe[src*='_Incapsula_Resource']") is not null)
            return Fail(ProviderFailureKind.AccessRestricted, "retailer_access_restricted");
        // Coles may place "best guesses" below a genuine zero-result message. Those are
        // recommendations, not search matches for the requested identity.
        if (shop == "coles" && text.Contains("No results for", StringComparison.OrdinalIgnoreCase))
            return new ProviderResult<IReadOnlyList<Uri>>.Success(Array.Empty<Uri>());
        var container = document.QuerySelector(selector);
        var urls = new List<(Uri Url, string Name)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (container is not null)
            foreach (var anchor in container.QuerySelectorAll("a[href]"))
            {
                token.ThrowIfCancellationRequested();
                if (!Uri.TryCreate(page.Url, anchor.GetAttribute("href"), out var uri) || uri.UserInfo.Length != 0) continue;
                var code = productCode(uri);
                if (code is null || !seen.Add(code)) continue;
                var clean = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty }.Uri;
                urls.Add((clean, anchor.TextContent));
            }
        if (urls.Count > 0)
        {
            // Ranking decides which pages to fetch; it never asserts a product match.
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ordered = urls.OrderByDescending(u => terms.Count(t => u.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Take(maximum).Select(u => u.Url).ToArray();
            return new ProviderResult<IReadOnlyList<Uri>>.Success(ordered);
        }
        if (new[] { "no results found", "no products found", "we couldn't find any", "we couldn’t find any" }
            .Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return new ProviderResult<IReadOnlyList<Uri>>.Success(Array.Empty<Uri>());
        return Fail(ProviderFailureKind.ParseError, "search_results_not_identified");
    }
    private static ProviderResult<IReadOnlyList<Uri>> Fail(ProviderFailureKind kind, string code) =>
        new ProviderResult<IReadOnlyList<Uri>>.Failure(new(kind, code, "The retailer did not expose verifiable search results."));
}
