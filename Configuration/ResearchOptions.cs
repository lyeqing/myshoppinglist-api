using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class ResearchOptions
{
    [Range(1, 168)] public int ActivePriceHours { get; set; } = 24;
    [Range(1, 168)] public int RetryHours { get; set; } = 6;
}
