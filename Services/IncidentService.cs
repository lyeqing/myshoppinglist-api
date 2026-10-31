using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class IncidentService(MyShoppingListDbContext db, IOptions<OperationsOptions> options, TimeProvider clock, IOptions<AdminOptions> admins)
{
    public async Task<OperationalIncident> ObserveAsync(string fingerprint, string component, string severity, bool healthy, CancellationToken ct,
        long? administrator = null, string? reason = null)
    {
        if (!OperationalEvents.Components.Contains(component) || severity is not ("P0" or "P1" or "P2")
            || !System.Text.RegularExpressions.Regex.IsMatch(fingerprint, "^[a-z_:]{1,100}$")) throw new ArgumentException("Use a fixed operational incident definition.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({"incident:" + fingerprint}))", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (administrator is { } actor)
        {
            if (!admins.Value.AccountIds.Contains(actor)) throw new UserImportException(403, "Administrator access required.");
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) throw new UserImportException(400, "Enter a simulation review reason.");
            db.OperationsAdminAudits.Add(new() { AdministratorId = actor, Action = "incident_simulation", Subject = fingerprint,
                Reason = reason.Trim(), CreatedAt = now });
        }
        var incident = await db.OperationalIncidents.SingleOrDefaultAsync(i => i.Fingerprint == fingerprint, ct);
        var transition = false;
        if (incident is null)
        {
            incident = new() { Fingerprint = fingerprint, Component = component, Severity = severity, Status = healthy ? "resolved" : "open",
                OpenedAt = now, LastObservedAt = now, ResolvedAt = healthy ? now : null };
            db.OperationalIncidents.Add(incident); transition = !healthy;
        }
        else
        {
            incident.LastObservedAt = now;
            if (healthy && incident.Status == "open") { incident.Status = "resolved"; incident.ResolvedAt = now; transition = true; }
            else if (!healthy && incident.Status != "open") { incident.Status = "open"; incident.ResolvedAt = null; incident.OpenedAt = now; incident.Version++; transition = true; }
        }
        if (transition && severity != "P2")
        {
            var kind = healthy ? "recovery" : "open";
            var cooldown = !healthy && await db.AlertDeliveries.AnyAsync(d => d.Incident.Fingerprint == fingerprint && d.Kind == "open"
                && d.Status != "suppressed" && d.CreatedAt > now.AddMinutes(-30), ct);
            var opened = !healthy || await db.AlertDeliveries.AnyAsync(d => d.IncidentId == incident.Id && d.IncidentVersion == incident.Version
                && d.Kind == "open" && d.Status != "suppressed", ct);
            db.AlertDeliveries.Add(new() { Incident = incident, IncidentVersion = incident.Version, Kind = kind,
                Status = cooldown || !opened ? "suppressed" : "queued", CreatedAt = now });
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return incident;
    }

    public async Task DeliverSimulatedAsync(CancellationToken ct)
    {
        if (!options.Value.SimulateDelivery) throw new InvalidOperationException("No live email provider has been approved or configured.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('operations-alert-delivery'))", ct);
        var deliveries = await db.AlertDeliveries.Where(d => d.Status == "queued").OrderBy(d => d.CreatedAt).Take(20).ToArrayAsync(ct);
        foreach (var delivery in deliveries)
        {
            delivery.Attempts++; delivery.RecipientCount = options.Value.IncidentRecipients.Length;
            delivery.Status = delivery.RecipientCount == 0 ? "not_configured" : "simulated";
            delivery.DeliveredAt = clock.GetUtcNow().UtcDateTime;
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task EvaluateRatesAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var api = db.OperationalEvents.AsNoTracking().Where(e => e.Component == "api" && e.CreatedAt > now.AddMinutes(-5) && e.CreatedAt <= now
            && (e.StatusCode >= 200 && e.StatusCode < 400 || e.StatusCode >= 500));
        var total = await api.CountAsync(ct); var failed = await api.CountAsync(e => e.StatusCode >= 500, ct);
        // Insufficient samples are unknown, not recovery. Wait for a complete observation window.
        if (total >= 100 && await api.MinAsync(e => e.CreatedAt, ct) <= now.AddMinutes(-4.5))
            await ObserveAsync("api_error_rate", "api", "P1", failed * 100m / total <= 5, ct);
        var captures = db.ExtensionTasks.AsNoTracking().Where(t => t.Type == "capture" && t.CompletedAt > now.AddMinutes(-15) && t.CompletedAt <= now
            && (t.Status == "completed" || t.Status == "failed"));
        var count = await captures.CountAsync(ct);
        if (count >= 20 && await captures.Select(t => t.UserAccountId).Distinct().CountAsync(ct) >= 3
            && await captures.MinAsync(t => t.CompletedAt, ct) <= now.AddMinutes(-14.5))
        {
            var failures = await captures.CountAsync(t => t.Status == "failed", ct);
            await ObserveAsync("extension_capture_error_rate", "extension", "P1", failures * 100m / count <= 20, ct);
        }
    }
}
