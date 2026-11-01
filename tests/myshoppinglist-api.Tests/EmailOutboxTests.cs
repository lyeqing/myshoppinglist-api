using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using myshoppinglist_api.Data;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class EmailOutboxTests
{
    [PostgreSqlFact]
    public async Task Critical_alerts_are_deduplicated_retried_and_only_marked_sent_after_transport_acceptance()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var app = new AccountSecurityTests.SecurityFactory(operations: true);
        using var scope = app.Services.CreateScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<EmailOutboxService>();
        var incident = await incidents.ObserveAsync("test:database", "database", "P0", false, default);
        await incidents.DispatchAsync(default); await incidents.DispatchAsync(default);
        Assert.Equal(1, await db.EmailOutboxMessages.CountAsync());
        Assert.Equal("pending", (await db.AlertDeliveries.AsNoTracking().SingleAsync()).Status);
        app.Transport.Failures = 1;
        Assert.True(await outbox.DeliverOneAsync(default)); Assert.Empty(app.Transport.Messages);
        var retry = await db.EmailOutboxMessages.AsNoTracking().SingleAsync(); Assert.Equal("queued", retry.Status); Assert.Equal(1, retry.Attempts);
        await incidents.ObserveAsync("test:database", "database", "P0", true, default); await incidents.DispatchAsync(default);
        Assert.Equal("queued", (await db.AlertDeliveries.AsNoTracking().SingleAsync(d => d.Kind == "recovery")).Status);
        Assert.False(await outbox.DeliverOneAsync(default));
        app.Clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(await outbox.DeliverOneAsync(default));
        var message = Assert.Single(app.Transport.Messages);
        Assert.Contains(incident.Id.ToString(), message.Email.Body); Assert.DoesNotContain("@", message.Email.Body);
        var accepted = await db.AlertDeliveries.AsNoTracking().SingleAsync(d => d.Kind == "open"); Assert.Equal("sent", accepted.Status); Assert.Equal(2, accepted.Attempts); Assert.NotNull(accepted.DeliveredAt);
        Assert.Null((await db.EmailOutboxMessages.AsNoTracking().SingleAsync()).ProtectedPayload);
        Assert.False(await outbox.DeliverOneAsync(default));
        await incidents.DispatchAsync(default); Assert.True(await outbox.DeliverOneAsync(default));
        Assert.Equal(2, app.Transport.Messages.Count);
        Assert.Equal("sent", (await db.AlertDeliveries.AsNoTracking().SingleAsync(d => d.Kind == "recovery")).Status);
    }

    [PostgreSqlFact]
    public async Task Simulations_never_enter_live_outbox_and_exhausted_delivery_retains_a_truthful_failure()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync();
        await using var app = new AccountSecurityTests.SecurityFactory(operations: true);
        using var scope = app.Services.CreateScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<EmailOutboxService>();
        await incidents.ObserveAsync("simulation:api", "api", "P0", false, default); await incidents.DispatchAsync(default);
        Assert.Empty(await db.EmailOutboxMessages.ToArrayAsync());
        Assert.Equal("simulated", (await db.AlertDeliveries.SingleAsync()).Status);
        await incidents.ObserveAsync("simulation:api", "api", "P0", true, default); await incidents.DispatchAsync(default);
        Assert.All(await db.AlertDeliveries.ToArrayAsync(), d => Assert.Equal("simulated", d.Status));
        Assert.Empty(await db.EmailOutboxMessages.ToArrayAsync());
        await incidents.ObserveAsync("test:api", "api", "P1", false, default); await incidents.DispatchAsync(default);
        app.Transport.Failures = 5;
        for (var i = 0; i < 3; i++) { Assert.True(await outbox.DeliverOneAsync(default)); app.Clock.Now += TimeSpan.FromMinutes(5); }
        var failed = await db.EmailOutboxMessages.AsNoTracking().SingleAsync(); Assert.Equal("failed", failed.Status); Assert.Equal(3, failed.Attempts); Assert.Null(failed.ProtectedPayload);
        Assert.Equal("failed", (await db.AlertDeliveries.AsNoTracking().SingleAsync(d => d.Id == failed.Id)).Status);
        Assert.Empty(app.Transport.Messages);
        await incidents.ObserveAsync("test:api", "api", "P1", true, default); await incidents.DispatchAsync(default);
        Assert.Equal("suppressed", (await db.AlertDeliveries.AsNoTracking().SingleAsync(d => d.Incident.Fingerprint == "test:api" && d.Kind == "recovery")).Status);
        await incidents.ObserveAsync("test:api", "api", "P1", false, default);
        Assert.Equal(2, await db.AlertDeliveries.CountAsync(d => d.Incident.Fingerprint == "test:api" && d.Kind == "open"));
        Assert.Equal("queued", (await db.AlertDeliveries.AsNoTracking().SingleAsync(d => d.Incident.Fingerprint == "test:api" && d.Kind == "open" && d.IncidentVersion == 2)).Status);
        app.Clock.Now += TimeSpan.FromDays(8); Assert.Equal(1, await outbox.RetainAsync(default));
    }

    [PostgreSqlFact]
    public async Task Expired_jobs_are_not_sent_and_abandoned_claims_can_be_recovered()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new AccountSecurityTests.SecurityFactory();
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<EmailOutboxService>();
        var row = outbox.Add("password_changed", new(["user@example.test"], "Fixture", "Fixture body"), app.Clock.Now.UtcDateTime.AddMinutes(1));
        await db.SaveChangesAsync(); app.Clock.Now += TimeSpan.FromMinutes(2);
        Assert.True(await outbox.DeliverOneAsync(default)); Assert.Empty(app.Transport.Messages);
        Assert.Equal("expired", (await db.EmailOutboxMessages.AsNoTracking().SingleAsync()).Status);
        var recoverable = outbox.Add("password_changed", new(["user@example.test"], "Recovered fixture", "Fixture body"), app.Clock.Now.UtcDateTime.AddDays(1));
        recoverable.Status = "sending"; recoverable.ClaimToken = Guid.NewGuid(); recoverable.LeaseExpiresAt = app.Clock.Now.UtcDateTime.AddSeconds(-1);
        await db.SaveChangesAsync(); Assert.True(await outbox.DeliverOneAsync(default));
        Assert.Equal("Recovered fixture", Assert.Single(app.Transport.Messages).Email.Subject);
    }
}
