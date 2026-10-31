using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class ExtensionBridgeEndpoints
{
    private static long Account(HttpContext c) => long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
    private static Guid Device(HttpContext c) => Guid.Parse(c.User.FindFirstValue(ExtensionDeviceAuthenticationHandler.DeviceIdClaim)!);

    public static void MapExtensionBridgeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/user-extension/bridge-info", (HttpContext c, IOptions<ExtensionBridgeOptions> bridge, IOptions<UserExtensionOptions> extensions, IOptions<OperationsOptions> operations) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            return extensions.Value.Allows(c.Request) ? Results.Ok(new { version = 1, enabled = bridge.Value.Enabled, feedbackEnabled = operations.Value.Enabled }) : Results.Forbid();
        }).RequireAuthorization();
        if (!app.Services.GetRequiredService<IOptions<ExtensionBridgeOptions>>().Value.Enabled) return;
        var registration = app.MapGroup("/api/user-extension/devices").RequireAuthorization();
        registration.AddEndpointFilter(async (invocation, next) =>
        {
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!invocation.HttpContext.RequestServices.GetRequiredService<IOptions<UserExtensionOptions>>().Value.Allows(invocation.HttpContext.Request))
                return Results.Problem(statusCode: 403, title: "This extension is not enabled on the server.");
            try { return await next(invocation); }
            catch (UserImportException error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
        });
        registration.MapPost("", async (ExtensionDeviceRegistration request, HttpContext c, ExtensionDeviceService service, CancellationToken ct) =>
            Results.Ok(await service.RegisterAsync(Account(c), long.Parse(c.User.FindFirstValue(SessionTokenAuthenticationHandler.SessionIdClaim)!, CultureInfo.InvariantCulture),
                c.Request.Headers["X-MyShoppingList-Extension"].ToString(), request, ct))).RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
        var own = app.MapGroup("/api/account/extension-devices").RequireAuthorization();
        own.AddEndpointFilter(async (invocation, next) =>
        {
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(invocation); }
            catch (UserImportException error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
        });
        own.MapGet("", async (HttpContext c, ExtensionDeviceService service, CancellationToken ct) => Results.Ok(await service.DevicesAsync(Account(c), ct)));
        own.MapDelete("/{id:guid}", async (Guid id, HttpContext c, ExtensionDeviceService service, CancellationToken ct) =>
            await service.RevokeAsync(Account(c), id, ct) ? Results.NoContent() : Results.NotFound());
        var device = app.MapGroup("/api/extension-device").RequireAuthorization(ExtensionBridgeSetup.DevicePolicy).RequireRateLimiting(ExtensionBridgeSetup.DeviceRatePolicy);
        device.AddEndpointFilter(async (invocation, next) =>
        {
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(invocation); }
            catch (UserImportException error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
        });
        device.MapPost("/heartbeat", async (ExtensionDeviceHeartbeat request, HttpContext c, ExtensionDeviceService service, CancellationToken ct) =>
        { await service.HeartbeatAsync(Account(c), Device(c), request, ct); return Results.NoContent(); });
        device.MapDelete("/self", async (HttpContext c, ExtensionDeviceService service, CancellationToken ct) =>
            await service.RevokeAsync(Account(c), Device(c), ct) ? Results.NoContent() : Results.NotFound());
        device.MapGet("/tasks", async (HttpContext c, ExtensionTaskService service, CancellationToken ct) => Results.Ok(await service.PendingAsync(Account(c), Device(c), ct)));
        device.MapGet("/tasks/{id:guid}", async (Guid id, HttpContext c, ExtensionTaskService service, CancellationToken ct) =>
            await service.DeviceTaskAsync(Account(c), Device(c), id, ct) is { } task ? Results.Ok(task) : Results.NotFound());
        device.MapPost("/tasks/{id:guid}/decision", async (Guid id, ExtensionTaskDecision request, HttpContext c, ExtensionTaskService service, CancellationToken ct) =>
            Results.Ok(await service.DecideAsync(Account(c), Device(c), id, request.Approve, ct)));
        device.MapPost("/tasks/{id:guid}/start", async (Guid id, ExtensionTaskClaimRequest request, HttpContext c, ExtensionTaskService service, CancellationToken ct) =>
            Results.Ok(await service.StartAsync(Account(c), Device(c), id, request.ClaimToken, ct)));
        device.MapPost("/tasks/{id:guid}/preview", async (Guid id, ExtensionTaskCapture request, HttpContext c, ExtensionTaskService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(Account(c), Device(c), id, request, ct))).WithMetadata(new RequestSizeLimitAttribute(2500000));
        device.MapPost("/tasks/{id:guid}/capture", async (Guid id, ExtensionTaskCapture request, HttpContext c, ExtensionTaskService service, CancellationToken ct) =>
            Results.Ok(await service.CompleteCaptureAsync(Account(c), Device(c), id, request, ct))).WithMetadata(new RequestSizeLimitAttribute(2500000));
        device.MapPost("/tasks/{id:guid}/search", async (Guid id, ExtensionTaskSearch request, HttpContext c, ExtensionTaskService service, CancellationToken ct) =>
            Results.Ok(await service.CompleteSearchAsync(Account(c), Device(c), id, request, ct))).WithMetadata(new RequestSizeLimitAttribute(20000));
        device.MapPost("/tasks/{id:guid}/fail", async (Guid id, ExtensionTaskFailure request, HttpContext c, ExtensionTaskService service, CancellationToken ct) =>
            Results.Ok(await service.EndAsync(Account(c), Device(c), id, request, false, ct)));
        device.MapPost("/tasks/{id:guid}/cancel", async (Guid id, ExtensionTaskFailure request, HttpContext c, ExtensionTaskService service, CancellationToken ct) =>
            Results.Ok(await service.EndAsync(Account(c), Device(c), id, request, true, ct)));
    }
}
