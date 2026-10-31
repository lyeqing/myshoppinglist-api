using myshoppinglist_api.Configuration;
using myshoppinglist_api.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace myshoppinglist_api.Tests;

public sealed class AiIntegrationConfigurationTests
{
    [Fact]
    public void Explicit_scope_configuration_replaces_defaults_without_appending_duplicate_permissions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["AiIntegration:AllowedScopes:0"] = "lists.write" }).Build();
        var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        host.Services.AddAiIntegration(configuration, host.Environment);
        using var services = host.Services.BuildServiceProvider();
        Assert.Equal(new[] { "lists.write" }, services.GetRequiredService<IOptions<AiIntegrationOptions>>().Value.AllowedScopes);
    }

    [Fact]
    public void An_explicitly_empty_scope_section_does_not_silently_restore_default_permissions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["AiIntegration:AllowedScopes"] = null }).Build();
        var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        host.Services.AddAiIntegration(configuration, host.Environment);
        using var services = host.Services.BuildServiceProvider();
        Assert.Empty(services.GetRequiredService<IOptions<AiIntegrationOptions>>().Value.AllowedScopes);
    }
    private static AiIntegrationOptions Local() => new() { Enabled = true, Issuer = "http://localhost:5392/",
        Resource = "http://localhost:5392/mcp", FrontendOrigin = "http://localhost:3001", DevelopmentEphemeralKeys = true,
        Clients = [new() { ClientId = "local-client", DisplayName = "Local client", RedirectUris = ["http://localhost:3001/callback"] }] };

    [Fact]
    public void Explicit_loopback_development_configuration_is_supported() => AiIntegrationSetup.Validate(Local(), true);

    [Theory]
    [InlineData("issuer")][InlineData("resource")][InlineData("frontend")][InlineData("callback")]
    [InlineData("keys")][InlineData("scopes")][InlineData("duplicate-scopes")][InlineData("clients")]
    [InlineData("lifetime")][InlineData("refresh")][InlineData("quota")][InlineData("browser-origin")]
    public void Unsafe_or_ambiguous_configuration_fails_closed(string invalid)
    {
        var value = Local();
        switch (invalid)
        {
            case "issuer": value.Issuer = "http://untrusted.example/"; break;
            case "resource": value.Resource = "http://localhost:5392/other-resource"; break;
            case "frontend": value.FrontendOrigin = "https://trusted.example/redirect?to=other"; break;
            case "callback": value.Clients[0].RedirectUris = ["javascript:alert(1)"]; break;
            case "keys": value.DevelopmentEphemeralKeys = false; break;
            case "scopes": value.AllowedScopes = ["everything"]; break;
            case "duplicate-scopes": value.AllowedScopes = ["lists.read", "lists.read"]; break;
            case "clients": value.Clients = [value.Clients[0], value.Clients[0]]; break;
            case "lifetime": value.AccessTokenMinutes = 1440; break;
            case "refresh": value.RefreshTokenDays = 365; break;
            case "quota": value.RequestsPerMinute = 0; break;
            case "browser-origin": value.AllowedBrowserOrigins = ["https://trusted.example/path"]; break;
        }
        Assert.Throws<InvalidOperationException>(() => AiIntegrationSetup.Validate(value, true));
    }

    [Fact]
    public void Production_rejects_development_keys_even_with_https_urls()
    {
        var value = Local(); value.Issuer = "https://api.example/"; value.Resource = "https://api.example/mcp";
        value.FrontendOrigin = "https://shop.example"; value.Clients[0].RedirectUris = ["https://client.example/callback"];
        Assert.Throws<InvalidOperationException>(() => AiIntegrationSetup.Validate(value, false));
    }
}
