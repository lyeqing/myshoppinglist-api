using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
public sealed class BillingTests
{
    [Theory]
    [InlineData(false, "sk_test_fixture", "http://localhost:3001", false)]
    [InlineData(true, "sk_live_fixture", "https://example.test", false)]
    [InlineData(true, "sk_test_fixture", "https://user:secret@example.test", false)]
    [InlineData(true, "sk_test_fixture", "https://example.test/path", false)]
    [InlineData(true, "sk_test_fixture", "http://example.test", false)]
    [InlineData(true, "sk_test_fixture", "http://localhost:3001", true)]
    public void Only_development_test_configuration_is_supported(bool development, string key, string origin, bool valid)
    {
        var o = new BillingOptions { Enabled = true, SecretKey = key, WebsiteOrigin = origin };
        if (valid) BillingSetup.Validate(o, development); else Assert.Throws<InvalidOperationException>(() => BillingSetup.Validate(o, development));
    }
    [PostgreSqlFact]
    public async Task Checkout_is_owner_bound_retry_safe_and_never_grants_access_from_a_redirect()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new Factory();
        using var a = app.Client(); using var b = app.Client(); using var anon = app.Client();
        var owner = await Register(a); await Register(b);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/billing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/admin/billing")).StatusCode);
        var action = new BillingAction(owner.Account.Id, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsJsonAsync("/api/billing/checkout", action)).StatusCode);
        var results = await Task.WhenAll(a.PostAsJsonAsync("/api/billing/checkout", action), a.PostAsJsonAsync("/api/billing/checkout", action));
        foreach (var result in results) result.EnsureSuccessStatusCode();
        Assert.Equal(1, app.Provider.Checkouts); Assert.Equal(1, app.Provider.Customers);
        (await a.PostAsJsonAsync("/api/billing/checkout", action)).EnsureSuccessStatusCode(); Assert.Equal(1, app.Provider.Checkouts);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/billing/checkout", action with { Plan = "annual" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/billing/checkout", action with { RequestId = Guid.NewGuid() })).StatusCode);
        var own = await a.GetFromJsonAsync<JsonElement>("/api/billing?checkout=returned");
        Assert.False(own.GetProperty("testEntitlement").GetProperty("appliesToAccount").GetBoolean());
        Assert.Equal(2, own.GetProperty("entitlement").GetProperty("activeListLimit").GetInt32());
        Assert.Empty((await b.GetFromJsonAsync<JsonElement>("/api/billing")).GetProperty("subscriptions").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsJsonAsync("/api/billing/portal", action)).StatusCode);
        (await a.PostAsJsonAsync("/api/billing/portal", action)).EnsureSuccessStatusCode(); Assert.Equal("cus_fixture", app.Provider.LastCustomer);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        Assert.False((await db.UserAccounts.SingleAsync(x => x.Id == owner.Account.Id)).IsPaid);
    }
    [PostgreSqlFact]
    public async Task Signed_duplicate_out_of_order_failed_payment_refund_and_outage_reconciliation_preserves_access()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new Factory();
        using var a = app.Client(); var owner = await Register(a);
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            db.BillingCustomers.Add(new() { UserAccountId = owner.Account.Id, CustomerId = "cus_fixture" }); await db.SaveChangesAsync();
        }
        var body = Event("evt_new", "customer.subscription.updated");
        Assert.Equal(HttpStatusCode.BadRequest, (await Webhook(a, body, "bad")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Webhook(a, body, Sign(body, DateTimeOffset.UtcNow.AddMinutes(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Webhook(a, body, Sign(body, DateTimeOffset.UtcNow.AddMinutes(10)))).StatusCode);
        var live = body.Replace("\"livemode\":false", "\"livemode\":true");
        Assert.Equal(HttpStatusCode.BadRequest, (await Webhook(a, live, Sign(live))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Webhook(a, new string('x', 131073), "bad")).StatusCode);
        foreach (var result in await Task.WhenAll(Webhook(a, body, Sign(body)), Webhook(a, body, Sign(body)))) result.EnsureSuccessStatusCode();
        app.Provider.State = new([new("sub_fixture", "active", DateTime.UtcNow.AddDays(20), true, 999)], [new("in_fixture", "paid", 999, "aud", null)], [new("ch_fixture", 999, 0, "aud")]);
        await Process(app, "evt_new");
        var current = await a.GetFromJsonAsync<JsonElement>("/api/billing"); Assert.True(current.GetProperty("testEntitlement").GetProperty("eligible").GetBoolean());
        Assert.False(current.GetProperty("testEntitlement").GetProperty("appliesToAccount").GetBoolean());
        // A late old event reads the current provider state, not its stale embedded subscription.
        app.Provider.State = new([new("sub_fixture", "past_due", DateTime.UtcNow.AddDays(-1), false, 999)], [], [new("ch_fixture", 999, 400, "aud")]);
        var old = Event("evt_old", "invoice.payment_failed"); (await Webhook(a, old, Sign(old))).EnsureSuccessStatusCode();
        app.Provider.Fail = true; await Process(app, "evt_old");
        var stale = await a.GetFromJsonAsync<JsonElement>("/api/billing"); Assert.Equal("active", stale.GetProperty("subscriptions")[0].GetProperty("status").GetString());
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            var pending = await db.BillingWebhookEvents.SingleAsync(e => e.Id == "evt_old"); Assert.Equal("pending", pending.Status); Assert.Equal(1, pending.Attempts);
            pending.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        }
        app.Provider.Fail = false; await Process(app, "evt_old");
        current = await a.GetFromJsonAsync<JsonElement>("/api/billing"); Assert.Equal("past_due", current.GetProperty("subscriptions")[0].GetProperty("status").GetString());
        Assert.Equal(400, current.GetProperty("payments")[0].GetProperty("refundedCents").GetInt64());
        Assert.False(current.GetProperty("testEntitlement").GetProperty("eligible").GetBoolean());
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            Assert.Equal(2, await db.BillingWebhookEvents.CountAsync()); Assert.All(await db.BillingWebhookEvents.ToArrayAsync(), e => Assert.Equal("complete", e.Status));
            var account = await db.UserAccounts.SingleAsync(x => x.Id == owner.Account.Id); Assert.False(account.IsPaid);
            account.IsPaid = true; await db.SaveChangesAsync(); // Existing administrator complimentary status is independent.
        }
        current = await a.GetFromJsonAsync<JsonElement>("/api/billing"); Assert.Equal(5, current.GetProperty("entitlement").GetProperty("activeListLimit").GetInt32());
        app.Provider.State = new([new("sub_fixture", "canceled", DateTime.UtcNow.AddDays(-1), false, 999)], [], [new("ch_fixture", 999, 999, "aud")]);
        var refund = Event("evt_refund", "charge.refunded"); (await Webhook(a, refund, Sign(refund))).EnsureSuccessStatusCode(); await Process(app, "evt_refund");
        current = await a.GetFromJsonAsync<JsonElement>("/api/billing"); Assert.Equal(999, current.GetProperty("payments")[0].GetProperty("refundedCents").GetInt64());
        Assert.Equal(5, current.GetProperty("entitlement").GetProperty("activeListLimit").GetInt32());
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>(); var account = await db.UserAccounts.SingleAsync(x => x.Id == owner.Account.Id);
            account.IsPaid = false; await db.SaveChangesAsync();
        }
        current = await a.GetFromJsonAsync<JsonElement>("/api/billing"); Assert.Equal(2, current.GetProperty("entitlement").GetProperty("activeListLimit").GetInt32());
        app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.AccountIds = [owner.Account.Id];
        var report = await a.GetFromJsonAsync<JsonElement>("/api/admin/billing");
        Assert.Equal(0, report.GetProperty("estimatedTestMrrAudCents").GetInt64()); Assert.Equal(1, report.GetProperty("canceledSubscriptions").GetInt32());
        Assert.Equal(0, report.GetProperty("pending").GetInt32());
    }
    [PostgreSqlFact]
    public async Task Provider_timeout_is_retryable_but_real_cancellation_propagates()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new Factory();
        using var client = app.Client(); var owner = await Register(client); var now = DateTime.UtcNow;
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
            db.BillingCustomers.Add(new() { UserAccountId = owner.Account.Id, CustomerId = "cus_fixture" });
            db.BillingWebhookEvents.Add(new() { Id = "evt_timeout", CustomerId = "cus_fixture", Type = "invoice.paid", ReceivedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync();
        }
        app.Provider.Timeout = true; await Process(app, "evt_timeout");
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>(); var receipt = await db.BillingWebhookEvents.SingleAsync();
            Assert.Equal("pending", receipt.Status); Assert.Equal(1, receipt.Attempts); Assert.True(receipt.NextAttemptAt > now.AddMinutes(1));
            Assert.Null((await db.BillingCustomers.SingleAsync()).ReconciledAt);
            receipt.NextAttemptAt = now.AddMinutes(-1); await db.SaveChangesAsync();
        }
        app.Provider.Timeout = false;
        using (var cancellation = new CancellationTokenSource()) {
            app.Provider.BeforeRead = ct => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); };
            await using var scope = app.Services.CreateAsyncScope();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<TestBillingService>().ProcessAsync("evt_timeout", cancellation.Token));
        }
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>(); var receipt = await db.BillingWebhookEvents.SingleAsync();
            Assert.Equal("pending", receipt.Status); Assert.Equal(1, receipt.Attempts); Assert.Null((await db.BillingCustomers.SingleAsync()).ReconciledAt);
        }
        app.Provider.BeforeRead = null; await Process(app, "evt_timeout");
        using (var scope = app.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>(); var receipt = await db.BillingWebhookEvents.SingleAsync();
            Assert.Equal("complete", receipt.Status); Assert.Equal(2, receipt.Attempts); Assert.NotNull((await db.BillingCustomers.SingleAsync()).ReconciledAt);
        }
    }
    [PostgreSqlFact]
    public async Task Disabled_and_unconfigured_billing_fail_closed_without_creating_records()
    {
        await using var isolated = await IsolatedDatabaseScope.CreateAsync(); await using var app = new Factory(false);
        using var a = app.Client(); var owner = await Register(a);
        var own = await a.GetFromJsonAsync<JsonElement>("/api/billing"); Assert.False(own.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await a.PostAsJsonAsync("/api/billing/checkout", new BillingAction(owner.Account.Id, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Webhook(a, "{}", "anything")).StatusCode);
        using var scope = app.Services.CreateScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<MyShoppingListDbContext>().BillingCustomers.ToArrayAsync());
    }
    private static async Task Process(Factory app, string id) { await using var scope = app.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<TestBillingService>().ProcessAsync(id, default); }
    private static string Event(string id, string type) => JsonSerializer.Serialize(new { id, type, livemode = false, data = new { @object = new { customer = "cus_fixture", status = "stale_payload_ignored" } } });
    private static string Sign(string body, DateTimeOffset? at = null) {
        var t = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(); var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes("whsec_fixture"), Encoding.UTF8.GetBytes($"{t}.{body}"));
        return $"t={t},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
    private static Task<HttpResponseMessage> Webhook(HttpClient c, string body, string signature) {
        var message = new HttpRequestMessage(HttpMethod.Post, "/billing/stripe-webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        message.Headers.Add("Stripe-Signature", signature); return c.SendAsync(message);
    }
    private static async Task<CurrentSessionResponse> Register(HttpClient client) {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"billing-{Guid.NewGuid():N}@example.test", "Secure-fixture-42!", "Billing fixture"));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<CurrentSessionResponse>())!;
    }
    private sealed class FakeProvider : ITestBillingProvider
    {
        public int Customers, Checkouts; public string? LastCustomer; public bool Fail, Timeout;
        public Action<CancellationToken>? BeforeRead;
        public BillingSnapshot State = new([], [], []);
        public Task<string> CustomerAsync(long account, CancellationToken ct) { Interlocked.Increment(ref Customers); return Task.FromResult("cus_fixture"); }
        public Task<string> CheckoutAsync(string customer, string plan, Guid request, CancellationToken ct) { Interlocked.Increment(ref Checkouts); return Task.FromResult("https://checkout.stripe.com/c/test_fixture"); }
        public Task<string> PortalAsync(string customer, CancellationToken ct) { LastCustomer = customer; return Task.FromResult("https://billing.stripe.com/p/session/test_fixture"); }
        public Task<BillingSnapshot> ReadAsync(string customer, CancellationToken ct) {
            BeforeRead?.Invoke(ct);
            if (Timeout) throw new TaskCanceledException("Fixture provider timeout");
            return Fail ? throw new HttpRequestException("Fixture outage") : Task.FromResult(State);
        }
    }
    private sealed class Factory(bool enabled = true) : WebApplicationFactory<Program>
    {
        public FakeProvider Provider { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder b) {
            b.UseEnvironment("Development"); b.UseSetting("Billing:Enabled", enabled.ToString()); b.UseSetting("Billing:SecretKey", "sk_test_fixture");
            b.UseSetting("Billing:WebhookSecret", "whsec_fixture"); b.UseSetting("Billing:WebsiteOrigin", "http://localhost:3001");
            b.UseSetting("Billing:MonthlyPriceId", "price_monthly"); b.UseSetting("Billing:AnnualPriceId", "price_annual");
            b.ConfigureServices(s => { s.RemoveAll<IHostedService>(); s.RemoveAll<ITestBillingProvider>(); s.AddSingleton<ITestBillingProvider>(Provider);
                s.RemoveAll<DbContextOptions<MyShoppingListDbContext>>(); s.AddDbContext<MyShoppingListDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("MYSHOPPINGLIST_TEST_CONNECTION")!));
            });
        }
        public HttpClient Client() { var c = CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); c.DefaultRequestHeaders.Add(CookieRequestProtection.HeaderName, "1"); return c; }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); await Serilog.Log.CloseAndFlushAsync(); }
    }
}
