using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    [Range(1, 24)] public int TrialLifetimeHours { get; set; } = 3;
    [Range(1, 100)] public int TrialRequestsPerWindow { get; set; } = 10;
    [Range(1, 86400)] public int TrialWindowSeconds { get; set; } = 3600;
}
