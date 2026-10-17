using System.Globalization;
using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class ShoppingPreferencesEndpoints
{
    public static void MapShoppingPreferencesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/account/shopping-preferences").RequireAuthorization().WithTags("Shopping preferences");
        group.MapGet("", (HttpContext context, ShoppingPreferencesService service, CancellationToken ct) =>
            Handle(context, () => service.ReadAsync(AccountId(context), ct)));
        group.MapPut("", (ShoppingPreferencesUpdate request, HttpContext context, ShoppingPreferencesService service, CancellationToken ct) =>
            Handle(context, () => service.SaveAsync(AccountId(context), request, ct)))
            .RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
    }
    private static long AccountId(HttpContext context) => long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
    private static async Task<IResult> Handle(HttpContext context, Func<Task<ShoppingPreferencesResponse>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return Results.Ok(await action()); }
        catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
    }
}
