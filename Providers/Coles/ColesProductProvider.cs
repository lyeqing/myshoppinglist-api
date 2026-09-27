using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Providers.Coles;

// Coles is exclusively served by the catalogue/extension queue. No HTTP/browser fallback.
public sealed class ColesProductProvider(IColesExtensionQueue queue) : IShopProductProvider
{
    public string ShopCode => "coles";
    public Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(Uri url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ColesProductParser.ProductCode(url) is null)
            return Task.FromResult<ProviderResult<ExtractedShopProduct>>(new ProviderResult<ExtractedShopProduct>.Failure(
                new(ProviderFailureKind.InvalidUrl, "invalid_coles_url", "Use a supported Coles product URL.")));
        return queue.ProductAsync(url, token);
    }
    public async Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(
        ProductIdentity product, ShopLocationContext? location, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var queries = ProductSearchQueryBuilder.BuildColesSearches(product);
        if (location is not null || queries.Count == 0)
            return new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure(
                new(ProviderFailureKind.NotSupported, "unsupported_search_context", "A product name and anonymous search context are required."));
        foreach (var query in queries)
        {
            var result = await queue.SearchAsync(query, token);
            if (result is not ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success success || success.Value.Count > 0)
                return result;
        }
        return new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success([]);
    }
    public async Task<ProviderResult<ShopProductOffer>> GetOfferAsync(
        ShopProductSearchResult product, ShopLocationContext? location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (location is not null || product.Product.ShopCode != ShopCode)
            return new ProviderResult<ShopProductOffer>.Failure(new(ProviderFailureKind.NotSupported,
                "unsupported_offer_context", "This provider cannot verify an offer for the requested retailer or location."));
        var extracted = await GetProductFromUrlAsync(product.Product.ProductUrl, cancellationToken);
        if (extracted is ProviderResult<ExtractedShopProduct>.Failure failure)
            return new ProviderResult<ShopProductOffer>.Failure(failure.Error);
        var current = ((ProviderResult<ExtractedShopProduct>.Success)extracted).Value;
        if (product.Product.Identity.GTIN is { } gtin && current.Identity.GTIN is { } actualGtin && gtin != actualGtin
            || product.Product.ShopProductCode is { } code && current.ShopProductCode != code)
            return new ProviderResult<ShopProductOffer>.Failure(new(ProviderFailureKind.InvalidProduct,
                "offer_identity_changed", "The retailer product identity has changed."));
        return current.Offer ?? new ProviderResult<ShopProductOffer>.Failure(new(ProviderFailureKind.ParseError,
            "price_unavailable", "A current Coles price could not be verified."));
    }
}
