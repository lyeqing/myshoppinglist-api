using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Providers;

public interface IShopProductProvider
{
    string ShopCode { get; }

    // Callers must validate URLs; providers must also enforce destination checks when fetching.
    Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(
        Uri productUrl, CancellationToken cancellationToken);

    Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(
        ProductIdentity product, ShopLocationContext? location, CancellationToken cancellationToken);

    Task<ProviderResult<ShopProductOffer>> GetOfferAsync(
        ShopProductSearchResult product, ShopLocationContext? location, CancellationToken cancellationToken);
}
