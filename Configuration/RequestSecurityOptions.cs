using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class RequestSecurityOptions
{
    public const string SectionName = "RequestSecurity";
    [Range(1, 10000)] public int RequestsPerMinute { get; set; } = 300;
    [Range(1, 60000)] public int IngressRequestsPerMinute { get; set; } = 1200;
    [Range(1024, 65536)] public int DefaultBodyBytes { get; set; } = 16384;
}
