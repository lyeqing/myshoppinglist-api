using System.ComponentModel.DataAnnotations;

namespace myshoppinglist_api.Configuration;

public sealed class EmailDeliveryOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = 587;
    public bool ImplicitTls { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    [Range(1, 30)] public int TimeoutSeconds { get; set; } = 15;
    [Range(1, 5)] public int MaximumAttempts { get; set; } = 3;
    // Only exact loopback IPs in Development, without credentials. Never public SMTP.
    public bool AllowLoopbackWithoutTls { get; set; }
}

public sealed class AccountRecoveryOptions
{
    public bool Enabled { get; set; }
    public string WebsiteOrigin { get; set; } = "";
}
