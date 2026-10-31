using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class OperationsTests
{
    [PostgreSqlFact]
    public async Task Feedback_is_private_retry_safe_audited_and_has_versioned_customer_responses()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        using var a = app.Client(); using var b = app.Client(); using var admin = app.Client(); using var anonymous = app.Client();
        var owner = await Register(a); await Register(b); var staff = await Register(admin);
        app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.AccountIds = [staff.Account.Id];
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/operations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/admin/operations")).StatusCode);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        var body = new FeedbackSubmission(owner.Account.Id, Guid.NewGuid(), "bug", "Private checkout-free support message", 4, Screenshot: png, ScreenshotApproved: true);
        var submitted = await a.PostAsJsonAsync("/api/feedback", body); submitted.EnsureSuccessStatusCode();
        var ticket = (await submitted.Content.ReadFromJsonAsync<FeedbackView>())!;
        var retry = await a.PostAsJsonAsync("/api/feedback", body); retry.EnsureSuccessStatusCode();
        Assert.Equal(ticket.Id, (await retry.Content.ReadFromJsonAsync<FeedbackView>())!.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/feedback", body with { Message = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsJsonAsync("/api/feedback", body)).StatusCode);
        Assert.Empty((await b.GetFromJsonAsync<FeedbackView[]>("/api/feedback"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/feedback/{ticket.Id}/screenshot")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/admin/operations/feedback")).StatusCode);
        var inbox = (await admin.GetFromJsonAsync<FeedbackView[]>("/api/admin/operations/feedback?status=new"))!; Assert.Single(inbox);
        var image = await admin.GetAsync($"/api/admin/operations/feedback/{ticket.Id}/screenshot"); image.EnsureSuccessStatusCode();
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single()); Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());
        var change = new FeedbackUpdate(ticket.Version, "resolved", "Thank you. This is resolved.", "INTERNAL-PRIVATE-42", "Reviewed reproduction");
        (await admin.PutAsJsonAsync($"/api/admin/operations/feedback/{ticket.Id}", change)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/admin/operations/feedback/{ticket.Id}", change)).StatusCode);
        var own = Assert.Single((await a.GetFromJsonAsync<FeedbackView[]>("/api/feedback"))!);
        Assert.Equal("resolved", own.Status); Assert.Equal(change.Response, own.Response); Assert.Null(own.IssueReference);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.Single(await db.FeedbackTickets.ToArrayAsync());
        Assert.Contains(await db.OperationalEvents.ToArrayAsync(), e => e.Name == "registration" && e.Outcome == "success" && e.UserAccountId == owner.Account.Id);
        Assert.Contains(await db.OperationsAdminAudits.ToArrayAsync(), x => x.Action == "feedback_update" && x.AdministratorId == staff.Account.Id && x.Reason == change.Reason);
        var overview = await admin.GetStringAsync("/api/admin/operations"); Assert.DoesNotContain(body.Message, overview); Assert.DoesNotContain("alerts@example.test", overview);
    }

    [PostgreSqlFact]
    public async Task Screenshots_require_permission_and_visitor_events_require_consent_and_safe_fields()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        using var client = app.Client(); var owner = await Register(client);
        var body = new FeedbackSubmission(owner.Account.Id, Guid.NewGuid(), "other", "Test", Screenshot: [1, 2, 3]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/feedback", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/feedback", body with { ScreenshotApproved = true })).StatusCode);
        var visitor = new VisitorEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "page_view", "landing", "newsletter", false);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/analytics/events", visitor)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/analytics/events", visitor with { Consent = true, Attribution = "https://private.test/?email=private" })).StatusCode);
        (await client.PostAsJsonAsync("/api/analytics/events", visitor with { Consent = true })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/analytics/events", visitor with { Consent = true })).EnsureSuccessStatusCode();
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.Single(await db.OperationalEvents.Where(e => e.VisitorId == visitor.VisitorId).ToArrayAsync());
        app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.AccountIds = [owner.Account.Id];
        var marketing = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/admin/operations");
        var source = marketing.GetProperty("acquisition")[0]; Assert.Equal("newsletter", source.GetProperty("source").GetString());
        Assert.Equal(1, source.GetProperty("visitors").GetInt32()); Assert.Equal(1, source.GetProperty("pageViews").GetInt32());
        (await client.PostAsJsonAsync("/api/analytics/forget", new { visitor.VisitorId })).EnsureSuccessStatusCode();
        Assert.Empty(await db.OperationalEvents.Where(e => e.VisitorId == visitor.VisitorId).ToArrayAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/diagnostics", new DiagnosticEvent("extension", "raw sensitive exception text", "test"))).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Incident_deduplication_recovery_cooldown_and_simulated_outbox_are_durable()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        // Concurrent observations use separate contexts and must produce one open delivery.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IncidentService>().ObserveAsync("test:database", "database", "P0", false, default);
        }));
        await using var read = app.Services.CreateAsyncScope(); var db = read.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var service = read.ServiceProvider.GetRequiredService<IncidentService>();
        Assert.Single(await db.AlertDeliveries.ToArrayAsync()); await service.DeliverSimulatedAsync(default);
        Assert.Equal("simulated", (await db.AlertDeliveries.SingleAsync()).Status);
        await service.ObserveAsync("test:database", "database", "P0", true, default);
        await service.ObserveAsync("test:database", "database", "P0", true, default); await service.DeliverSimulatedAsync(default);
        Assert.Equal(2, await db.AlertDeliveries.CountAsync()); Assert.All(await db.AlertDeliveries.ToArrayAsync(), d => { Assert.Equal("simulated", d.Status); Assert.Equal(1, d.Attempts); });
        await service.ObserveAsync("test:database", "database", "P0", false, default);
        Assert.Equal("suppressed", (await db.AlertDeliveries.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.IncidentVersion).FirstAsync()).Status);
        Assert.DoesNotContain(await db.AlertDeliveries.ToArrayAsync(), d => d.Status == "sent");
    }

    [PostgreSqlFact]
    public async Task Retention_aggregates_before_deleting_raw_data_and_does_not_duplicate_daily_totals()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        app.Clock.Now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var now = app.Clock.Now.UtcDateTime; var visitor = Guid.NewGuid(); var session = Guid.NewGuid();
        foreach (var at in new[] { now.AddDays(-31), now.AddDays(-30).AddHours(-1), now.AddDays(-30).AddHours(1), now.AddDays(-1), now.AddDays(-1) }) db.OperationalEvents.Add(new() {
            CreatedAt = at, Component = "website", Name = "page_view", Outcome = "success", Release = "test", Fingerprint = "website:landing:page_view", VisitorId = visitor, SessionId = session });
        db.DailyOperationalSummaries.Add(new() { Day = DateOnly.FromDateTime(now.AddMonths(-13)), Component = "website", Name = "page_view", Outcome = "success", Events = 99 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var service = scope.ServiceProvider.GetRequiredService<VisitorAnalyticsService>();
        await service.RetainAsync(default); db.ChangeTracker.Clear();
        Assert.Equal(3, await db.OperationalEvents.CountAsync()); Assert.Equal(3, await db.DailyOperationalSummaries.CountAsync());
        var daily = await db.DailyOperationalSummaries.SingleAsync(d => d.Day == DateOnly.FromDateTime(now.AddDays(-1)));
        Assert.Equal(2, daily.Events); Assert.Equal(1, daily.VisitorEstimate); Assert.Equal(1, daily.Sessions);
        db.ChangeTracker.Clear(); app.Clock.Now += TimeSpan.FromHours(2); await service.RetainAsync(default);
        Assert.Equal(5, await db.DailyOperationalSummaries.SumAsync(d => d.Events));
        Assert.Equal(2, await db.OperationalEvents.CountAsync());
        Assert.All(await db.OperationalEvents.ToArrayAsync(), e => Assert.True(e.CreatedAt >= app.Clock.Now.UtcDateTime.AddDays(-30)));
    }

    [PostgreSqlFact]
    public async Task Error_rate_alerts_require_volume_and_observation_window_and_do_not_repeat()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IncidentService>();
        for (var i = 0; i < 100; i++) db.OperationalEvents.Add(new() { CreatedAt = app.Clock.Now.UtcDateTime.AddSeconds(i == 0 ? -299 : -1),
            Component = "api", Name = "api_request", Outcome = i < 6 ? "failed" : "success", Release = "test", Fingerprint = "api:request", StatusCode = i < 6 ? 500 : 200 });
        await db.SaveChangesAsync(); await service.EvaluateRatesAsync(default); await service.EvaluateRatesAsync(default);
        Assert.Single(await db.AlertDeliveries.ToArrayAsync()); Assert.Equal("P1", (await db.OperationalIncidents.SingleAsync()).Severity);
        app.Clock.Now += TimeSpan.FromMinutes(6);
        await service.EvaluateRatesAsync(default); // No volume is unknown, not healthy.
        Assert.Equal("open", (await db.OperationalIncidents.SingleAsync()).Status);
    }

    [PostgreSqlFact]
    public async Task Security_failure_alerts_use_configured_threshold_deduplicate_and_recover()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IncidentService>();
        app.Services.GetRequiredService<IOptions<OperationsOptions>>().Value.SecurityFailuresPerFiveMinutes = 10;
        for (var i = 0; i < 9; i++) db.OperationalEvents.Add(new() { CreatedAt = app.Clock.Now.UtcDateTime,
            Component = "api", Name = "sign_in", Outcome = "rejected", Release = "test", Fingerprint = "api:sign_in:rejected", StatusCode = 401 });
        await db.SaveChangesAsync(); await service.EvaluateRatesAsync(default); Assert.Empty(await db.AlertDeliveries.ToArrayAsync());
        db.OperationalEvents.Add(new() { CreatedAt = app.Clock.Now.UtcDateTime, Component = "api", Name = "sign_in",
            Outcome = "rejected", Release = "test", Fingerprint = "api:sign_in:rejected", StatusCode = 401 });
        await db.SaveChangesAsync(); await service.EvaluateRatesAsync(default); await service.EvaluateRatesAsync(default);
        Assert.Single(await db.AlertDeliveries.ToArrayAsync());
        Assert.Equal("authentication_failures", (await db.OperationalIncidents.SingleAsync()).Fingerprint);
        app.Clock.Now += TimeSpan.FromMinutes(6);
        db.OperationalEvents.Add(new() { CreatedAt = app.Clock.Now.UtcDateTime, Component = "api", Name = "api_request",
            Outcome = "rejected", Release = "test", Fingerprint = "api:api_request:rejected", StatusCode = 403 });
        await db.SaveChangesAsync(); await service.EvaluateRatesAsync(default);
        Assert.Equal("open", (await db.OperationalIncidents.AsNoTracking().SingleAsync()).Status);
        app.Clock.Now += TimeSpan.FromMinutes(6);
        await service.EvaluateRatesAsync(default);
        Assert.Equal("resolved", (await db.OperationalIncidents.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2, await db.AlertDeliveries.CountAsync());
    }

    [PostgreSqlFact]
    public async Task Operations_observes_both_authorization_and_early_ingress_rejections()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        app.Services.GetRequiredService<IOptions<RequestSecurityOptions>>().Value.IngressRequestsPerMinute = 1;
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/auth/me")).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.Equal(new[] { 401, 429 }, await db.OperationalEvents.OrderBy(e => e.StatusCode).Select(e => e.StatusCode).ToArrayAsync());
    }

    [PostgreSqlFact]
    public async Task Rejected_request_telemetry_is_bounded_to_prevent_log_write_amplification()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        var events = app.Services.GetRequiredService<OperationalEvents>();
        for (var i = 0; i < 125; i++) await events.RecordAsync("api", "api_request", "rejected", status: 429);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.Equal(120, await db.OperationalEvents.CountAsync());
        await events.RecordAsync("api", "api_request", "success", status: 200);
        Assert.Equal(121, await db.OperationalEvents.CountAsync());
    }

    private static async Task<CurrentSessionResponse> Register(HttpClient client)
    {
        var result = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(Guid.NewGuid() + "@example.test", "Operations-test9!", "Support tester"));
        result.EnsureSuccessStatusCode(); return (await result.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
    }

    [PostgreSqlFact]
    public async Task Readiness_does_not_claim_an_unmigrated_schema_is_ready()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory(); using var client = app.Client();
        // Model fixtures create tables, not applied-migration history.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Customer_cancellation_is_recorded_without_a_false_server_failure()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestAborted = cancelled.Token };
        context.Request.Path = "/api/test-cancel";
        var middleware = app.Services.GetRequiredService<OperationsMiddleware>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(context, _ => throw new OperationCanceledException(cancelled.Token)));
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var recorded = await db.OperationalEvents.SingleAsync(); Assert.Equal("cancelled", recorded.Outcome); Assert.Equal(499, recorded.StatusCode);
    }

    [PostgreSqlFact]
    public async Task Capture_alert_requires_multiple_accounts_and_a_complete_window()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new OperationsFactory();
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IncidentService>(); var now = app.Clock.Now.UtcDateTime;
        var accounts = Enumerable.Range(0, 3).Select(i => new UserAccount { Email = $"capture-{i}@example.test", DisplayName = "Fixture", PasswordHash = "fixture", PasswordSalt = "fixture", CreatedDate = now, UpdatedDate = now }).ToArray();
        db.UserAccounts.AddRange(accounts); await db.SaveChangesAsync();
        var connections = new List<AiConnection>();
        foreach (var account in accounts)
        {
            var authorization = new OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization { Id = Guid.NewGuid().ToString(), Status = "valid", Subject = account.Id.ToString(), Type = "permanent" };
            db.Add(authorization); var connection = new AiConnection { UserAccountId = account.Id, AuthorizationId = authorization.Id, ClientId = "test", DisplayName = "Fixture", GrantedScopes = "extension.capture", CreatedDate = now };
            db.AiConnections.Add(connection); connections.Add(connection);
        }
        await db.SaveChangesAsync();
        void Failure(int owner, DateTime completed) => db.ExtensionTasks.Add(new() { UserAccountId = accounts[owner].Id, AiConnectionId = connections[owner].Id,
            RequestId = Guid.NewGuid(), RequestHash = new string('A', 64), Type = "capture", Status = "failed", Retailer = "coles", Url = "https://www.coles.com.au/product/123456",
            ShoppingListId = 1, Quantity = 1, CreatedAt = completed.AddMinutes(-1), ExpiresAt = completed.AddMinutes(9), CompletedAt = completed, ErrorCode = "retailer_blocked" });
        for (var i = 0; i < 20; i++) Failure(0, now.AddSeconds(i == 0 ? -899 : -1));
        await db.SaveChangesAsync(); await service.EvaluateRatesAsync(default); Assert.Empty(await db.AlertDeliveries.ToArrayAsync());
        Failure(1, now.AddSeconds(-1)); Failure(2, now.AddSeconds(-1)); await db.SaveChangesAsync(); await service.EvaluateRatesAsync(default);
        var incident = await db.OperationalIncidents.SingleAsync(); Assert.Equal("extension_capture_error_rate", incident.Fingerprint); Assert.Equal("P1", incident.Severity);
        Assert.Single(await db.AlertDeliveries.ToArrayAsync());
    }
    private sealed class OperationsFactory : WebApplicationFactory<Program>
    {
        public TestClock Clock { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Operations:Enabled", "true");
            builder.UseSetting("Operations:SimulateDelivery", "true");
            builder.ConfigureServices(services => {
                services.RemoveAll<IHostedService>(); services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.PostConfigure<OperationsOptions>(o => { o.IncidentRecipients = ["alerts@example.test"]; o.SimulateDelivery = true; });
                services.PostConfigure<AuthOptions>(o => o.RegistrationRequestsPerWindow = 100);
                services.RemoveAll<DbContextOptions<MyShoppingListDbContext>>(); services.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
        public HttpClient Client() { var client = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); client.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1"); return client; }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); await Serilog.Log.CloseAndFlushAsync(); }
    }
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
