using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

public sealed class RequestSecurityTests
{
    [Fact]
    public async Task Declared_and_chunked_oversize_bodies_are_rejected_and_endpoint_limits_are_preserved()
    {
        await using var server = await Server(); using var client = server.GetTestClient();
        using var declared = await client.PostAsync("/api/echo", new StringContent(new string('a', 1025)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        using var chunked = await client.PostAsync("/api/echo", new UnknownLengthContent(new string('a', 1025)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        using var exact = await client.PostAsync("/api/echo", new UnknownLengthContent(new string('a', 1024)));
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        using var capture = await client.PostAsync("/api/capture", new UnknownLengthContent(new string('a', 2048)));
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        Assert.Equal(2048, await capture.Content.ReadFromJsonAsync<int>());
        using var oversizedCapture = await client.PostAsync("/api/capture", new UnknownLengthContent(new string('a', 2049)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedCapture.StatusCode);
    }

    [Fact]
    public async Task Api_responses_have_privacy_and_browser_security_headers()
    {
        await using var server = await Server(); using var client = server.GetTestClient();
        using var result = await client.GetAsync("/api/status");
        Assert.Equal("nosniff", result.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", result.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", result.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(result.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Global_limits_partition_accounts_independently_and_do_not_limit_health_probes()
    {
        await using var server = await Server(2); using var a = server.GetTestClient(); using var b = server.GetTestClient();
        a.DefaultRequestHeaders.Add("Test-Account", "1"); b.DefaultRequestHeaders.Add("Test-Account", "2");
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/status")).StatusCode);
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_are_limited_without_trusting_forwarded_headers()
    {
        await using var server = await Server(1); using var client = server.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.50");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task Ingress_limit_applies_before_rejected_authorization_can_skip_the_account_limiter()
    {
        await using var server = await Server(ingressLimit: 1); using var client = server.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/blocked")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.51");
        using var blocked = await client.GetAsync("/api/blocked");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);
    }

    private static async Task<WebApplication> Server(int limit = 300, int ingressLimit = 1200)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders().AddConsole();
        builder.WebHost.UseTestServer();
        var services = builder.Services;
        {
            services.AddRouting(); services.AddRateLimiter(o => o.RejectionStatusCode = 429);
            services.AddRequestSecurity();
            services.PostConfigure<RequestSecurityOptions>(o => { o.DefaultBodyBytes = 1024; o.RequestsPerMinute = limit; o.IngressRequestsPerMinute = ingressLimit; });
        }
        var app = builder.Build();
        {
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (BadHttpRequestException error) { context.Response.StatusCode = error.StatusCode; }
            });
            app.UseRouting(); app.UseMiddleware<RequestSecurityMiddleware>();
            app.UseMiddleware<IngressRequestProtection>();
            // This identity source exists only inside this isolated test host.
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/api/blocked") { context.Response.StatusCode = 401; return; }
                if (context.Request.Headers.TryGetValue("Test-Account", out var account))
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.ToString())], "test"));
                await next(context);
            });
            app.UseRateLimiter();
            {
                async Task Echo(HttpContext c)
                {
                    using var reader = new StreamReader(c.Request.Body);
                    var text = await reader.ReadToEndAsync(c.RequestAborted);
                    await c.Response.WriteAsJsonAsync(text.Length);
                }
                app.MapPost("/api/echo", Echo);
                app.MapPost("/api/capture", Echo).WithMetadata(new RequestSizeLimitAttribute(2048));
                app.MapGet("/api/status", () => "ok"); app.MapGet("/health/live", () => "ok");
            }
        }
        await app.StartAsync();
        return app;
    }

    private sealed class UnknownLengthContent(string text) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
    }
}
