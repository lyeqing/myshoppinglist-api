using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class AdminHealthEndpoints
{
    public static void MapAdminHealthEndpoints(this IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/admin/health", async (HttpContext context, AdminHealthService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ReadAsync(token));
        }).RequireAuthorization("Administrator");
}
