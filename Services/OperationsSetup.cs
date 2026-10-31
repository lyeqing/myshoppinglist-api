using System.Diagnostics;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;

namespace myshoppinglist_api.Services;

public static class OperationsSetup
{
    public static void AddOperations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRateLimiter(o => o.AddPolicy("OperationsInput", c => RateLimitPartition.GetFixedWindowLimiter(
            c.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? c.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
        var settings = configuration.GetSection("Operations").Get<OperationsOptions>() ?? new();
        services.Configure<OperationsOptions>(configuration.GetSection("Operations"));
        if (!settings.Enabled) return;
        if (!settings.SimulateDelivery || settings.RawRetentionDays != 30 || settings.AggregateRetentionMonths != 12
            || settings.SecurityFailuresPerFiveMinutes is < 10 or > 600
            || !Regex.IsMatch(settings.Release, "^[a-zA-Z0-9._-]{1,64}$") || settings.IncidentRecipients.Length > 5
            || settings.IncidentRecipients.Any(r => !System.Net.Mail.MailAddress.TryCreate(r, out _)))
            throw new InvalidOperationException("Operations requires simulated delivery, approved retention defaults, a safe release and valid configured recipients.");
        services.AddScoped<FeedbackService>(); services.AddScoped<VisitorAnalyticsService>(); services.AddScoped<IncidentService>();
        services.AddSingleton<OperationalEvents>(); services.AddTransient<OperationsMiddleware>(); services.AddHostedService<OperationsMaintenance>();
    }
}

public sealed class OperationsMiddleware(OperationalEvents events) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var started = Stopwatch.GetTimestamp(); var failed = false; var cancelled = false;
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { cancelled = true; throw; }
        catch { failed = true; throw; }
        finally
        {
            var path = context.Request.Path;
            string? name = path == "/mcp" ? "mcp_request" : path == "/connect/token" ? "oauth_token"
                : path == "/api/auth/login" || path == "/api/auth/mobile/login" || path == "/api/user-extension/login" ? "sign_in"
                : path == "/api/auth/register" ? "registration" : path == "/api/shopping-lists" && context.Request.Method == "POST" ? "list_creation"
                : path.StartsWithSegments("/api/user-extension/captures") ? "personal_capture"
                : path.StartsWithSegments("/api/extension-device/tasks") && context.Request.Method == "POST" ? "extension_task_action"
                : path.StartsWithSegments("/api") ? "api_request"
                : context.Response.StatusCode >= 500 || failed ? "api_error" : null;
            if (name != null)
            {
                var status = cancelled ? 499 : failed ? 500 : context.Response.StatusCode;
                var account = long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (long?)null;
                if (context.Items[myshoppinglist_api.Endpoints.AuthEndpoints.ResultAccountItem] is long authenticatedAccount) account = authenticatedAccount;
                await events.RecordAsync("api", name, cancelled ? "cancelled" : status >= 500 ? "failed" : status >= 400 ? "rejected" : "success", account,
                    Activity.Current?.TraceId.ToString(), (int)Math.Min(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 120000), status);
            }
        }
    }
}

public sealed class OperationsMaintenance(IServiceScopeFactory scopes, ILogger<OperationsMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1)); var nextRetention = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var incident = scope.ServiceProvider.GetRequiredService<IncidentService>();
                await incident.EvaluateRatesAsync(stoppingToken); await incident.DeliverSimulatedAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= nextRetention)
                {
                    await scope.ServiceProvider.GetRequiredService<VisitorAnalyticsService>().RetainAsync(stoppingToken);
                    nextRetention = DateTimeOffset.UtcNow.AddHours(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Operations maintenance unavailable; durable pending records remain for retry"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
