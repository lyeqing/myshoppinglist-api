using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class OperationalEvents(IServiceScopeFactory scopes, IOptions<OperationsOptions> options, TimeProvider clock, ILogger<OperationalEvents> logger) : IDisposable
{
    // Bound rejected-request persistence so ingress protection cannot amplify database writes.
    // These security counts are sampled lower bounds during a flood.
    private readonly FixedWindowRateLimiter rejectionEvents = new(new FixedWindowRateLimiterOptions
    { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
    public void Dispose() => rejectionEvents.Dispose();
    public static readonly string[] Components = ["website", "api", "mcp", "extension", "database", "payment"];
    public static readonly string[] DiagnosticCodes = ["connection_failed", "request_timeout", "page_unreadable", "retailer_blocked", "permission_denied", "unexpected_error"];
    public async Task RecordAsync(string component, string name, string outcome, long? account = null, string? trace = null,
        int duration = 0, int status = 0, string? client = null, CancellationToken ct = default, string? release = null)
    {
        if (!options.Value.Enabled) return;
        if (component == "api" && status is 401 or 403 or 429)
        {
            using var lease = rejectionEvents.AttemptAcquire();
            if (!lease.IsAcquired) return;
        }
        // Callers supply fixed names; never accept exception text, routes, URLs or page content.
        if (!Components.Contains(component) || !Regex.IsMatch(name, "^[a-z_]{1,64}$") || outcome is not ("success" or "failed" or "rejected" or "cancelled")) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            db.OperationalEvents.Add(new() { CreatedAt = clock.GetUtcNow().UtcDateTime, Component = component, Name = name, Outcome = outcome,
                Release = release is not null && Regex.IsMatch(release, "^[a-zA-Z0-9._-]{1,64}$") ? release : options.Value.Release,
                Fingerprint = component + ":" + name + ":" + outcome,
                UserAccountId = account, TraceId = trace is { Length: <= 64 } && Regex.IsMatch(trace, "^[a-zA-Z0-9:-]+$") ? trace : null,
                DurationMs = Math.Clamp(duration, 0, 120000), StatusCode = Math.Clamp(status, 0, 599), ClientId = client is { Length: <= 100 } ? client : null });
            await db.SaveChangesAsync(timeout.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A diagnostics failure must not change a successful customer operation.
            logger.LogWarning("Operational event persistence unavailable; event was not recorded");
        }
    }
}

public sealed class VisitorAnalyticsService(MyShoppingListDbContext db, IOptions<OperationsOptions> options, TimeProvider clock)
{
    public async Task RecordAsync(VisitorEvent request, long? account, CancellationToken ct)
    {
        if (!request.Consent) throw new UserImportException(400, "Analytics requires optional consent.");
        if (request.EventId == Guid.Empty || request.VisitorId == Guid.Empty || request.SessionId == Guid.Empty
            || request.Name is not ("page_view" or "cta_click" or "signup_start" or "signup_complete")
            || request.Page is not ("landing" or "extension" or "features" or "faq" or "how_it_works" or "pricing" or "support")
            || request.Attribution is { } attribution && !Regex.IsMatch(attribution, "^[a-zA-Z0-9._-]{1,80}$"))
            throw new UserImportException(400, "Send a supported anonymous analytics event without URLs or personal information.");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OperationalEvents" ("Id","CreatedAt","Component","Name","Outcome","Release","Fingerprint","VisitorId","SessionId","Attribution","UserAccountId","DurationMs","StatusCode")
            VALUES ({request.EventId},{clock.GetUtcNow().UtcDateTime},'website',{request.Name},'success',{options.Value.Release},{"website:" + request.Page + ":" + request.Name},
                {request.VisitorId},{request.SessionId},{request.Attribution},{account},0,200) ON CONFLICT ("Id") DO NOTHING
            """, ct);
    }
    public Task ForgetAsync(Guid visitor, CancellationToken ct) => db.OperationalEvents.Where(e => e.VisitorId == visitor).ExecuteDeleteAsync(ct);

    public async Task RetainAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = now.Date;
        var cutoff = now.AddDays(-options.Value.RawRetentionDays);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('operations-retention'))", ct);
        // Rebuild closed days, including any older unaggregated rows after downtime.
        var summaries = await db.OperationalEvents.Where(e => e.CreatedAt < today)
            .GroupBy(e => new { Day = e.CreatedAt.Date, e.Component, e.Name, e.Outcome })
            .Select(g => new { g.Key, Events = g.LongCount(), Visitors = g.Where(e => e.VisitorId != null).Select(e => e.VisitorId).Distinct().LongCount(),
                Sessions = g.Where(e => e.SessionId != null).Select(e => e.SessionId).Distinct().LongCount(),
                Accounts = g.Where(e => e.UserAccountId != null).Select(e => e.UserAccountId).Distinct().LongCount(), Duration = g.Sum(e => (long)e.DurationMs) }).ToArrayAsync(ct);
        foreach (var summary in summaries)
        {
            var day = DateOnly.FromDateTime(summary.Key.Day);
            // Once a day overlaps raw expiry, preserve its completed full-day
            // aggregate; rebuilding it from remaining partial rows would lose counts.
            if (summary.Key.Day < cutoff.Date.AddDays(1) && await db.DailyOperationalSummaries.AnyAsync(e => e.Day == day
                && e.Component == summary.Key.Component && e.Name == summary.Key.Name && e.Outcome == summary.Key.Outcome, ct)) continue;
            await db.DailyOperationalSummaries.Where(e => e.Day == day && e.Component == summary.Key.Component && e.Name == summary.Key.Name && e.Outcome == summary.Key.Outcome).ExecuteDeleteAsync(ct);
            db.DailyOperationalSummaries.Add(new() { Day = day, Component = summary.Key.Component, Name = summary.Key.Name, Outcome = summary.Key.Outcome,
                Events = summary.Events, VisitorEstimate = summary.Visitors, Sessions = summary.Sessions, Accounts = summary.Accounts, TotalDurationMs = summary.Duration });
        }
        await db.SaveChangesAsync(ct);
        await db.OperationalEvents.Where(e => e.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        var oldest = DateOnly.FromDateTime(today.AddMonths(-options.Value.AggregateRetentionMonths));
        await db.DailyOperationalSummaries.Where(e => e.Day < oldest).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }
}
