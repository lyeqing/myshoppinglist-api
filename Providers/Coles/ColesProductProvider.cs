using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Providers.Coles;

public sealed class ColesProductProvider(RetailerHttpClient http, ColesProductParser parser,
    ProductUrlValidator validator, ILogger<ColesProductProvider> logger) : IShopProductProvider
{
    public string ShopCode => "coles";

    public async Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(Uri productUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = validator.Validate(productUrl.OriginalString);
        var code = ColesProductParser.ProductCode(productUrl);
        if (!validation.IsValid || validation.Retailer!.Code != ShopCode || code is null)
            return new ProviderResult<ExtractedShopProduct>.Failure(new(ProviderFailureKind.InvalidUrl,
                "invalid_coles_url", "Use a supported Coles product page URL."));
        var response = await http.GetPageAsync(validation.ProductUrl!, ShopCode, cancellationToken);
        if (response is ProviderResult<RetailerPage>.Failure failure)
            return new ProviderResult<ExtractedShopProduct>.Failure(failure.Error);
        var page = ((ProviderResult<RetailerPage>.Success)response).Value;
        if (ColesProductParser.ProductCode(page.Url) != code)
            return new ProviderResult<ExtractedShopProduct>.Failure(new(ProviderFailureKind.InvalidProduct,
                "redirected_product", "Coles redirected to a different product."));
        var result = await parser.ParseAsync(page, cancellationToken);
        logger.LogInformation("Coles extraction for product {ShopProductCode}: {Outcome}", code,
            result is ProviderResult<ExtractedShopProduct>.Success ? "Identified" : "Failed");
        return result;
    }

    public Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(
        ProductIdentity product, ShopLocationContext? location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ProviderResult<IReadOnlyList<ShopProductSearchResult>>>(
            new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure(new(ProviderFailureKind.NotSupported,
                "coles_search_not_implemented", "Coles cross-retailer search is not implemented yet.")));
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
