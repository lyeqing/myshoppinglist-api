using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using Stripe;

namespace myshoppinglist_api.Services;

public static class BillingSetup
{
    public static void AddTestBilling(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var o = configuration.GetSection("Billing").Get<BillingOptions>() ?? new();
        Validate(o, environment.IsDevelopment());
        services.Configure<BillingOptions>(configuration.GetSection("Billing"));
        services.AddScoped<ITestBillingProvider, StripeTestBillingProvider>(); services.AddScoped<TestBillingService>();
        if (o.Enabled) services.AddHostedService<TestBillingMaintenance>();
    }
    public static void Validate(BillingOptions o, bool development)
    {
        if (!o.Enabled) return;
        if (!development || o.SecretKey.Length > 0 && !o.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal)
            || o.WebhookSecret.Length > 0 && !o.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal)
            || o.WebsiteOrigin.Length > 0 && (!Uri.TryCreate(o.WebsiteOrigin, UriKind.Absolute, out var origin)
                || origin.UserInfo.Length > 0 || origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0
                || !(origin.Scheme == "https" || origin.Scheme == "http" && origin.IsLoopback)))
            throw new InvalidOperationException("Billing groundwork permits Development and test credentials only, with a fixed safe website origin.");
    }
    public static async Task ReceiveAsync(string body, string signature, MyShoppingListDbContext db, BillingOptions options, TimeProvider clock, CancellationToken ct)
    {
        if (options.WebhookSecret.Length == 0) throw new UserImportException(503, "Test webhook is not configured.");
        try {
            var timestamp = signature.Split(',').Single(x => x.StartsWith("t=", StringComparison.Ordinal))[2..];
            if (!long.TryParse(timestamp, out var t) || Math.Abs((decimal)clock.GetUtcNow().ToUnixTimeSeconds() - t) > 300) throw new FormatException();
            EventUtility.ValidateSignature(body, signature, options.WebhookSecret, 300, clock.GetUtcNow().ToUnixTimeSeconds());
            using var json = JsonDocument.Parse(body); var root = json.RootElement;
            if (root.GetProperty("livemode").GetBoolean()) throw new FormatException();
            var id = root.GetProperty("id").GetString()!; var type = root.GetProperty("type").GetString()!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^evt_[a-zA-Z0-9]{1,180}$")) throw new FormatException();
            var supported = type is "checkout.session.completed" or "checkout.session.expired" or "customer.subscription.created" or "customer.subscription.updated"
                or "customer.subscription.deleted" or "invoice.paid" or "invoice.payment_failed" or "invoice.voided" or "invoice.finalized" or "charge.refunded";
            if (!supported) return;
            var customer = root.GetProperty("data").GetProperty("object").GetProperty("customer").GetString()!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(customer, "^cus_[a-zA-Z0-9]{1,180}$")) throw new FormatException();
            var now = clock.GetUtcNow().UtcDateTime;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"BillingWebhookEvents\" (\"Id\",\"CustomerId\",\"Type\",\"ReceivedAt\",\"NextAttemptAt\",\"Attempts\",\"Status\") VALUES ({id},{customer},{type},{now},{now},0,'pending') ON CONFLICT (\"Id\") DO NOTHING", ct);
        }
        catch (Exception ex) when (ex is StripeException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException or ArgumentException) {
            throw new UserImportException(400, "Invalid test webhook.");
        }
    }
}

public sealed class TestBillingMaintenance(IServiceScopeFactory scopes, TimeProvider clock, ILogger<TestBillingMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do {
            try {
                await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
                var now = clock.GetUtcNow().UtcDateTime;
                var customers = await db.BillingCustomers.AsNoTracking().Where(c => c.ReconciledAt == null || c.ReconciledAt < now.AddHours(-1))
                    .OrderBy(c => c.ReconciledAt).Take(100).Select(c => c.CustomerId).ToArrayAsync(stoppingToken);
                foreach (var customer in customers) {
                    var id = $"sync_{customer}_{now:yyyyMMddHH}";
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"BillingWebhookEvents\" (\"Id\",\"CustomerId\",\"Type\",\"ReceivedAt\",\"NextAttemptAt\",\"Attempts\",\"Status\") VALUES ({id},{customer},'scheduled_reconciliation',{now},{now},0,'pending') ON CONFLICT (\"Id\") DO NOTHING", stoppingToken);
                }
                var ids = await db.BillingWebhookEvents.AsNoTracking().Where(e => e.Status == "pending" && e.NextAttemptAt <= now)
                    .OrderBy(e => e.ReceivedAt).Take(100).Select(e => e.Id).ToArrayAsync(stoppingToken);
                foreach (var id in ids) await scope.ServiceProvider.GetRequiredService<TestBillingService>().ProcessAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Test billing reconciliation is unavailable; pending work is retained."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
