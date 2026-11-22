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
        static (long Account, long Session) Identity(HttpContext c) => (
            long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture),
            long.Parse(c.User.FindFirstValue(SessionTokenAuthenticationHandler.SessionIdClaim)!, CultureInfo.InvariantCulture));
        var profile = app.MapGroup("/api/account").RequireAuthorization();
        profile.MapGet("/profile", async (AccountProfileService service, HttpContext c, CancellationToken ct) =>
        {
            try { var id = Identity(c); return Results.Ok(await service.GetAsync(id.Account, id.Session, ct)); }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        });
        profile.MapPut("/profile", async (UpdateProfileRequest request, AccountProfileService service, HttpContext c, CancellationToken ct) =>
        {
            try { var id = Identity(c); return Results.Ok(await service.UpdateAsync(id.Account, id.Session, request, ct)); }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        });
        profile.MapPost("/email/request", async (RequestEmailChange request, AccountProfileService service, HttpContext c, CancellationToken ct) =>
        {
            try { var id = Identity(c); return Results.Json(await service.RequestEmailAsync(id.Account, id.Session, request, ct), statusCode: 202); }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        }).RequireRateLimiting(EmailDeliverySetup.RecoveryRatePolicy);
        profile.MapPost("/email/cancel", async (CancelEmailChange request, AccountProfileService service, HttpContext c, CancellationToken ct) =>
        {
            try { var id = Identity(c); return Results.Ok(await service.CancelAsync(id.Account, id.Session, request, ct)); }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        });
        profile.MapPost("/email/complete", async (CompleteEmailChange request, AccountProfileService service, HttpContext c, IWebHostEnvironment env, CancellationToken ct) =>
        {
            try
            {
                var id = Identity(c); await service.CompleteAsync(id.Account, id.Session, request, ct);
                SessionCookie.Delete(c, !env.IsDevelopment() || c.Request.IsHttps); return Results.NoContent();
            }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        }).RequireRateLimiting(EmailDeliverySetup.RecoveryRatePolicy);
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
