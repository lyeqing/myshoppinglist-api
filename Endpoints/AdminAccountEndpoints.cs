using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Services;
namespace myshoppinglist_api.Endpoints;

public static class AdminAccountEndpoints
{
    public static void MapAdminAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/accounts").RequireAuthorization("Administrator");
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        });
        group.MapGet("", async (string? search, MyShoppingListDbContext db, CancellationToken ct) => {
            var query = db.UserAccounts.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search)) query = query.Where(a => a.Email != null && a.Email.Contains(search));
            return Results.Ok(await query.OrderBy(a => a.Id).Take(100).Select(a => new { a.Id, a.Email, a.DisplayName,
                a.IsPaid, a.IsActive, a.ContributionBlocked, a.ContributionEnabled, a.RestrictionReason, a.UpdatedDate }).ToListAsync(ct));
        });
        group.MapGet("/{id:long}/review", async (long id, MyShoppingListDbContext db, CancellationToken ct) => Results.Ok(new {
            observations = await db.ContributionObservations.AsNoTracking().Where(o => o.UserAccountId == id).OrderByDescending(o => o.ReceivedAt).Take(100).ToListAsync(ct),
            actions = await db.AccountAdminAudits.AsNoTracking().Where(o => o.AccountId == id).OrderByDescending(o => o.CreatedAt).Take(100).ToListAsync(ct)
        }));
        group.MapPut("/{id:long}", async (long id, AdminAccountUpdate change, HttpContext context, AdminAccountService service, CancellationToken ct) => {
            await service.UpdateAsync(long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!), id, change, ct);
            return Results.NoContent();
        });
    }
}
