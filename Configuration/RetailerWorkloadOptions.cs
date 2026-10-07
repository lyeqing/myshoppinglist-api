using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class RetailerWorkloadOptions
{
    public const string SectionName = "RetailerWorkload";
    [Range(1, 100)] public int MaxConcurrentTasks { get; set; } = 2;
    [Range(1, 100)] public int BlockedThreshold { get; set; } = 3;
    [Range(1, 1440)] public int BlockedWindowMinutes { get; set; } = 10;
    [Range(1, 1440)] public int PauseMinutes { get; set; } = 15;
}
