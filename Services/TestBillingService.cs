using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class TestBillingService(MyShoppingListDbContext db, ITestBillingProvider provider, IOptions<BillingOptions> options, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static BillingSnapshot Snapshot(BillingCustomer c) => c.ReconciledAt == null ? new([], [], []) : JsonSerializer.Deserialize<BillingSnapshot>(c.SnapshotJson, Json)!;
    public async Task<object> OwnAsync(long account, CancellationToken ct)
    {
        var a = await AccountAsync(account, ct); var c = options.Value.Enabled ? await db.BillingCustomers.AsNoTracking().SingleOrDefaultAsync(x => x.UserAccountId == account, ct) : null;
        var snapshot = c == null ? new BillingSnapshot([], [], []) : Snapshot(c);
        return new { testOnly = true, enabled = options.Value.Enabled, checkoutConfigured = Configured(),
            entitlement = new { source = a.IsTrial ? "trial" : a.IsPaid ? "manual_paid_or_complimentary" : "free", activeListLimit = a.IsPaid ? 5 : 2, trialExpiresAt = a.ExpiresDate },
            testEntitlement = new { appliesToAccount = false, eligible = snapshot.Subscriptions.Any(s => s.Status == "active" && s.CurrentPeriodEnd > clock.GetUtcNow().UtcDateTime) },
            reconciledAt = c?.ReconciledAt, snapshot.Subscriptions, snapshot.Invoices, snapshot.Payments };
    }
    private bool Configured() => options.Value.Enabled && options.Value.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal)
        && options.Value.WebhookSecret.Length > 0 && options.Value.WebsiteOrigin.Length > 0 && options.Value.MonthlyPriceId.Length > 0 && options.Value.AnnualPriceId.Length > 0;
    private async Task<UserAccount> AccountAsync(long id, CancellationToken ct) => await db.UserAccounts.SingleOrDefaultAsync(a => a.Id == id && a.IsActive, ct)
        ?? throw new UserImportException(403, "Account unavailable.");
    private async Task LockAsync(long account, CancellationToken ct) => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({account.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 508))", ct);
    public async Task<string> CheckoutAsync(long account, BillingAction action, CancellationToken ct)
    {
        if (action.ExpectedAccountId != account) throw new UserImportException(409, "Your account changed. Review billing before continuing.");
        if (action.RequestId == Guid.Empty || action.Plan is not ("monthly" or "annual")) throw new UserImportException(400, "Choose a test billing plan.");
        if (!Configured()) throw new UserImportException(503, "Test billing and prices are not configured.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(account, ct);
        var a = await AccountAsync(account, ct);
        if (a.IsTrial) throw new UserImportException(403, "Register an account before testing billing.");
        var now = clock.GetUtcNow().UtcDateTime;
        var attempt = await db.BillingCheckoutAttempts.SingleOrDefaultAsync(x => x.UserAccountId == account && x.RequestId == action.RequestId, ct);
        if (attempt != null) {
            if (attempt.Plan != action.Plan || attempt.CreatedAt <= now.AddHours(-23)) throw new UserImportException(409, "This checkout request expired or changed. Review billing before starting another.");
            if (attempt.Url != null) return attempt.Url;
        }
        if (await db.BillingCheckoutAttempts.AnyAsync(x => x.UserAccountId == account && x.RequestId != action.RequestId && x.CreatedAt > now.AddHours(-24), ct))
            throw new UserImportException(409, "A test checkout was already started today. Reuse it or review the customer portal.");
        var c = await db.BillingCustomers.SingleOrDefaultAsync(x => x.UserAccountId == account, ct);
        if (c == null) { c = new() { UserAccountId = account, CustomerId = await provider.CustomerAsync(account, ct) }; db.BillingCustomers.Add(c); }
        if (attempt == null) { attempt = new() { UserAccountId = account, RequestId = action.RequestId, Plan = action.Plan, CreatedAt = now }; db.BillingCheckoutAttempts.Add(attempt); }
        // Commit the retry fence before contacting Checkout. A lost response retains the same key.
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        await using var checkout = await db.Database.BeginTransactionAsync(ct); await LockAsync(account, ct);
        await db.Entry(attempt).ReloadAsync(ct);
        if (attempt.Url == null) {
            var snapshot = await provider.ReadAsync(c.CustomerId, ct);
            if (snapshot.Subscriptions.Any(s => s.Status is not ("canceled" or "incomplete_expired")))
                throw new UserImportException(409, "A test subscription already exists. Use the customer portal.");
            attempt.Url = await provider.CheckoutAsync(c.CustomerId, action.Plan, action.RequestId, ct);
            await db.SaveChangesAsync(ct);
        }
        await checkout.CommitAsync(ct); return attempt.Url;
    }
    public async Task<string> PortalAsync(long account, long expected, CancellationToken ct)
    {
        if (expected != account) throw new UserImportException(409, "Your account changed. Review billing before continuing.");
        if (!options.Value.Enabled) throw new UserImportException(503, "Test billing is disabled.");
        await AccountAsync(account, ct);
        var c = await db.BillingCustomers.AsNoTracking().SingleOrDefaultAsync(x => x.UserAccountId == account, ct)
            ?? throw new UserImportException(409, "No test billing customer exists.");
        return await provider.PortalAsync(c.CustomerId, ct);
    }
    public async Task ProcessAsync(string id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var customerId = await db.BillingWebhookEvents.Where(e => e.Id == id && e.Status == "pending").Select(e => e.CustomerId).SingleOrDefaultAsync(ct);
        if (customerId == null) return;
        // Serialize the remote read as well as its write: an older event always reads current state.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({customerId}, 509))", ct);
        var e = await db.BillingWebhookEvents.SingleAsync(e => e.Id == id, ct);
        if (e.Status != "pending" || e.NextAttemptAt > clock.GetUtcNow().UtcDateTime) return;
        var c = await db.BillingCustomers.SingleOrDefaultAsync(c => c.CustomerId == customerId, ct);
        e.Attempts++;
        if (c == null) { e.Status = "unmatched"; }
        else {
            try {
                var snapshot = await provider.ReadAsync(customerId, ct);
                c.SnapshotJson = JsonSerializer.Serialize(snapshot, Json); c.ReconciledAt = clock.GetUtcNow().UtcDateTime;
                e.Status = "complete"; e.CompletedAt = c.ReconciledAt;
            }
            catch (Exception) when (!ct.IsCancellationRequested) {
                // Keep the last good ledger; never manufacture a cancellation during an outage.
                // A provider/HTTP timeout can be an OperationCanceledException without
                // cancellation of our request. Persist its retry just like other outages.
                e.NextAttemptAt = clock.GetUtcNow().UtcDateTime.AddMinutes(Math.Min(60, e.Attempts * 2));
            }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
