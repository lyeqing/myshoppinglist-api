using System.Threading.RateLimiting;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Mcp;

// Runs before the OAuth authentication handler, which can finish protocol requests itself.
public sealed class AiProtocolProtection(AiIntegrationOptions settings, Microsoft.Extensions.Options.IOptions<ExtensionBridgeOptions> bridge) : IMiddleware, IAsyncDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> limiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.RequestsPerMinute,
                Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));

    public async Task InvokeAsync(HttpContext c, RequestDelegate next)
    {
        var consent = c.Request.Path.StartsWithSegments("/api/ai/oauth");
        var device = bridge.Value.Enabled && (c.Request.Path.StartsWithSegments("/api/extension-device") || c.Request.Path.StartsWithSegments("/api/user-extension/devices") || c.Request.Path.StartsWithSegments("/api/account/extension-devices"));
        if (!consent && !device && !c.Request.Path.StartsWithSegments("/connect") && !c.Request.Path.StartsWithSegments("/mcp"))
        { await next(c); return; }
        c.Response.Headers.CacheControl = "no-store";
        if (c.Request.Path.StartsWithSegments("/mcp"))
        {
            var origins = settings.AllowedBrowserOrigins.Append(settings.FrontendOrigin).Append(settings.Issuer)
                .Select(value => new Uri(value).GetLeftPart(UriPartial.Authority));
            if (!string.Equals(c.Request.Host.Value, new Uri(settings.Issuer).Authority, StringComparison.OrdinalIgnoreCase)
                || c.Request.Headers.Origin.Count > 1 || c.Request.Headers.Origin.Count == 1
                && !origins.Contains(c.Request.Headers.Origin.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                await Results.Problem(statusCode: 403, title: "The request origin is not permitted.").ExecuteAsync(c);
                return;
            }
        }
        // Reject forged browser consent before OpenIddict restores any cached request.
        if (consent && (!HttpMethods.IsPost(c.Request.Method) || c.Request.Headers[CookieRequestProtection.HeaderName] != "1"
            || c.Request.Headers["Sec-Fetch-Site"] == "cross-site" || c.Request.Headers.Origin.Count > 1
            || c.Request.Headers.Origin.Count == 1 && c.Request.Headers.Origin != $"{c.Request.Scheme}://{c.Request.Host}"))
        {
            await Results.Problem(statusCode: 403, title: "The request failed browser request protection.").ExecuteAsync(c);
            return;
        }
        using var lease = await limiter.AcquireAsync(c, 1, c.RequestAborted);
        if (!lease.IsAcquired)
        {
            c.Response.Headers.RetryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                ? Math.Ceiling(retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : "60";
            await Results.Problem(statusCode: 429, title: "Too many requests. Please try again later.").ExecuteAsync(c);
            return;
        }
        await next(c);
    }

    public ValueTask DisposeAsync() => limiter.DisposeAsync();
}
