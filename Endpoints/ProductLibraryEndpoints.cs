using System.Globalization;
using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class ProductLibraryEndpoints
{
    public static void MapProductLibraryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/account/products").RequireAuthorization().WithTags("Product library");
        group.MapGet("", (HttpContext context, ProductLibraryService service, CancellationToken ct) => Handle(context, async () =>
        {
            var q = context.Request.Query;
            if (q.Any(p => p.Value.Count != 1) || q.Keys.Any(k => k is not ("view" or "filter" or "search" or "offset" or "pageSize")))
                throw new UserImportException(400, "Send each supported product filter once.");
            if (!int.TryParse(q["offset"].FirstOrDefault() ?? "0", out var offset) || !int.TryParse(q["pageSize"].FirstOrDefault() ?? "24", out var size))
                throw new UserImportException(400, "Invalid pagination parameters.");
            return await service.ReadAsync(AccountId(context), q["view"].FirstOrDefault() ?? "history", q["filter"].FirstOrDefault() ?? "all", q["search"], offset, size, ct);
        }));
        group.MapGet("/preferences", (HttpContext context, ProductLibraryService service, CancellationToken ct) => Handle(context, async () =>
        {
            var q = context.Request.Query;
            if (q.Count != 2 || q["productIds"].Count != 1 || q["expectedAccountId"].Count != 1) throw new UserImportException(400, "Send productIds and expectedAccountId once.");
            if (!long.TryParse(q["expectedAccountId"], out var expected) || expected != AccountId(context)) throw new UserImportException(409, "Your account changed. Reload your products.");
            var values = q["productIds"].ToString().Split(',');
            if (values.Length is < 1 or > 50 || values.Any(v => !long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0))
                throw new UserImportException(400, "Send between 1 and 50 valid product IDs.");
            return await service.PreferencesAsync(AccountId(context), values.Select(long.Parse).ToArray(), ct);
        }));
        group.MapPut("/{productId:long}/preferences", (long productId, ProductPreferenceUpdate request, HttpContext context, ProductLibraryService service, CancellationToken ct) =>
            Handle(context, () => service.SavePreferenceAsync(AccountId(context), productId, request, ct)))
            .RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
        routes.MapPost("/api/shopping-lists/{listId:long}/products/{productId:long}", (long listId, long productId, ProductLibraryAddRequest request,
            HttpContext context, ProductLibraryService service, CancellationToken ct) => Handle(context, () => service.AddAsync(AccountId(context), productId, listId, request, ct)))
            .RequireAuthorization().RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy).WithTags("Product library");
    }
    private static long AccountId(HttpContext context) => long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
    private static async Task<IResult> Handle<T>(HttpContext context, Func<Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return Results.Ok(await action()); }
        catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
    }
}
