using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class AccountSecurityEndpoints
{
    public static void MapAccountSecurityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");
        group.MapGet("/recovery", (IOptions<AccountRecoveryOptions> options) => Results.Ok(new { enabled = options.Value.Enabled }));
        group.MapPost("/recovery/request", async (RecoveryRequest request, EmailOutboxService service, CancellationToken ct) =>
        {
            try
            {
                await service.RequestRecoveryAsync(request.Email, ct);
                return Results.Json(new { message = "If an active account uses that email, you will receive a reset link. Check your inbox and spam folder." }, statusCode: 202);
            }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        }).RequireRateLimiting(EmailDeliverySetup.RecoveryRatePolicy);
        group.MapPost("/recovery/complete", async (ResetPasswordRequest request, AccountCredentialService service, HttpContext c, IWebHostEnvironment env, CancellationToken ct) =>
        {
            try
            {
                var account = await service.ResetAsync(request, ct);
                if (c.User.FindFirstValue(ClaimTypes.NameIdentifier) == account.ToString(CultureInfo.InvariantCulture))
                    SessionCookie.Delete(c, !env.IsDevelopment() || c.Request.IsHttps);
                return Results.NoContent();
            }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        }).RequireRateLimiting(EmailDeliverySetup.RecoveryRatePolicy);
        group.MapPost("/password", async (ChangePasswordRequest request, AccountCredentialService service, HttpContext c, IWebHostEnvironment env, CancellationToken ct) =>
        {
            try
            {
                await service.ChangeAsync(long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture),
                    long.Parse(c.User.FindFirstValue(SessionTokenAuthenticationHandler.SessionIdClaim)!, CultureInfo.InvariantCulture), request, ct);
                SessionCookie.Delete(c, !env.IsDevelopment() || c.Request.IsHttps);
                return Results.NoContent();
            }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        }).RequireAuthorization().RequireRateLimiting(EmailDeliverySetup.RecoveryRatePolicy);
    }
}
