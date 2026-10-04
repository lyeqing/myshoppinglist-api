namespace myshoppinglist_api.Models;

public class UserSession
{
    public long Id { get; set; }
    public long UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public DateTime ExpiresDate { get; set; }
    public string? Platform { get; set; }
    public string? DeviceType { get; set; }
    public string? DeviceModel { get; set; }
    public string? OsVersion { get; set; }
    public string? AppVersion { get; set; }
    public string? UserAgent { get; set; }
    public DateTime? LastSeenDate { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public decimal? LocationAccuracy { get; set; }
    public DateTime? LocationCapturedDate { get; set; }
    public DateTime? RevokedDate { get; set; }
}

