using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class PriceRefreshOptions
{
    public const string SectionName = "PriceRefresh";
    public bool Enabled { get; set; } = true;
    [Range(1, 1440)] public int ScanMinutes { get; set; } = 15;
    [Range(1, 168)] public int RetryCooldownHours { get; set; } = 6;
}
