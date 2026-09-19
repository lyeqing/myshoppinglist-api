using System.Globalization;
using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class ProductImportEndpoints
{
    public const string SubmissionRatePolicy = "product-import-submit";
    public static void MapProductImportEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/shopping-lists/{listId:long}/imports", ListAsync)
            .WithTags("Product imports").RequireAuthorization().Produces<ProductImportPage>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        routes.MapPost("/api/shopping-lists/{listId:long}/products/url", SubmitAsync)
            .WithTags("Product imports").RequireAuthorization().RequireRateLimiting(SubmissionRatePolicy)
            .Produces<ProductImportAcceptedResponse>(202).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        routes.MapGet("/api/product-import-jobs/{jobId:long}", ReadAsync)
            .WithTags("Product imports").RequireAuthorization().Produces<ProductImportStatusResponse>()
            .ProducesProblem(401).ProducesProblem(404);
    }
    private static long AccountId(HttpContext context) => long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
    private static async Task<IResult> ListAsync(long listId, long? beforeId, int? pageSize, HttpContext context,
        ProductImportStatusService service, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        var size = pageSize ?? 20;
        if (size is < 1 or > 50 || beforeId <= 0) return Results.Problem(statusCode: 400, title: "Invalid pagination parameters.");
        var page = await service.ListAsync(AccountId(context), listId, beforeId, size, token);
        return page is null ? Results.Problem(statusCode: 404, title: "The shopping list was not found.") : Results.Ok(page);
    }
    private static async Task<IResult> SubmitAsync(long listId, ProductImportRequest request, HttpContext context,
        ProductImportSubmissionService service, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await service.SubmitAsync(AccountId(context), listId, request, token);
        return result.Response is not null ? Results.Accepted(result.Response.StatusUrl, result.Response)
            : Results.Problem(statusCode: result.StatusCode, title: result.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.ErrorCode });
    }
    private static async Task<IResult> ReadAsync(long jobId, HttpContext context, ProductImportStatusService service, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        var response = await service.ReadAsync(AccountId(context), jobId, token);
        return response is null ? Results.Problem(statusCode: 404, title: "The import job was not found.") : Results.Ok(response);
    }
}
