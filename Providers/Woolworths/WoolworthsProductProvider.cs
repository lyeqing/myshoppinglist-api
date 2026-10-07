using myshoppinglist_api.Services;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Providers.Woolworths;

public sealed class WoolworthsProductProvider(RetailerHttpClient http, WoolworthsProductParser parser,
    ProductUrlValidator validator, ILogger<WoolworthsProductProvider> logger,
    IRetailerSearchBrowser? searchBrowser = null, IOptions<RetailerSearchOptions>? searchOptions = null, IWoolworthsExtensionQueue? extension = null) : IShopProductProvider
{
    public string ShopCode => "woolworths";

    public async Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(Uri productUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = validator.Validate(productUrl.OriginalString);
        var code = WoolworthsProductParser.ProductCode(productUrl);
        if (!validation.IsValid || validation.Retailer!.Code != ShopCode || code is null)
            return new ProviderResult<ExtractedShopProduct>.Failure(new(ProviderFailureKind.InvalidUrl,
                "invalid_woolworths_url", "Use a supported Woolworths product page URL."));
        var response = await http.GetPageAsync(validation.ProductUrl!, ShopCode, cancellationToken);
        if (response is ProviderResult<RetailerPage>.Failure failure)
            return failure.Error.Kind == ProviderFailureKind.AccessRestricted && extension is not null
                ? await extension.ProductAsync(productUrl, cancellationToken)
                : new ProviderResult<ExtractedShopProduct>.Failure(failure.Error);
        var page = ((ProviderResult<RetailerPage>.Success)response).Value;
        if (WoolworthsProductParser.ProductCode(page.Url) != code)
            return new ProviderResult<ExtractedShopProduct>.Failure(new(ProviderFailureKind.InvalidProduct,
                "redirected_product", "Woolworths redirected to a different product."));
        var result = await parser.ParseAsync(page, cancellationToken);
        logger.LogInformation("Woolworths extraction for product {ShopProductCode}: {Outcome}", code,
            result is ProviderResult<ExtractedShopProduct>.Success ? "Identified" : "Failed");
        if (result is ProviderResult<ExtractedShopProduct>.Failure blocked && blocked.Error.Kind == ProviderFailureKind.AccessRestricted && extension is not null)
            return await extension.ProductAsync(productUrl, cancellationToken);
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
        var queries = ProductSearchQueryBuilder.BuildSearches(product);
        if (queries.Count == 0)
            return SearchFailure(new(ProviderFailureKind.InvalidProduct, "invalid_search_identity", "A product name is required."));
        var settings = searchOptions?.Value ?? new RetailerSearchOptions();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            foreach (var query in queries)
            {
                var rendered = await searchBrowser.ReadAsync(ShopCode, query, deadline.Token);
                if (rendered is ProviderResult<RetailerPage>.Failure failed)
                {
                    var fallback = failed.Error.Kind == ProviderFailureKind.AccessRestricted && extension is not null
                        ? await extension.SearchAsync(query, cancellationToken) : SearchFailure(failed.Error);
                    if (ShouldStop(fallback, product) || query == queries[^1]) return fallback;
                    continue;
                }
                var links = await new WoolworthsSearchParser().ParseAsync(((ProviderResult<RetailerPage>.Success)rendered).Value,
                    query, settings.MaximumCandidates, deadline.Token);
                if (links is ProviderResult<IReadOnlyList<Uri>>.Failure invalid)
                {
                    var fallback = invalid.Error.Kind == ProviderFailureKind.AccessRestricted && extension is not null
                        ? await extension.SearchAsync(query, cancellationToken) : SearchFailure(invalid.Error);
                    if (ShouldStop(fallback, product) || query == queries[^1]) return fallback;
                    continue;
                }
                var candidates = new List<ShopProductSearchResult>();
                // Keep search ranking and read sequentially because browser fallbacks share a scoped database context.
                foreach (var batch in ((ProviderResult<IReadOnlyList<Uri>>.Success)links).Value.Chunk(2))
                {
                    var results = new List<ProviderResult<ExtractedShopProduct>>();
                    foreach (var url in batch) results.Add(await GetProductFromUrlAsync(url, deadline.Token));
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
                var found = new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success(candidates);
                if (ShouldStop(found, product) || query == queries[^1]) return found;
            }
            return new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success([]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return SearchFailure(new(ProviderFailureKind.Timeout, "search_timeout", "Retailer search timed out.")); }
    }

    private static ProviderResult<IReadOnlyList<ShopProductSearchResult>> SearchFailure(ProviderFailure failure) =>
        new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure(failure);
    private static bool ShouldStop(ProviderResult<IReadOnlyList<ShopProductSearchResult>> result, ProductIdentity product) =>
        result is not ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success success
        || success.Value.Any(candidate => new ProductMatchingService(new()).Match(product, candidate.Product.Identity).Type == myshoppinglist_api.Models.MatchType.Exact);
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
            "price_unavailable", "A current Woolworths price could not be verified."));
    }
}
