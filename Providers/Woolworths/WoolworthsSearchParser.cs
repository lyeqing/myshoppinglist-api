using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers.Woolworths;

public sealed class WoolworthsSearchParser
{
    public Task<ProviderResult<IReadOnlyList<Uri>>> ParseAsync(RetailerPage page, string query, int maximum, CancellationToken token) =>
        RetailerSearchParsing.ParseAsync(page, "woolworths", query,
            "[data-testid='search-results-product-scrollable-content']", WoolworthsProductParser.ProductCode, maximum, token);
}
