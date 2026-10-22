using myshoppinglist_api.Providers.Models;
namespace myshoppinglist_api.Providers;
public interface IBrandAlternativeSearchProvider
{
    Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAlternativeBrandsAsync(string query, CancellationToken token);
}
