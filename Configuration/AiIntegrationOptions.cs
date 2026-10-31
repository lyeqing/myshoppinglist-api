namespace myshoppinglist_api.Configuration;

public sealed class AiIntegrationOptions
{
    public const string SectionName = "AiIntegration";
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "";
    public string Resource { get; set; } = "";
    public string FrontendOrigin { get; set; } = "";
    public string[] AllowedBrowserOrigins { get; set; } = [];
    public bool DevelopmentEphemeralKeys { get; set; }
    public string SigningCertificatePath { get; set; } = "";
    public string EncryptionCertificatePath { get; set; } = "";
    public string? SigningCertificatePassword { get; set; }
    public string? EncryptionCertificatePassword { get; set; }
    public int AccessTokenMinutes { get; set; } = 5;
    public int RefreshTokenDays { get; set; } = 30;
    public int RequestsPerMinute { get; set; } = 120;
    public string[] AllowedScopes { get; set; } = ["lists.read", "products.read"];
    public AiClientOptions[] Clients { get; set; } = [];
}

public sealed class AiClientOptions
{
    public string ClientId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string[] RedirectUris { get; set; } = [];
}
