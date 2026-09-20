using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class RetailerSearchOptions
{
    public const string SectionName = "RetailerSearch";
    public bool Enabled { get; set; } = true;
    // Null uses Playwright's installed Chromium. "chrome" uses an installed Chrome.
    public string? BrowserChannel { get; set; }
    [Range(5, 90)] public int TimeoutSeconds { get; set; } = 45;
    [Range(1, 4)] public int MaximumConcurrentBrowsers { get; set; } = 2;
    [Range(1, 8)] public int MaximumCandidates { get; set; } = 5;
    [Range(10, 300)] public int MaximumRequests { get; set; } = 180;
    [Range(1_048_576, 104_857_600)] public int MaximumTransferBytes { get; set; } = 40 * 1024 * 1024;
}
