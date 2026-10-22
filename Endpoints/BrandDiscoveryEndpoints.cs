using System.Globalization;
using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class BrandDiscoveryEndpoints
{
    public static void MapBrandDiscoveryEndpoints(this IEndpointRouteBuilder routes)
    {
        foreach (var path in new[] { "/api/shopping-lists/{listId:long}/brand-discovery", "/api/shopping-lists/{listId:long}/items/{itemId:long}/brand-discovery" })
        {
            routes.MapGet(path, (HttpContext context, BrandDiscoveryService service, CancellationToken token) => Run(context, service, null, token)).RequireAuthorization();
            routes.MapPost(path, (BrandDiscoveryRequest request, HttpContext context, BrandDiscoveryService service, CancellationToken token) => Run(context, service, request, token))
                .RequireAuthorization().RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
        }
    }
    private static async Task<IResult> Run(HttpContext context, BrandDiscoveryService service, BrandDiscoveryRequest? request, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        var accountId = long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
        var listId = long.Parse((string)context.Request.RouteValues["listId"]!, CultureInfo.InvariantCulture);
        var itemId = context.Request.RouteValues["itemId"] is string value ? long.Parse(value, CultureInfo.InvariantCulture) : (long?)null;
        try { return Results.Ok(request is null ? await service.ReadAsync(accountId, listId, itemId, token) : await service.RequestAsync(accountId, listId, itemId, request, token)); }
        catch (UserImportException error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
    }
}
