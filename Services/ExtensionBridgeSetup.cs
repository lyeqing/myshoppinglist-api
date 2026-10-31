using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public static class ExtensionBridgeSetup
{
    public const string DevicePolicy = "ExtensionDeviceOnly";
    public const string DeviceRatePolicy = "ExtensionDeviceRequests";
    public static void AddExtensionBridge(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ExtensionBridgeOptions>(configuration.GetSection(ExtensionBridgeOptions.SectionName));
        var settings = configuration.GetSection(ExtensionBridgeOptions.SectionName).Get<ExtensionBridgeOptions>() ?? new();
        if (!settings.Enabled) return;
        if (!configuration.GetValue<bool>("AiIntegration:Enabled") || !configuration.GetValue<bool>("UserExtension:PersonalCapturesEnabled")
            || settings.TaskLifetimeMinutes is < 1 or > 30 || settings.PresenceSeconds is < 10 or > 120
            || settings.MaximumPendingTasks is < 1 or > 20 || settings.MaximumDevices is < 1 or > 20)
            throw new InvalidOperationException("The personal extension bridge requires AI integration, personal captures and bounded task/device settings.");
        services.AddScoped<ExtensionDeviceService>(); services.AddScoped<ExtensionTaskService>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ExtensionDeviceAuthenticationHandler>(ExtensionDeviceAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(o => o.AddPolicy(DevicePolicy, p => p.AddAuthenticationSchemes(ExtensionDeviceAuthenticationHandler.SchemeName).RequireAuthenticatedUser()));
        services.AddRateLimiter(o => o.AddPolicy(DeviceRatePolicy, c => RateLimitPartition.GetFixedWindowLimiter(
            c.User.FindFirst(ExtensionDeviceAuthenticationHandler.DeviceIdClaim)?.Value ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 90, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
    }
}
