using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class OperationsEndpoints
{
    private static long Actor(HttpContext c) => long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static void MapOperationsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/session", (HttpContext c) => { c.Response.Headers.CacheControl = "no-store"; return Results.Ok(new { administrator = true }); }).RequireAuthorization("Administrator");
        app.MapGet("/health/live", () => Results.Ok(new { status = "up" })).RequireRateLimiting("OperationsInput");
        app.MapGet("/health/ready", async (MyShoppingListDbContext db, HttpContext c) => {
            c.Response.Headers.CacheControl = "no-store";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try { return await db.Database.CanConnectAsync(timeout.Token) && !(await db.Database.GetPendingMigrationsAsync(timeout.Token)).Any()
                ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503); }
            catch { return Results.StatusCode(503); }
        }).RequireRateLimiting("OperationsInput");
        if (!app.Services.GetRequiredService<IOptions<OperationsOptions>>().Value.Enabled) return;
        var feedback = app.MapGroup("/api/feedback").RequireAuthorization().RequireRateLimiting("OperationsInput");
        Protect(feedback);
        feedback.MapPost("", async (FeedbackSubmission request, HttpContext c, FeedbackService service, CancellationToken ct) => Results.Ok(await service.SubmitAsync(Actor(c), request, ct)))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(750000));
        feedback.MapGet("", async (HttpContext c, FeedbackService service, CancellationToken ct) => Results.Ok(await service.OwnAsync(Actor(c), ct)));
        feedback.MapGet("/{id:guid}/screenshot", async (Guid id, HttpContext c, FeedbackService service, CancellationToken ct) =>
            Image(c, await service.ScreenshotAsync(Actor(c), id, false, ct)));

        var analytics = app.MapGroup("/api/analytics").RequireRateLimiting("OperationsInput");
        Protect(analytics);
        analytics.MapPost("/events", async (VisitorEvent request, HttpContext c, VisitorAnalyticsService service, CancellationToken ct) => {
            var account = long.TryParse(c.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (long?)null;
            await service.RecordAsync(request, account, ct); return Results.NoContent();
        });
        analytics.MapPost("/forget", async (ForgetVisitor request, VisitorAnalyticsService service, CancellationToken ct) => {
            if (request.VisitorId == Guid.Empty) return Results.BadRequest(); await service.ForgetAsync(request.VisitorId, ct); return Results.NoContent();
        });
        var diagnostics = app.MapGroup("/api/diagnostics").RequireRateLimiting("OperationsInput");
        Protect(diagnostics);
        diagnostics.MapPost("", async (DiagnosticEvent request, HttpContext c, OperationalEvents events, CancellationToken ct) => {
            if (request.Component is not ("website" or "extension") || !OperationalEvents.DiagnosticCodes.Contains(request.Code)
                || !System.Text.RegularExpressions.Regex.IsMatch(request.Release ?? "", "^[a-zA-Z0-9._-]{1,64}$")) return Results.BadRequest();
            var actor = long.TryParse(c.User.FindFirstValue(ClaimTypes.NameIdentifier), out var account) ? account : (long?)null;
            await events.RecordAsync(request.Component, request.Code, "failed", actor, System.Diagnostics.Activity.Current?.TraceId.ToString(), ct: ct, release: request.Release);
            return Results.NoContent();
        });

        var admin = app.MapGroup("/api/admin/operations").RequireAuthorization("Administrator"); Protect(admin);
        admin.MapGet("", ReadAsync);
        admin.MapGet("/feedback", async (string? status, HttpContext c, FeedbackService service, CancellationToken ct) => Results.Ok(await service.InboxAsync(Actor(c), status, ct)));
        admin.MapPut("/feedback/{id:guid}", async (Guid id, FeedbackUpdate request, HttpContext c, FeedbackService service, CancellationToken ct) => Results.Ok(await service.UpdateAsync(Actor(c), id, request, ct)));
        admin.MapGet("/feedback/{id:guid}/screenshot", async (Guid id, HttpContext c, FeedbackService service, CancellationToken ct) => Image(c, await service.ScreenshotAsync(Actor(c), id, true, ct)));
        admin.MapPost("/simulate-incident", async (IncidentSimulation request, HttpContext c, IncidentService incidents, CancellationToken ct) => {
            if (!OperationalEvents.Components.Contains(request.Component) || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000) return Results.BadRequest();
            var result = await incidents.ObserveAsync("simulation:" + request.Component, request.Component, "P0", request.Healthy, ct, Actor(c), request.Reason);
            await incidents.DeliverSimulatedAsync(ct);
            return Results.Ok(new { result.Id, result.Status, simulation = true });
        });
        admin.MapGet("/account/{id:long}", async (long id, HttpContext c, MyShoppingListDbContext db, TimeProvider clock, CancellationToken ct) => {
            if (!await db.UserAccounts.AnyAsync(a => a.Id == id, ct)) return Results.NotFound();
            db.OperationsAdminAudits.Add(new() { AdministratorId = Actor(c), Action = "account_journey", Subject = id.ToString(), Reason = "Support journey review", CreatedAt = clock.GetUtcNow().UtcDateTime }); await db.SaveChangesAsync(ct);
            return Results.Ok(new {
                events = await db.OperationalEvents.AsNoTracking().Where(e => e.UserAccountId == id).OrderByDescending(e => e.CreatedAt).Take(100)
                    .Select(e => new { e.CreatedAt, e.Component, e.Name, e.Outcome, e.Fingerprint, e.TraceId, e.StatusCode, e.DurationMs, e.Release }).ToArrayAsync(ct),
                firstList = await db.ShoppingLists.Where(l => l.UserAccountId == id).MinAsync(l => (DateTime?)l.CreatedDate, ct),
                firstSavedItem = await db.UserProductActivities.Where(a => a.UserAccountId == id).MinAsync(a => (DateTime?)a.AddedDate, ct),
                firstExtensionLink = await db.ExtensionDevices.Where(d => d.UserAccountId == id).MinAsync(d => (DateTime?)d.LinkedAt, ct),
                firstRecordedRegistration = await db.OperationalEvents.Where(e => e.UserAccountId == id && e.Name == "registration" && e.Outcome == "success").MinAsync(e => (DateTime?)e.CreatedAt, ct),
                firstExtensionCapture = await db.ProductImportJobs.Where(j => j.UserAccountId == id && j.CaptureRequestId != null && j.Status == ProductImportJobStatus.Completed && j.ProductId != null).MinAsync(j => j.CompletedDate, ct)
            });
        });
    }
    private static IResult Image(HttpContext c, byte[]? bytes)
    {
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return bytes == null ? Results.NotFound() : Results.File(bytes, "image/png", "feedback-screenshot.png");
    }
    private static void Protect(RouteGroupBuilder group) => group.AddEndpointFilter(async (invocation, next) => {
        invocation.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(invocation); } catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
    });
    private static async Task<IResult> ReadAsync(HttpContext c, MyShoppingListDbContext db, IOptions<OperationsOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime; var since = now.AddDays(-1);
        db.OperationsAdminAudits.Add(new() { AdministratorId = Actor(c), Action = "operations_overview", Reason = "Operational dashboard review", CreatedAt = now }); await db.SaveChangesAsync(ct);
        var recent = db.OperationalEvents.AsNoTracking().Where(e => e.CreatedAt >= since && e.CreatedAt <= now);
        var lifecycle = await db.ExtensionTasks.AsNoTracking().Where(t => t.CreatedAt >= since && t.CreatedAt <= now)
            .OrderByDescending(t => t.CreatedAt).Take(1000).Select(t => new { t.CreatedAt, t.AcknowledgedAt, t.StartedAt, t.CompletedAt }).ToArrayAsync(ct);
        return Results.Ok(new {
            checkedAt = now, since, simulationOnly = options.Value.SimulateDelivery, recipientConfigured = options.Value.IncidentRecipients.Length > 0,
            retention = new { rawDays = options.Value.RawRetentionDays, aggregateMonths = options.Value.AggregateRetentionMonths },
            accounts = new { registered = await db.UserAccounts.CountAsync(a => !a.IsTrial, ct), trial = await db.UserAccounts.CountAsync(a => a.IsTrial && a.IsActive && a.ExpiresDate > now, ct),
                free = await db.UserAccounts.CountAsync(a => !a.IsTrial && !a.IsPaid, ct), paid = await db.UserAccounts.CountAsync(a => !a.IsTrial && a.IsPaid, ct),
                observedActive7Days = await db.OperationalEvents.Where(e => e.UserAccountId != null && e.CreatedAt >= now.AddDays(-7)).Select(e => e.UserAccountId).Distinct().CountAsync(ct),
                observedActive30Days = await db.OperationalEvents.Where(e => e.UserAccountId != null && e.CreatedAt >= now.AddDays(-30)).Select(e => e.UserAccountId).Distinct().CountAsync(ct),
                activated = await db.UserProductActivities.Where(a => !a.UserAccount.IsTrial).Select(a => a.UserAccountId).Distinct().CountAsync(ct) },
            visitors = new { estimate = await recent.Where(e => e.VisitorId != null).Select(e => e.VisitorId).Distinct().CountAsync(ct),
                sessions = await recent.Where(e => e.SessionId != null).Select(e => e.SessionId).Distinct().CountAsync(ct), pageViews = await recent.CountAsync(e => e.Name == "page_view", ct) },
            acquisition = await db.OperationalEvents.Where(e => e.VisitorId != null && e.CreatedAt >= now.AddDays(-30) && e.CreatedAt <= now)
                .GroupBy(e => e.Attribution).Select(g => new { source = g.Key, visitors = g.Select(e => e.VisitorId).Distinct().Count(),
                    pageViews = g.Count(e => e.Name == "page_view"), ctaClicks = g.Count(e => e.Name == "cta_click"),
                    signupStarts = g.Count(e => e.Name == "signup_start"), signupCompletions = g.Count(e => e.Name == "signup_complete") })
                .OrderByDescending(g => g.pageViews).Take(25).ToArrayAsync(ct),
            events = await recent.GroupBy(e => new { e.Component, e.Name, e.Outcome }).Select(g => new { g.Key.Component, g.Key.Name, g.Key.Outcome, count = g.Count(), averageMs = g.Average(e => e.DurationMs) }).ToArrayAsync(ct),
            failures = await recent.Where(e => e.Outcome != "success").GroupBy(e => new { e.Fingerprint, e.Component }).Select(g => new { g.Key.Fingerprint, g.Key.Component, count = g.Count(), lastAt = g.Max(e => e.CreatedAt) }).OrderByDescending(e => e.count).Take(50).ToArrayAsync(ct),
            clients = await recent.Where(e => e.Component == "mcp").GroupBy(e => new { e.ClientId, e.Name, e.Outcome }).Select(g => new { g.Key.ClientId, g.Key.Name, g.Key.Outcome, calls = g.Count(), averageMs = g.Average(e => e.DurationMs) }).ToArrayAsync(ct),
            extension = new { linkedBrowsers = await db.ExtensionDevices.CountAsync(d => d.RevokedAt == null && d.UserSession.RevokedDate == null && d.UserSession.ExpiresDate > now, ct),
                pending = await db.ExtensionTasks.CountAsync(t => t.ExpiresAt > now && t.AiConnection.RevokedDate == null
                    && db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>().Any(a => a.Id == t.AiConnection.AuthorizationId && a.Status == "valid")
                    && (t.Type == "search" || db.ShoppingLists.Any(l => l.Id == t.ShoppingListId && l.UserAccountId == t.UserAccountId && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now)))
                    && (t.Status == "queued" || t.Status == "awaiting_user" || t.Status == "acknowledged" || t.Status == "running"), ct),
                expiredLast24Hours = await db.ExtensionTasks.CountAsync(t => t.ExpiresAt >= since && t.ExpiresAt <= now && (t.Status == "queued" || t.Status == "awaiting_user" || t.Status == "acknowledged" || t.Status == "running"), ct),
                latencySample = lifecycle.Length,
                averageApprovalWaitMs = lifecycle.Where(t => t.AcknowledgedAt != null).Select(t => (double?)(t.AcknowledgedAt!.Value - t.CreatedAt).TotalMilliseconds).DefaultIfEmpty().Average(),
                averageExecutionMs = lifecycle.Where(t => t.StartedAt != null && t.CompletedAt != null).Select(t => (double?)(t.CompletedAt!.Value - t.StartedAt!.Value).TotalMilliseconds).DefaultIfEmpty().Average(),
                outcomes = await db.ExtensionTasks.Where(t => t.CompletedAt >= since).GroupBy(t => new { t.Type, t.Status, t.Retailer, ExtensionVersion = t.Device == null ? null : t.Device.ExtensionVersion })
                    .Select(g => new { g.Key.Type, g.Key.Status, g.Key.Retailer, g.Key.ExtensionVersion, count = g.Count() }).ToArrayAsync(ct) },
            incidents = await db.OperationalIncidents.AsNoTracking().OrderByDescending(i => i.LastObservedAt).Take(50).ToArrayAsync(ct),
            deliveries = await db.AlertDeliveries.AsNoTracking().OrderByDescending(d => d.CreatedAt).Take(50).Select(d => new { d.Id, d.IncidentId, d.Kind, d.Status, d.Attempts, d.RecipientCount, d.CreatedAt, d.DeliveredAt }).ToArrayAsync(ct),
            daily = await db.DailyOperationalSummaries.AsNoTracking().OrderByDescending(d => d.Day).Take(500).ToArrayAsync(ct)
        });
    }
    private sealed record ForgetVisitor(Guid VisitorId);
}
