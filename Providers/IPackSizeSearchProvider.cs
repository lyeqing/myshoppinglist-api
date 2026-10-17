using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers;

// One size-free family query; discovery must not stop on an exact-size candidate.
public interface IPackSizeSearchProvider
{
    Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchPackSizesAsync(string familyQuery, CancellationToken token);
}
