using System.Globalization;
using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class CurrentListEndpoints
{
    public static void MapCurrentListEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/account/current-list").RequireAuthorization();
        group.MapGet("", (HttpContext c, CurrentListService service, CancellationToken ct) => Handle(c, () => service.ReadAsync(Account(c), ct)));
        group.MapPut("", (HttpContext c, CurrentListUpdateRequest request, CurrentListService service, CancellationToken ct) =>
            Handle(c, () => service.SetAsync(Account(c), request, ct))).RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
    }
    private static long Account(HttpContext c) => long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
    private static async Task<IResult> Handle(HttpContext c, Func<Task<CurrentListResponse>> action)
    {
        c.Response.Headers.CacheControl = "no-store";
        try { return Results.Ok(await action()); }
        catch (UserImportException error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
    }
}
