using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class ShopLocationEndpoints
{
    public static void MapShopLocationEndpoints(this IEndpointRouteBuilder routes)
    {
        var customer = routes.MapGroup("/api/shop-locations").RequireAuthorization();
        var admin = routes.MapGroup("/api/admin/shop-locations").RequireAuthorization("Administrator");
        foreach (var group in new[] { customer, admin })
            group.AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                try { return await next(context); }
                catch (UserImportException error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
            });
        static long Actor(HttpContext c) => long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        static bool SingleFilters(HttpContext c) => c.Request.Query["shopId"].Count <= 1 && c.Request.Query["search"].Count <= 1;
        customer.MapGet("", async (long? shopId, string? search, HttpContext c, ShopLocationService service, CancellationToken ct) =>
            SingleFilters(c) ? Results.Ok(await service.SearchAsync(Actor(c), shopId, search, ct))
                : Results.Problem(statusCode: 400, title: "Send each search filter once."));
        customer.MapGet("/{id:long}", async (long id, HttpContext c, ShopLocationService service, CancellationToken ct) =>
            Results.Ok(await service.FindAsync(Actor(c), id, ct)));
        admin.MapGet("", async (long? shopId, string? search, HttpContext c, ShopLocationService service, CancellationToken ct) =>
            SingleFilters(c) ? Results.Ok(await service.SearchAdminAsync(Actor(c), shopId, search, ct))
                : Results.Problem(statusCode: 400, title: "Send each search filter once."));
        admin.MapGet("/{id:long}", async (long id, HttpContext c, ShopLocationService service, CancellationToken ct) =>
            Results.Ok(await service.ReadAdminAsync(Actor(c), id, ct)));
        admin.MapPost("", async (ShopLocationCreateRequest request, HttpContext c, ShopLocationService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(Actor(c), request, ct);
            return Results.Created($"/api/admin/shop-locations/{created.Branch.Location.Id}", created);
        });
        admin.MapPut("/{id:long}", async (long id, ShopLocationUpdateRequest request, HttpContext c, ShopLocationService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(Actor(c), id, request, ct)));
    }
}
