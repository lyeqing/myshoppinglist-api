using System.Diagnostics;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;

namespace myshoppinglist_api.Security;

public static class RequestSecurity
{
    public static void AddRequestSecurity(this IServiceCollection services)
    {
        services.AddOptions<RequestSecurityOptions>().BindConfiguration(RequestSecurityOptions.SectionName)
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IngressRequestProtection>();
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RequestSecurityOptions>>((limits, settings) =>
            limits.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/mcp")
                    ? RateLimitPartition.GetNoLimiter("other")
                    : RateLimitPartition.GetFixedWindowLimiter(
                        context.User.Identity?.IsAuthenticated == true
                            ? "account:" + context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                            : "ip:" + context.Connection.RemoteIpAddress?.MapToIPv6(),
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.Value.RequestsPerMinute,
                            Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
    }

    public static string Reference(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;
}

// Runs before authentication/database lookups and authorization short-circuits.
// Uses the network peer only: forwarded headers need a separately trusted ingress configuration.
public sealed class IngressRequestProtection(IOptions<RequestSecurityOptions> options) : IMiddleware, IAsyncDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = options.Value.IngressRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        using var lease = await limiter.AcquireAsync(context, 1, context.RequestAborted);
        if (!lease.IsAcquired)
        {
            context.Response.Headers.RetryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                ? Math.Ceiling(retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : "60";
            await Results.Problem(statusCode: 429, title: "Too many requests. Please try again later.").ExecuteAsync(context);
            return;
        }
        await next(context);
    }

    public ValueTask DisposeAsync() => limiter.DisposeAsync();
}

// Enforce limits before JSON/form binding, including bodies without Content-Length.
// Endpoint metadata preserves the explicitly larger capture/screenshot contracts.
public sealed class RequestSecurityMiddleware(RequestDelegate next, IOptions<RequestSecurityOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XFrameOptions = "DENY";
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/mcp")
            || context.Request.Path.StartsWithSegments("/connect"))
            context.Response.Headers.CacheControl = "no-store";
        var limit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize
            ?? (context.Request.Path.StartsWithSegments("/mcp") ? 65536 : options.Value.DefaultBodyBytes);
        var original = context.Request.Body;
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
        if (context.Request.ContentLength > limit)
        {
            await Results.Problem(statusCode: 413, title: "The request is too large.").ExecuteAsync(context);
            return;
        }
        context.Request.Body = new LimitedRequestStream(original, limit);
        try { await next(context); }
        finally { context.Request.Body = original; }
    }
}

internal sealed class LimitedRequestStream(Stream source, long maximum) : Stream
{
    private long consumed;
    private int Count(int count)
    {
        consumed += count;
        if (consumed > maximum) throw new BadHttpRequestException("The request is too large.", 413);
        return count;
    }
    private int Allow(int requested) => (int)Math.Min(requested, maximum - consumed + 1);
    public override int Read(byte[] buffer, int offset, int count) => Count(source.Read(buffer, offset, Allow(count)));
    public override int Read(Span<byte> buffer) => Count(source.Read(buffer[..Allow(buffer.Length)]));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await source.ReadAsync(buffer[..Allow(buffer.Length)], cancellationToken));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
