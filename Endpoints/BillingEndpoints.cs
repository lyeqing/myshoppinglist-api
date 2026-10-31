using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this WebApplication app)
    {
        var own = app.MapGroup("/api/billing").RequireAuthorization().RequireRateLimiting("OperationsInput");
        own.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (context.HttpContext.User.FindFirst(SessionTokenAuthenticationHandler.TransportClaim)?.Value != "cookie") return Results.Forbid();
            try { return await next(context); }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
            catch (Exception e) when (e is Stripe.StripeException or HttpRequestException or TaskCanceledException && !context.HttpContext.RequestAborted.IsCancellationRequested) {
                return Results.Problem(statusCode: 503, title: "The test billing provider is unavailable. Retry the same request.");
            }
        });
        own.MapGet("", async (HttpContext c, TestBillingService service, CancellationToken ct) => Results.Ok(await service.OwnAsync(Actor(c), ct)));
        own.MapPost("/checkout", async (BillingAction action, HttpContext c, TestBillingService service, CancellationToken ct) => Results.Ok(new { url = await service.CheckoutAsync(Actor(c), action, ct) }));
        own.MapPost("/portal", async (BillingAction action, HttpContext c, TestBillingService service, CancellationToken ct) => Results.Ok(new { url = await service.PortalAsync(Actor(c), action.ExpectedAccountId, ct) }));
        if (!app.Services.GetRequiredService<IOptions<BillingOptions>>().Value.Enabled) return;
        // Outside /api: provider callbacks have signatures, never browser session authentication.
        app.MapPost("/billing/stripe-webhook", async (HttpContext c, MyShoppingListDbContext db, IOptions<BillingOptions> o, TimeProvider clock, CancellationToken ct) => {
            try {
                var body = await ReadBoundedAsync(c.Request.Body, ct);
                await BillingSetup.ReceiveAsync(body, c.Request.Headers["Stripe-Signature"].ToString(), db, o.Value, clock, ct);
                return Results.NoContent();
            } catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        }).RequireRateLimiting("OperationsInput").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(131072));
        app.MapGet("/api/admin/billing", async (HttpContext c, MyShoppingListDbContext db, TimeProvider clock, CancellationToken ct) => {
            c.Response.Headers.CacheControl = "no-store";
            var customers = await db.BillingCustomers.AsNoTracking().OrderBy(x => x.UserAccountId).Take(1000).ToArrayAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var subscriptions = customers.SelectMany(x => TestBillingService.Snapshot(x).Subscriptions).ToArray();
            db.OperationsAdminAudits.Add(new() { AdministratorId = Actor(c), Action = "test_billing_review", Reason = "Test billing dashboard review", CreatedAt = now });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { testOnly = true, capped = customers.Length == 1000, checkedAt = now,
                estimatedTestMrrAudCents = subscriptions.Where(s => s.Status == "active" && s.CurrentPeriodEnd > now).Sum(s => s.MonthlyAudCents),
                scheduledCancellations = subscriptions.Count(s => s.CancelAtPeriodEnd), failedSubscriptions = subscriptions.Count(s => s.Status is "past_due" or "unpaid"),
                canceledSubscriptions = subscriptions.Count(s => s.Status == "canceled"),
                customers = customers.Select(x => new { accountId = x.UserAccountId, x.ReconciledAt, snapshot = TestBillingService.Snapshot(x) }),
                pending = await db.BillingWebhookEvents.CountAsync(x => x.Status == "pending", ct), unmatched = await db.BillingWebhookEvents.CountAsync(x => x.Status == "unmatched", ct) });
        }).RequireAuthorization("Administrator");
    }
    private static long Actor(HttpContext c) => long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static async Task<string> ReadBoundedAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
        while ((count = await input.ReadAsync(bytes, ct)) > 0) {
            if (buffer.Length + count > 131072) throw new UserImportException(413, "Webhook is too large.");
            buffer.Write(bytes, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
