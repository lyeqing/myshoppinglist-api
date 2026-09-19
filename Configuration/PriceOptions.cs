using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class PriceOptions
{
    public const string SectionName = "Price";
    [Range(1, 8760)] public int HistorySampleHours { get; set; } = 24;
}
