using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class AdminProductAlternativeEndpoints
{
    public static void MapAdminProductAlternativeEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/product-alternatives").RequireAuthorization("Administrator");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UserImportException error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
        });
        group.MapGet("/products", async (HttpContext context, BrandAlternativeService service, CancellationToken ct) =>
        {
            var values = context.Request.Query["search"];
            if (values.Count != 1) return Results.Problem(statusCode: 400, title: "Send one product search value.");
            return Results.Ok(await service.SearchAsync(long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!), values[0], ct));
        });
        group.MapGet("/{productId:long}/{alternativeProductId:long}", async (long productId, long alternativeProductId,
            HttpContext context, BrandAlternativeService service, CancellationToken ct) =>
            Results.Ok(await service.ContextAsync(long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!),
                productId, alternativeProductId, ct)));
        group.MapPut("/{productId:long}/{alternativeProductId:long}", async (long productId, long alternativeProductId,
            BrandAlternativeReviewRequest request, HttpContext context, BrandAlternativeService service, CancellationToken ct) =>
            Results.Ok(await service.ReviewAsync(long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!),
                productId, alternativeProductId, request, ct)));
    }
}
