using System.Text.Json.Serialization;
namespace myshoppinglist_api.Contracts;

public sealed record BillingAction([property: JsonRequired] long ExpectedAccountId, Guid RequestId, string Plan = "monthly");
public sealed record TestSubscription(string Id, string Status, DateTime? CurrentPeriodEnd, bool CancelAtPeriodEnd, long MonthlyAudCents);
public sealed record TestInvoice(string Id, string Status, long PaidCents, string Currency, string? ReceiptUrl);
public sealed record TestPayment(string Id, long PaidCents, long RefundedCents, string Currency);
public sealed record BillingSnapshot(TestSubscription[] Subscriptions, TestInvoice[] Invoices, TestPayment[] Payments);
