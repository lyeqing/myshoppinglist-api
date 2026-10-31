namespace myshoppinglist_api.Configuration;

// This release deliberately cannot activate production billing or paid entitlements.
public sealed class BillingOptions
{
    public bool Enabled { get; set; }
    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string MonthlyPriceId { get; set; } = "";
    public string AnnualPriceId { get; set; } = "";
    public string WebsiteOrigin { get; set; } = "";
}
