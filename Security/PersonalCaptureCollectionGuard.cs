using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;

namespace myshoppinglist_api.Security;

// Leave legacy history reads available, but never dispatch or accept shared
// collection work once personal mode is enabled. Authorization runs first.
public sealed class PersonalCaptureCollectionGuard(RequestDelegate next, IOptions<UserExtensionOptions> options)
{
    private static readonly Regex ListCollectionRoute = new(
        @"^/api/shopping-lists/[0-9]+/(?:products/url|(?:items/[0-9]+/)?(?:refresh-prices|pack-discovery|brand-discovery)|items/[0-9]+/retry-comparison)/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Value.PersonalCapturesEnabled) { await next(context); return; }
        var path = context.Request.Path;
        var canonical = path.Value?.TrimEnd('/');
        if (string.Equals(canonical, "/api/contributions/preference", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.Headers.CacheControl = "no-store";
            await Results.Ok(new { enabled = false, blocked = false }).ExecuteAsync(context); return;
        }
        if (string.Equals(canonical, "/api/contributions/claim", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status204NoContent; return;
        }
        var oldCollection = path.StartsWithSegments("/api/user-extension/imports", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/coles-worker", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/contributions", StringComparison.OrdinalIgnoreCase)
            || ListCollectionRoute.IsMatch(path.Value ?? "") && !HttpMethods.IsOptions(context.Request.Method);
        if (oldCollection)
        {
            context.Response.Headers.CacheControl = "no-store";
            await Results.Problem(statusCode: StatusCodes.Status410Gone,
                title: "Automatic retailer collection has been retired. Use the updated MyShoppingList extension on the product page to save a personal observation.")
                .ExecuteAsync(context); return;
        }
        await next(context);
    }
}
