using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Providers.Coles;

public sealed class ColesProductProvider(RetailerHttpClient http, ColesProductParser parser,
    ProductUrlValidator validator, ILogger<ColesProductProvider> logger,
    IRetailerSearchBrowser? searchBrowser = null, IOptions<RetailerSearchOptions>? searchOptions = null) : IShopProductProvider
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

    public async Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(
        ProductIdentity product, ShopLocationContext? location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (location is not null)
            return SearchFailure(new(ProviderFailureKind.NotSupported, "unsupported_search_context", "Store-specific search is not supported."));
        if (searchBrowser is null)
            return SearchFailure(new(ProviderFailureKind.NotSupported, "search_not_configured", "Retailer search is not configured."));
        var query = ProductSearchQueryBuilder.Build(product);
        if (query is null)
            return SearchFailure(new(ProviderFailureKind.InvalidProduct, "invalid_search_identity", "A product name is required."));
        var settings = searchOptions?.Value ?? new RetailerSearchOptions();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var rendered = await searchBrowser.ReadAsync(ShopCode, query, deadline.Token);
            if (rendered is ProviderResult<RetailerPage>.Failure failed) return SearchFailure(failed.Error);
            var links = await new ColesSearchParser().ParseAsync(((ProviderResult<RetailerPage>.Success)rendered).Value,
                query, settings.MaximumCandidates, deadline.Token);
            if (links is ProviderResult<IReadOnlyList<Uri>>.Failure invalid) return SearchFailure(invalid.Error);
            var candidates = new List<ShopProductSearchResult>();
            // Two product reads at a time; results retain the search ranking regardless of completion order.
            foreach (var batch in ((ProviderResult<IReadOnlyList<Uri>>.Success)links).Value.Chunk(2))
            {
                var results = await Task.WhenAll(batch.Select(url => GetProductFromUrlAsync(url, deadline.Token)));
                foreach (var result in results)
                {
                    if (result is ProviderResult<ExtractedShopProduct>.Success identified)
                        candidates.Add(new(identified.Value));
                    else if (result is ProviderResult<ExtractedShopProduct>.Failure failure)
                    {
                        // A vanished or unsupported marketplace candidate is not a match. Incomplete
                        // network/parsing checks must not become a misleading successful empty search.
                        if (failure.Error.Kind is ProviderFailureKind.NotFound or ProviderFailureKind.NotSupported) continue;
                        return SearchFailure(failure.Error);
                    }
                }
            }
            return new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success(candidates);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return SearchFailure(new(ProviderFailureKind.Timeout, "search_timeout", "Retailer search timed out.")); }
    }

    private static ProviderResult<IReadOnlyList<ShopProductSearchResult>> SearchFailure(ProviderFailure failure) =>
        new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure(failure);
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
