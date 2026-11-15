using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace myshoppinglist_api.Mcp;

public static class AiIntegrationSetup
{
    public const string AccessPolicy = "McpAccess";
    public static void AddAiIntegration(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection(AiIntegrationOptions.SectionName).Get<AiIntegrationOptions>() ?? new();
        var scopes = configuration.GetSection(AiIntegrationOptions.SectionName + ":AllowedScopes");
        var hasConfiguredScopes = configuration.GetSection(AiIntegrationOptions.SectionName).GetChildren()
            .Any(c => c.Key == nameof(AiIntegrationOptions.AllowedScopes));
        // ConfigurationBinder appends to initialized arrays; an explicit scope list must replace defaults.
        if (hasConfiguredScopes) settings.AllowedScopes = scopes.Get<string[]>() ?? [];
        services.Configure<AiIntegrationOptions>(configuration.GetSection(AiIntegrationOptions.SectionName));
        services.PostConfigure<AiIntegrationOptions>(o => { if (hasConfiguredScopes) o.AllowedScopes = scopes.Get<string[]>() ?? []; });
        if (!settings.Enabled) return;
        Validate(settings, environment.IsDevelopment());
        services.AddSingleton(settings);
        services.AddScoped<AiConnectionService>();
        services.AddScoped<ShoppingListReadTools>();
        services.AddScoped<AccountReadTools>();
        services.AddScoped<ResearchReadTools>();
        services.AddScoped<SavedPriceReader>();
        services.AddHostedService<AiClientRegistration>();
        services.AddSingleton<AiProtocolProtection>();
        services.AddOpenIddict()
            .AddCore(o => { o.UseEntityFrameworkCore().UseDbContext<MyShoppingListDbContext>(); o.DisableEntityCaching(); })
            .AddServer(o =>
            {
                o.SetIssuer(settings.Issuer);
                o.SetAuthorizationEndpointUris("/connect/authorize", "/api/ai/oauth/preview", "/api/ai/oauth/authorize");
                o.SetTokenEndpointUris("/connect/token");
                o.SetRevocationEndpointUris("/connect/revoke");
                o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
                // Public PKCE clients authenticate without a shared client secret; advertise that capability.
                o.Configure(options => options.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.None));
                o.Configure(options => { options.CodeChallengeMethods.Clear(); options.CodeChallengeMethods.Add(CodeChallengeMethods.Sha256); });
                o.RegisterScopes(settings.AllowedScopes.Concat([Scopes.OfflineAccess]).ToArray());
                o.RegisterResources(settings.Resource);
                o.UseReferenceAccessTokens().UseReferenceRefreshTokens();
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(settings.AccessTokenMinutes));
                o.SetRefreshTokenLifetime(TimeSpan.FromDays(settings.RefreshTokenDays));
                o.Configure(options => options.RequestTokenLifetime = TimeSpan.FromMinutes(5));
                o.EnableAuthorizationRequestCaching();
                if (environment.IsDevelopment() && settings.DevelopmentEphemeralKeys)
                    o.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                else
                {
                    o.AddSigningCertificate(X509CertificateLoader.LoadPkcs12FromFile(settings.SigningCertificatePath, settings.SigningCertificatePassword));
                    o.AddEncryptionCertificate(X509CertificateLoader.LoadPkcs12FromFile(settings.EncryptionCertificatePath, settings.EncryptionCertificatePassword));
                }
                var host = o.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
                if (environment.IsDevelopment() && new Uri(settings.Issuer).IsLoopback) host.DisableTransportSecurityRequirement();
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.AddAudiences(settings.Resource);
                o.EnableTokenEntryValidation().EnableAuthorizationEntryValidation();
                o.UseAspNetCore();
            });
        services.AddAuthentication().AddMcp(o =>
        {
            o.ForwardAuthenticate = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            o.ResourceMetadataUri = new Uri(new Uri(settings.Issuer), "/.well-known/oauth-protected-resource/mcp");
            o.ResourceMetadata = new ProtectedResourceMetadata
            {
                Resource = settings.Resource, AuthorizationServers = [settings.Issuer],
                ScopesSupported = settings.AllowedScopes
            };
        });
        services.AddAuthorization(o => o.AddPolicy(AccessPolicy, p => p
            .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
        var server = services.AddMcpServer(o => o.ServerInstructions = Services.ResearchWorkflow.ServerInstructions)
            .WithHttpTransport(o => o.Stateless = true).WithTools<ShoppingListReadTools>().WithTools<AccountReadTools>()
            .WithTools<ResearchReadTools>().WithPrompts<ResearchPrompts>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(McpToolAudit.Wrap));
        if (settings.AllowedScopes.Contains(AiScopes.ResearchWrite, StringComparer.Ordinal))
        {
            services.AddScoped<ResearchWriteTools>();
            server.WithTools<ResearchWriteTools>();
        }
        if (settings.AllowedScopes.Contains(AiScopes.ListsWrite, StringComparer.Ordinal))
        {
            services.AddScoped<McpMutationCoordinator>();
            services.AddScoped<ShoppingListWriteTools>();
            server.WithTools<ShoppingListWriteTools>();
        }
        if (settings.AllowedScopes.Contains(AiScopes.ExtensionCapture, StringComparer.Ordinal))
        {
            if (!configuration.GetValue<bool>("ExtensionBridge:Enabled")) throw new InvalidOperationException("Enable the personal extension bridge before offering extension.capture.");
            services.AddScoped<ExtensionBridgeTools>();
            server.WithTools<ExtensionBridgeTools>();
        }
        services.AddRateLimiter(o => o.AddPolicy("AiProtocol", c => RateLimitPartition.GetFixedWindowLimiter(
            c.User.GetClaim(Claims.Subject) ?? "unknown", _ => new FixedWindowRateLimiterOptions
            { PermitLimit = settings.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
    }

    public static void Validate(AiIntegrationOptions o, bool development)
    {
        bool Origin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && u.UserInfo.Length == 0
            && u.AbsolutePath == "/" && u.Query.Length == 0 && u.Fragment.Length == 0
            && (u.Scheme == "https" || development && u.Scheme == "http" && u.IsLoopback);
        if (!Origin(o.Issuer) || !Origin(o.FrontendOrigin) || o.AllowedBrowserOrigins.Any(value => !Origin(value))
            || o.Resource != new Uri(o.Issuer).GetLeftPart(UriPartial.Authority) + "/mcp")
            throw new InvalidOperationException("AI integration requires explicit trusted issuer, frontend origin and matching MCP resource.");
        if (o.AccessTokenMinutes is < 1 or > 15 || o.RefreshTokenDays is < 1 or > 90 || o.RequestsPerMinute is < 1 or > 10000
            || o.AllowedScopes.Length == 0 || o.AllowedScopes.Distinct(StringComparer.Ordinal).Count() != o.AllowedScopes.Length
            || o.AllowedScopes.Any(s => !AiScopes.All.Contains(s, StringComparer.Ordinal)))
            throw new InvalidOperationException("Invalid AI lifetimes, scopes or rate limit.");
        if (!development && o.DevelopmentEphemeralKeys || !o.DevelopmentEphemeralKeys
            && (!File.Exists(o.SigningCertificatePath) || !File.Exists(o.EncryptionCertificatePath)))
            throw new InvalidOperationException("AI integration requires configured signing/encryption certificates outside explicit development-key mode.");
        if (o.Clients.Select(c => c.ClientId).Distinct(StringComparer.Ordinal).Count() != o.Clients.Length)
            throw new InvalidOperationException("AI client IDs must be unique.");
        foreach (var c in o.Clients)
            if (string.IsNullOrWhiteSpace(c.ClientId) || c.ClientId.Length > 100 || string.IsNullOrWhiteSpace(c.DisplayName)
                || c.DisplayName.Length > 200 || c.RedirectUris.Length == 0 || c.RedirectUris.Any(s =>
                    !Uri.TryCreate(s, UriKind.Absolute, out var u) || u.UserInfo.Length != 0 || u.Fragment.Length != 0
                    || !(u.Scheme == "https" || development && u.Scheme == "http" && u.IsLoopback)))
                throw new InvalidOperationException("AI clients require explicit names and exact HTTPS callback URLs (loopback HTTP is development-only).");
    }
}
