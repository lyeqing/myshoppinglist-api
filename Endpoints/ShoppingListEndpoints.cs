using System.Globalization;
using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class ShoppingListEndpoints
{
    public static void MapShoppingListEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/shopping-lists/{listId:long}/items", ReadAsync).RequireAuthorization()
            .WithTags("Shopping lists").Produces<ShoppingListItemPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404);
        routes.MapPut("/api/shopping-lists/{listId:long}/items/{itemId:long}", UpdateAsync).RequireAuthorization()
            .WithTags("Shopping lists").Produces<ShoppingListItemResponse>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
    }
    private static long AccountId(HttpContext context) => long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
    private static async Task<IResult> ReadAsync(long listId, long? beforeId, int? pageSize, bool? includeHidden,
        bool? includePurchased, HttpContext context, ShoppingListService service, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        var size = pageSize ?? 20;
        if (size is < 1 or > 50 || beforeId <= 0) return Results.Problem(statusCode: 400, title: "Invalid pagination parameters.");
        var result = await service.ReadAsync(AccountId(context), listId, beforeId, size, includeHidden ?? false, includePurchased ?? true, token);
        return result is null ? Results.Problem(statusCode: 404, title: "The shopping list was not found.") : Results.Ok(result);
    }
    private static async Task<IResult> UpdateAsync(long listId, long itemId, ShoppingListItemUpdateRequest request,
        HttpContext context, ShoppingListService service, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await service.UpdateAsync(AccountId(context), listId, itemId, request, token);
        return result.Item is null ? Results.Problem(statusCode: result.StatusCode, title: result.Message) : Results.Ok(result.Item);
    }
}
