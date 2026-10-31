using System.Text.Json;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using Stripe;

namespace myshoppinglist_api.Services;

public interface ITestBillingProvider
{
    Task<string> CustomerAsync(long account, CancellationToken ct);
    Task<string> CheckoutAsync(string customer, string plan, Guid request, CancellationToken ct);
    Task<string> PortalAsync(string customer, CancellationToken ct);
    Task<BillingSnapshot> ReadAsync(string customer, CancellationToken ct);
}

// Raw SDK responses are projected to a deliberately small, version-independent ledger.
// No raw event, card data, addresses, customer emails or provider exception text is stored.
public sealed class StripeTestBillingProvider(IOptions<BillingOptions> options, StripeClient? testClient = null) : ITestBillingProvider
{
    private BillingOptions Settings => options.Value;
    private static readonly SystemNetHttpClient Transport = new(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
    private StripeClient Client => testClient ?? new(Settings.SecretKey, httpClient: Transport);
    private void Configured()
    {
        if (!Settings.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal))
            throw new UserImportException(503, "Test billing is not configured.");
    }
    public async Task<string> CustomerAsync(long account, CancellationToken ct)
    {
        Configured();
        var c = await new CustomerService(Client).CreateAsync(new CustomerCreateOptions(),
            new RequestOptions { IdempotencyKey = $"msl-test-customer-{account}" }, ct);
        if (c.Livemode) throw new InvalidOperationException("Live billing is forbidden.");
        return c.Id;
    }
    public async Task<string> CheckoutAsync(string customer, string plan, Guid request, CancellationToken ct)
    {
        Configured(); var price = plan == "monthly" ? Settings.MonthlyPriceId : Settings.AnnualPriceId;
        if (string.IsNullOrEmpty(price)) throw new UserImportException(503, "Test prices are not configured.");
        var p = await new Stripe.PriceService(Client).GetAsync(price, cancellationToken: ct);
        if (p.Livemode || !p.Active || p.Currency != "aud" || p.Recurring?.IntervalCount != 1
            || p.Recurring.Interval != (plan == "monthly" ? "month" : "year"))
            throw new UserImportException(503, "An approved AUD test price is required.");
        var session = await new Stripe.Checkout.SessionService(Client).CreateAsync(new Stripe.Checkout.SessionCreateOptions {
            Customer = customer, Mode = "subscription", LineItems = [new() { Price = price, Quantity = 1 }],
            SuccessUrl = Settings.WebsiteOrigin.TrimEnd('/') + "/account/billing?checkout=returned",
            CancelUrl = Settings.WebsiteOrigin.TrimEnd('/') + "/account/billing?checkout=cancelled",
        }, new RequestOptions { IdempotencyKey = $"msl-test-checkout-{customer}-{request:N}" }, ct);
        if (session.Livemode) throw new InvalidOperationException("Live billing is forbidden.");
        return SafeUrl(session.Url, "checkout.stripe.com") ?? throw new InvalidOperationException("Invalid hosted checkout URL.");
    }
    public async Task<string> PortalAsync(string customer, CancellationToken ct)
    {
        Configured();
        var session = await new Stripe.BillingPortal.SessionService(Client).CreateAsync(new() {
            Customer = customer, ReturnUrl = Settings.WebsiteOrigin.TrimEnd('/') + "/account/billing"
        }, cancellationToken: ct);
        return SafeUrl(session.Url, "billing.stripe.com") ?? throw new InvalidOperationException("Invalid hosted portal URL.");
    }
    public async Task<BillingSnapshot> ReadAsync(string customer, CancellationToken ct)
    {
        Configured();
        var subscriptions = await AllAsync("subscriptions", customer, "&status=all", ct);
        var invoices = await AllAsync("invoices", customer, "", ct);
        var charges = await AllAsync("charges", customer, "", ct);
        return new(subscriptions.Select(s => {
            var items = s.GetProperty("items");
            if (items.GetProperty("has_more").GetBoolean()) throw new InvalidOperationException("Subscription requires manual review.");
            var rows = items.GetProperty("data").EnumerateArray().ToArray();
            DateTime? until = rows.Length == 0 ? null : rows.Min(i => DateTimeOffset.FromUnixTimeSeconds(i.GetProperty("current_period_end").GetInt64()).UtcDateTime);
            long monthly = 0;
            foreach (var row in rows) {
                var price = row.GetProperty("price"); var recurring = price.GetProperty("recurring");
                if (price.GetProperty("currency").GetString() != "aud" || price.GetProperty("livemode").GetBoolean()
                    || recurring.GetProperty("interval_count").GetInt32() != 1 || !price.TryGetProperty("unit_amount", out var amount) || amount.ValueKind != JsonValueKind.Number)
                    throw new InvalidOperationException("Subscription requires manual review.");
                var interval = recurring.GetProperty("interval").GetString();
                monthly += interval switch { "month" => amount.GetInt64() * row.GetProperty("quantity").GetInt64(),
                    "year" => amount.GetInt64() * row.GetProperty("quantity").GetInt64() / 12,
                    _ => throw new InvalidOperationException("Subscription requires manual review.") };
            }
            return new TestSubscription(s.GetProperty("id").GetString()!, s.GetProperty("status").GetString()!, until,
                s.GetProperty("cancel_at_period_end").GetBoolean(), monthly);
        }).ToArray(), invoices.Select(i => new TestInvoice(i.GetProperty("id").GetString()!, i.GetProperty("status").GetString() ?? "draft",
            i.GetProperty("amount_paid").GetInt64(), i.GetProperty("currency").GetString()!,
            i.TryGetProperty("hosted_invoice_url", out var url) ? SafeUrl(url.GetString(), "invoice.stripe.com") : null)).ToArray(),
            charges.Where(c => c.GetProperty("paid").GetBoolean() && c.GetProperty("amount_captured").GetInt64() > 0).Select(c => new TestPayment(c.GetProperty("id").GetString()!, c.GetProperty("amount_captured").GetInt64(),
                c.GetProperty("amount_refunded").GetInt64(), c.GetProperty("currency").GetString()!)).ToArray());
    }
    private async Task<JsonElement[]> AllAsync(string resource, string customer, string extra, CancellationToken ct)
    {
        var rows = new List<JsonElement>(); string? cursor = null;
        // Hard cap bounds each reconciliation. Never replace a ledger with a truncated result.
        for (var page = 0; page < 10; page++) {
            var response = await Client.RawRequestAsync(HttpMethod.Get, $"/v1/{resource}?customer={Uri.EscapeDataString(customer)}&limit=100{extra}"
                + (cursor == null ? "" : "&starting_after=" + Uri.EscapeDataString(cursor)), null, null, ct);
            using var json = JsonDocument.Parse(response.Content); var root = json.RootElement;
            var batch = root.GetProperty("data").EnumerateArray().Select(x => x.Clone()).ToArray();
            foreach (var row in batch) if (row.GetProperty("livemode").GetBoolean() || row.GetProperty("customer").GetString() != customer)
                throw new InvalidOperationException("Billing customer mismatch.");
            rows.AddRange(batch);
            if (!root.GetProperty("has_more").GetBoolean()) return rows.ToArray();
            cursor = batch.Last().GetProperty("id").GetString();
        }
        throw new InvalidOperationException("Billing history requires manual review.");
    }
    internal static string? SafeUrl(string? value, string host) => Uri.TryCreate(value, UriKind.Absolute, out var u)
        && u.Scheme == "https" && u.Host == host && u.IsDefaultPort && u.UserInfo.Length == 0 ? u.AbsoluteUri : null;
}
