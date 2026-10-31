namespace myshoppinglist_api.Configuration;

public sealed class ExtensionBridgeOptions
{
    public const string SectionName = "ExtensionBridge";
    public bool Enabled { get; set; }
    public int TaskLifetimeMinutes { get; set; } = 10;
    public int PresenceSeconds { get; set; } = 45;
    public int MaximumPendingTasks { get; set; } = 5;
    public int MaximumDevices { get; set; } = 10;
}
