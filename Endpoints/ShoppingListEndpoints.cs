using System.Globalization;
using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class ShoppingListEndpoints
{
    public static void MapShoppingListEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/shopping-lists/{listId:long}/refresh-prices", async (long listId,
            HttpContext context, PriceRefreshService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var count = await service.RequestAsync(AccountId(context), listId, token);
            return count is null ? Results.Problem(statusCode: 404, title: "The shopping list was not found.")
                : Results.Ok(new { queued = count.Value });
        }).RequireAuthorization().WithTags("Shopping lists");
        routes.MapGet("/api/shopping-lists/{listId:long}/plan", async (long listId, HttpContext context, ShoppingListService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var plan = await service.PlanAsync(AccountId(context), listId, token);
            return plan is null ? Results.Problem(statusCode: 404, title: "The shopping list was not found.") : Results.Ok(plan);
        }).RequireAuthorization().WithTags("Shopping lists");
        routes.MapDelete("/api/shopping-lists/{listId:long}/items/{itemId:long}", async (long listId, long itemId,
            [Microsoft.AspNetCore.Mvc.FromBody] ShoppingListItemDeleteRequest request, HttpContext context, ShoppingListService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = await service.DeleteAsync(AccountId(context), listId, itemId, request, token);
            return result.StatusCode == 204 ? Results.NoContent() : Results.Problem(statusCode: result.StatusCode, title: result.Message);
        }).RequireAuthorization().WithTags("Shopping lists");
        routes.MapPost("/api/shopping-lists/default", async (HttpContext context, ShoppingListService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var id = await service.EnsureDefaultAsync(AccountId(context), token, context.Request.Headers["X-Client-Timezone"].ToString());
            return id.HasValue ? Results.Ok(new { shoppingListId = id.Value })
                : Results.Problem(statusCode: 401, title: "Your session is unavailable.");
        }).RequireAuthorization().WithTags("Shopping lists");
        routes.MapGet("/api/shopping-lists", async (HttpContext context, InStoreShoppingService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ListsAsync(AccountId(context), token));
        }).RequireAuthorization().WithTags("Shopping lists");
        routes.MapGet("/api/shopping-lists/{listId:long}/in-store", async (long listId, HttpContext context, InStoreShoppingService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var detail = await service.DetailAsync(AccountId(context), listId, token);
            return detail is null ? Results.Problem(statusCode: 404, title: "The shopping list was not found.") : Results.Ok(detail);
        }).RequireAuthorization().WithTags("Shopping lists");
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
