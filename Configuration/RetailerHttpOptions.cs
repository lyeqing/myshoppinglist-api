using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class RetailerHttpOptions
{
    public const string SectionName = "RetailerHttp";
    [Range(1, 60)] public int TimeoutSeconds { get; set; } = 20;
    [Range(1024, 10_485_760)] public int MaximumResponseBytes { get; set; } = 2_097_152;
    [Range(0, 5)] public int MaximumRedirects { get; set; } = 3;
}
