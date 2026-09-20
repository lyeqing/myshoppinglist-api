using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    [Range(1, 90)] public int RegisteredSessionDays { get; set; } = 30;
    [Range(15, 128)] public int MinimumPasswordLength { get; set; } = 15;
    [Range(1, 100)] public int RegistrationRequestsPerWindow { get; set; } = 5;
    [Range(1, 86400)] public int RegistrationWindowSeconds { get; set; } = 3600;
    [Range(1, 100)] public int SignInRequestsPerWindow { get; set; } = 20;
    [Range(1, 86400)] public int SignInWindowSeconds { get; set; } = 900;
    [Range(1, 24)] public int TrialLifetimeHours { get; set; } = 3;
    [Range(1, 100)] public int TrialRequestsPerWindow { get; set; } = 10;
    [Range(1, 86400)] public int TrialWindowSeconds { get; set; } = 3600;
}
