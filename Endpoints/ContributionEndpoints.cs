using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Services;
namespace myshoppinglist_api.Endpoints;

public static class ContributionEndpoints
{
    public static void MapContributionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/contributions").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        });
        static long Account(HttpContext c) => long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        group.MapGet("/preference", async (HttpContext c, ContributionService service, CancellationToken ct) => {
            var a = await service.AccountAsync(Account(c), ct);
            return Results.Ok(new { enabled = a.ContributionEnabled, blocked = a.ContributionBlocked });
        });
        group.MapPut("/preference", async (ContributionPreference request, HttpContext c, MyShoppingListDbContext db, ContributionService service, CancellationToken ct) => {
            await service.AccountAsync(Account(c), ct);
            var id = Account(c);
            await db.UserAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ContributionEnabled, request.Enabled), ct);
            return Results.NoContent();
        });
        group.MapPost("/claim", async (HttpContext c, ContributionService service, CancellationToken ct) =>
            Results.Ok(await service.ClaimAsync(Account(c), ct))).RequireRateLimiting("Contribution");
        group.MapPost("/{id:long}/result", async (long id, ContributionResult result, HttpContext c, ContributionService service, CancellationToken ct) => {
            await service.SubmitAsync(Account(c), id, result, ct); return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(2500000)).RequireRateLimiting("Contribution");
    }
}
