using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class ProductImportOptions
{
    public const string SectionName = "ProductImport";
    public bool Enabled { get; set; } = true;
    [Range(1, 60)] public int PollSeconds { get; set; } = 1;
    [Range(1, 10)] public int MaxAttempts { get; set; } = 3;
    [Range(30, 3600)] public int LeaseSeconds { get; set; } = 600;
    [Range(1, 300)] public int RenewalSeconds { get; set; } = 30;
    [Range(1, 3600)] public int RetryDelaySeconds { get; set; } = 30;
}
