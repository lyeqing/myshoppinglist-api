using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;

namespace myshoppinglist_api.Services;

public sealed class AdminHealthService(MyShoppingListDbContext db, TimeProvider clock, IOptions<RetailerWorkloadOptions> options)
{
    public async Task<AdminHealthResponse> ReadAsync(CancellationToken token)
    {
        // A consistent, read-only view; checking health must never claim or recover work.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now.AddHours(-24);
        var states = await db.RetailerWorkloadStates.AsNoTracking().ToDictionaryAsync(s => s.Retailer, token);
        var rows = new List<AdminRetailerHealth>();
        foreach (var (code, name) in new[] { ("coles", "Coles"), ("woolworths", "Woolworths") })
        {
            var host = "https://www." + code + ".com.au/";
            var bareHost = "https://" + code + ".com.au/";
            var tasks = db.ColesExtensionTasks.AsNoTracking().Where(t => t.Url.StartsWith(host) || t.Url.StartsWith(bareHost));
            var counts = await tasks.GroupBy(t => 1).Select(g => new
            {
                Waiting = g.Count(t => t.Status == "Waiting"),
                Processing = g.Count(t => t.Status == "Processing" && t.LeaseExpiresAt > now),
                Expired = g.Count(t => t.Status == "Processing" && (t.LeaseExpiresAt == null || t.LeaseExpiresAt <= now)),
                Failed = g.Count(t => t.Status == "Failed"),
                Oldest = g.Min(t => t.Status == "Waiting" || t.Status == "Processing" ? (DateTime?)t.CreatedAt : null),
                CompletedRecent = g.Count(t => t.Status == "Completed" && t.CompletedAt >= since && t.CompletedAt <= now),
                FailedRecent = g.Count(t => t.Status == "Failed" && t.CompletedAt >= since && t.CompletedAt <= now)
            }).SingleOrDefaultAsync(token);
            var failures = await tasks.Where(t => (t.Status == "Waiting" || t.Status == "Failed") && t.ErrorCode != null)
                .GroupBy(t => t.ErrorCode).Select(g => new { Code = g.Key!, Count = g.Count() })
                .OrderByDescending(f => f.Count).ThenBy(f => f.Code).Take(5)
                .Select(f => new AdminHealthFailure(f.Code, f.Count)).ToArrayAsync(token);
            var state = states.GetValueOrDefault(code);
            var status = state?.PausedUntil is null ? "Running" : state.PausedUntil > now ? "Paused"
                : state.ProbeToken is not null && state.ProbeExpiresAt > now ? "TrialRunning" : "AwaitingTrial";
            var completed = counts?.CompletedRecent ?? 0;
            var failed = counts?.FailedRecent ?? 0;
            rows.Add(new(code, name, counts?.Waiting ?? 0, counts?.Processing ?? 0, counts?.Expired ?? 0,
                counts?.Failed ?? 0, counts?.Oldest, completed, failed,
                completed + failed == 0 ? null : decimal.Round(100m * completed / (completed + failed), 1),
                state?.BlockedAt.Count(t => t > now.AddMinutes(-options.Value.BlockedWindowMinutes) && t <= now) ?? 0,
                status, state?.PausedUntil, status == "TrialRunning" ? state?.ProbeExpiresAt : null, failures));
        }
        await transaction.CommitAsync(token);
        return new(now, since, options.Value.BlockedWindowMinutes, options.Value.MaxConcurrentTasks, rows.ToArray());
    }
}
