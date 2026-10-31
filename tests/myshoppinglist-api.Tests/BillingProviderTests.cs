using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Services;
using Npgsql;
using Stripe;

namespace myshoppinglist_api.Tests;

[Collection("Import worker database")]
public sealed class BillingProviderTests
{
    [Fact]
    public async Task Hosted_checkout_uses_server_price_fixed_returns_and_provider_idempotency()
    {
        var handler = new FixtureHandler();
        var provider = new StripeTestBillingProvider(Options.Create(new BillingOptions { SecretKey = "sk_test_fixture", MonthlyPriceId = "price_test", WebsiteOrigin = "https://example.test" }),
            new StripeClient("sk_test_fixture", httpClient: new SystemNetHttpClient(new HttpClient(handler))));
        var request = Guid.NewGuid();
        Assert.Equal("https://checkout.stripe.com/c/test_fixture", await provider.CheckoutAsync("cus_fixture", "monthly", request, default));
        Assert.Equal($"msl-test-checkout-cus_fixture-{request:N}", handler.Idempotency);
        Assert.Contains("price_test", handler.Body); Assert.Contains("cus_fixture", handler.Body);
        Assert.Contains("example.test", handler.Body); Assert.DoesNotContain("card_number", handler.Body);
        Assert.Equal("https://billing.stripe.com/p/test_fixture", await provider.PortalAsync("cus_fixture", default));
    }
    [Fact]
    public async Task Real_sdk_adapter_projects_only_current_test_records_and_rejects_live_or_mismatched_data()
    {
        var handler = new FixtureHandler();
        var provider = new StripeTestBillingProvider(Options.Create(new BillingOptions { SecretKey = "sk_test_fixture" }),
            new StripeClient("sk_test_fixture", httpClient: new SystemNetHttpClient(new HttpClient(handler))));
        var snapshot = await provider.ReadAsync("cus_fixture", default);
        var s = Assert.Single(snapshot.Subscriptions); Assert.Equal(1000, s.MonthlyAudCents); Assert.True(s.CancelAtPeriodEnd);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000).UtcDateTime, s.CurrentPeriodEnd);
        Assert.Equal(10000, Assert.Single(snapshot.Payments).PaidCents); Assert.Equal(400, Assert.Single(snapshot.Payments).RefundedCents); Assert.Equal("https://invoice.stripe.com/i/test", Assert.Single(snapshot.Invoices).ReceiptUrl);
        Assert.Equal(3, handler.Requests); Assert.DoesNotContain("card", System.Text.Json.JsonSerializer.Serialize(snapshot));
        handler.Live = true; await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadAsync("cus_fixture", default));
        handler.Live = false; handler.Customer = "cus_someoneelse";
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadAsync("cus_fixture", default));
    }
    [PostgreSqlFact]
    public async Task Migration_preserves_existing_data_and_refuses_to_drop_billing_history()
    {
        await using var f = await MigrationRehearsalScope.CreateAsync(); await f.SeedAsync();
        var before = await f.FingerprintAsync(legacyOnly: true);
        await f.Db.GetService<IMigrator>().MigrateAsync("20261031043409_AddOperationsAndPrivateFeedback");
        Assert.Equal(before, await f.FingerprintAsync(legacyOnly: true));
        await f.Db.Database.MigrateAsync();
        Assert.False(f.Db.Database.HasPendingModelChanges());
        f.Db.BillingCustomers.Add(new() { UserAccountId = f.A.Id, CustomerId = "cus_fixture" }); await f.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => f.Db.GetService<IMigrator>().MigrateAsync("20261031043409_AddOperationsAndPrivateFeedback"));
        Assert.Contains("Cannot remove billing reconciliation history", error.MessageText);
        Assert.Equal("cus_fixture", (await f.Db.BillingCustomers.SingleAsync()).CustomerId);
    }
    private sealed class FixtureHandler : HttpMessageHandler
    {
        public bool Live; public string Customer = "cus_fixture"; public int Requests;
        public string? Idempotency; public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.Method == HttpMethod.Post) {
                Body = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (request.Headers.TryGetValues("Idempotency-Key", out var keys)) Idempotency = keys.Single();
                var portal = request.RequestUri!.AbsolutePath.Contains("billing_portal");
                return Reply(portal ? "{\"id\":\"bps_test\",\"object\":\"billing_portal.session\",\"url\":\"https://billing.stripe.com/p/test_fixture\"}"
                    : "{\"id\":\"cs_test\",\"object\":\"checkout.session\",\"livemode\":false,\"url\":\"https://checkout.stripe.com/c/test_fixture\"}");
            }
            if (request.RequestUri!.AbsolutePath == "/v1/prices/price_test") return Reply("{\"id\":\"price_test\",\"object\":\"price\",\"active\":true,\"livemode\":false,\"currency\":\"aud\",\"recurring\":{\"interval\":\"month\",\"interval_count\":1}}");
            var common = $"\"livemode\":{Live.ToString().ToLowerInvariant()},\"customer\":\"{Customer}\"";
            var row = request.RequestUri!.AbsolutePath switch {
                "/v1/subscriptions" => "{\"id\":\"sub_fixture\",\"status\":\"active\",\"cancel_at_period_end\":true," + common + ",\"items\":{\"has_more\":false,\"data\":[{\"current_period_end\":1800000000,\"quantity\":1,\"price\":{\"currency\":\"aud\",\"livemode\":false,\"unit_amount\":12000,\"recurring\":{\"interval\":\"year\",\"interval_count\":1}}}]}}",
                "/v1/invoices" => "{\"id\":\"in_fixture\",\"status\":\"paid\",\"amount_paid\":12000,\"currency\":\"aud\",\"hosted_invoice_url\":\"https://invoice.stripe.com/i/test\"," + common + "}",
                "/v1/charges" => "{\"id\":\"ch_fixture\",\"paid\":true,\"amount\":12000,\"amount_captured\":10000,\"amount_refunded\":400,\"currency\":\"aud\"," + common + "}",
                _ => throw new InvalidOperationException("Unexpected provider request")
            };
            return Reply("{\"object\":\"list\",\"has_more\":false,\"data\":[" + row + "]}");
        }
        private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
