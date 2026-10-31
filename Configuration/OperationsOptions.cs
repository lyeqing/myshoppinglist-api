namespace myshoppinglist_api.Configuration;

public sealed class OperationsOptions
{
    public bool Enabled { get; set; }
    public bool SimulateDelivery { get; set; } = true;
    public string[] IncidentRecipients { get; set; } = [];
    public string Release { get; set; } = "local";
    public int RawRetentionDays { get; set; } = 30;
    public int AggregateRetentionMonths { get; set; } = 12;
}
