namespace myshoppinglist_api.Configuration;

public sealed class SocialAuthenticationOptions
{
    public bool Enabled { get; set; }
    public string WebsiteOrigin { get; set; } = "";
    public SocialProviderOptions Google { get; set; } = new();
    public SocialProviderOptions Facebook { get; set; } = new();
    public SocialProviderOptions Microsoft { get; set; } = new();
    public SocialProviderOptions? Provider(string name) => name switch
    { "google" => Google, "facebook" => Facebook, "microsoft" => Microsoft, _ => null };
    public bool Available(string name) => Enabled && Provider(name)?.Enabled == true;
    public static readonly string[] Names = ["google", "facebook", "microsoft"];
}

public sealed class SocialProviderOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
