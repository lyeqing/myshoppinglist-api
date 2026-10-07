using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public class AdminHealthTests
{
    [PostgreSqlFact]
    public async Task Snapshot_counts_boundaries_and_does_not_recover_expired_work()
    {
        await using var isolation = await IsolatedDatabaseScope.CreateAsync();
        await using var db = PersistenceScope.Context();
        var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        ColesExtensionTask Task(string status, DateTime? completed = null) => new()
        {
            Key = Guid.NewGuid().ToString(),
            Url = "https://www.coles.com.au/product/example-123",
            Status = status,
            CreatedAt = now.AddDays(-2),
            CompletedAt = completed
        };
        var waiting = Task("Waiting"); waiting.ErrorCode = "retailer_blocked"; waiting.NextAttemptAt = now.AddHours(1);
        var active = Task("Processing"); active.LeaseExpiresAt = now.AddMinutes(1); active.WorkerId = "active";
        var expired = Task("Processing"); expired.LeaseExpiresAt = now; expired.WorkerId = "expired";
        var failed = Task("Failed", now); failed.ErrorCode = "retailer_blocked";
        db.AddRange(waiting, active, expired, failed, Task("Completed", now.AddHours(-24)),
            Task("Completed", now.AddHours(-24).AddTicks(-10)), Task("Completed", now.AddHours(1)));
        db.RetailerWorkloadStates.Add(new()
        {
            Retailer = "coles",
            PausedUntil = now.AddMinutes(5),
            BlockedAt = [now.AddMinutes(-10), now.AddMinutes(-1), now.AddMinutes(1)]
        });
        await db.SaveChangesAsync();
        var service = new AdminHealthService(db, new PersistenceClock { Now = now }, Options.Create(new RetailerWorkloadOptions()));
        var result = await service.ReadAsync(default);
        var coles = result.Retailers[0];
        Assert.Equal(1, coles.Waiting); Assert.Equal(1, coles.Processing); Assert.Equal(1, coles.ExpiredLeases);
        Assert.Equal(1, coles.CompletedLast24Hours); Assert.Equal(1, coles.FailedLast24Hours);
        Assert.Equal(50m, coles.CompletionPercent); Assert.Equal(1, coles.RecentBlockedReports);
        Assert.Equal("Paused", coles.WorkloadStatus); Assert.Equal(now.AddDays(-2), coles.OldestOutstandingCreatedAt);
        Assert.Equal(2, Assert.Single(coles.CommonFailures).Count);
        Assert.Null(result.Retailers[1].CompletionPercent); Assert.Empty(result.Retailers[1].CommonFailures);
        db.ChangeTracker.Clear();
        Assert.Equal("Processing", (await db.ColesExtensionTasks.SingleAsync(t => t.Id == expired.Id)).Status);
        var state = await db.RetailerWorkloadStates.SingleAsync();
        state.PausedUntil = now; await db.SaveChangesAsync();
        Assert.Equal("AwaitingTrial", (await service.ReadAsync(default)).Retailers[0].WorkloadStatus);
        state.ProbeToken = Guid.NewGuid(); state.ProbeExpiresAt = now.AddMinutes(2); await db.SaveChangesAsync();
        Assert.Equal("TrialRunning", (await service.ReadAsync(default)).Retailers[0].WorkloadStatus);
    }

    [PostgreSqlFact]
    public async Task Endpoint_requires_administrator_and_disables_caching()
    {
        await using var isolation = await IsolatedDatabaseScope.CreateAsync();
        await using var db = PersistenceScope.Context();
        var admin = AdminAccountTests.TestAccount(); var user = AdminAccountTests.TestAccount();
        db.AddRange(admin, user); await db.SaveChangesAsync();
        var adminToken = new string('a', 64); var userToken = new string('b', 64);
        db.UserSessions.AddRange(new UserSession { UserAccountId = admin.Id, TokenHash = SessionToken.Hash(adminToken), CreatedDate = DateTime.UtcNow, ExpiresDate = DateTime.UtcNow.AddHours(1) },
            new UserSession { UserAccountId = user.Id, TokenHash = SessionToken.Hash(userToken), CreatedDate = DateTime.UtcNow, ExpiresDate = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        await using var app = new ContributionTestApp(admin.Id);
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/health")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/health")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await client.GetAsync("/api/admin/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("woolworths", body); Assert.DoesNotContain(admin.Email!, body); Assert.DoesNotContain(adminToken, body);
    }
}
