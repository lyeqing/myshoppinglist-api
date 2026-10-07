namespace myshoppinglist_api.Models;

public sealed class RetailerWorkloadState
{
    public string Retailer { get; set; } = "";
    public DateTime[] BlockedAt { get; set; } = [];
    public DateTime? PausedUntil { get; set; }
    public Guid? ProbeToken { get; set; }
    public DateTime? ProbeExpiresAt { get; set; }
}
