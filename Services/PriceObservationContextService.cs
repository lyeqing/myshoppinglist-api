using System.Text.RegularExpressions;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public static class PriceObservationContextService
{
    private static readonly ProductUrlValidator Urls = new(new RetailerCatalog());

    // Legacy unknown coverage remains an estimate; branch observations cannot be
    // used as a general retailer price, even when their scope claims otherwise.
    public static bool SupportsGeneralComparison(ShopProductPrice price) => price.ShopLocationId is null
        && price.PriceScope is PriceScope.Unknown or PriceScope.Online or PriceScope.National;

    public static PriceObservationContext? From(ShopProductPrice? price, string shopCode)
    {
        if (price is null) return null;
        return new(Enum.IsDefined(price.SourceType) ? price.SourceType : SourceType.Other,
            Enum.IsDefined(price.PriceScope) ? price.PriceScope : PriceScope.Unknown,
            PublicSourceUrl(price.SourceUrl, shopCode));
    }

    private static string? PublicSourceUrl(string source, string shopCode)
    {
        var result = Urls.Validate(source);
        if (!result.IsValid || !string.Equals(result.Retailer!.Code, shopCode, StringComparison.OrdinalIgnoreCase)) return null;
        var uri = result.ProductUrl!;
        // Only established public product routes are shareable. Endpoint URLs,
        // account pages and unknown routes can contain private identifiers.
        var pattern = result.Retailer.Code switch
        {
            "coles" => @"^/product/[a-zA-Z0-9-]+/?$",
            "woolworths" => @"^/shop/productdetails/[0-9]+(?:/[a-zA-Z0-9-]+)?/?$",
            _ => null
        };
        if (pattern is null || !Regex.IsMatch(uri.AbsolutePath, pattern, RegexOptions.CultureInvariant)) return null;
        // A product route remains useful without tracking, tokens or fragments.
        return new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
    }
}
