using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class SocialAuthenticationEndpoints
{
    private static (long? Account, long? Session) Identity(HttpContext c) =>
        c.User.Identity?.IsAuthenticated == true
            ? (long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture),
                long.Parse(c.User.FindFirstValue(SessionTokenAuthenticationHandler.SessionIdClaim)!, CultureInfo.InvariantCulture)) : (null, null);
    private static bool Expected(HttpContext c, long id) => Identity(c).Account == id;
    private static IResult Problem(UserImportException e) => Results.Problem(statusCode: e.Status, title: e.Message);

    public static void MapSocialAuthenticationEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth/social");
        g.MapGet("/providers", (IOptions<SocialAuthenticationOptions> o) => Results.Ok(SocialAuthenticationOptions.Names
            .Select(p => new { provider = p, available = o.Value.Available(p) })));
        g.MapGet("/methods", async (HttpContext c, SocialAuthenticationService s, CancellationToken ct) =>
        {
            try { var (account, session) = Identity(c); return Results.Ok(await s.Methods(account!.Value, session!.Value, ct)); }
            catch (UserImportException e) { return Problem(e); }
        }).RequireAuthorization();
        g.MapPost("/start", async (SocialStartRequest request, HttpContext c, SocialAuthenticationService s,
            IOptions<SocialAuthenticationOptions> settings, CancellationToken ct) =>
        {
            try
            {
                var (account, session) = Identity(c);
                if (account is null && (c.Request.Cookies.ContainsKey(SessionCookie.Name) || c.Request.Headers.ContainsKey("Authorization")))
                    return Results.Problem(statusCode: 401, title: "The session is unavailable. Clear the expired session before starting a new sign-in.");
                var flow = await s.Start(request, account, session, ct);
                await c.SignOutAsync(SocialAuthenticationSetup.TicketScheme);
                var properties = new AuthenticationProperties { RedirectUri = settings.Value.WebsiteOrigin.TrimEnd('/') + "/account/social" };
                properties.Items["flow"] = flow.ToString(); properties.Items["provider"] = request.Provider;
                return Results.Challenge(properties, [SocialAuthenticationSetup.Scheme(request.Provider)]);
            }
            catch (UserImportException e) { return Problem(e); }
        }).RequireRateLimiting(AuthEndpoints.SignInRatePolicy);
        g.MapPost("/complete", async (SocialCompleteRequest request, HttpContext c, SocialAuthenticationService s,
            IOptions<SocialAuthenticationOptions> settings, CancellationToken ct) =>
        {
            if (!settings.Value.Enabled) return Results.Problem(statusCode: 503, title: "Social sign-in is not available.");
            var ticket = await c.AuthenticateAsync(SocialAuthenticationSetup.TicketScheme);
            if (!ticket.Succeeded || ticket.Properties is null || !ticket.Properties.Items.TryGetValue("flow", out var raw) || !Guid.TryParse(raw, out var id)
                || !ticket.Properties.Items.TryGetValue("provider", out var provider) || provider is null)
                return Results.Problem(statusCode: 400, title: "This sign-in could not be completed. Start again in the same browser.");
            try
            {
                var (account, session) = Identity(c);
                var result = await s.Complete(id, new(provider, ticket.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!,
                    ticket.Principal.FindFirstValue(ClaimTypes.Email), ticket.Principal.FindFirstValue(ClaimTypes.Name)), account, session, request.LeaveTrialSeparate, ct, c.Request.Headers["X-Client-Timezone"].ToString());
                if (result.Outcome != "confirm_trial_switch") await c.SignOutAsync(SocialAuthenticationSetup.TicketScheme);
                if (result.Authentication is { Response: { } response } auth)
                {
                    c.Items[AuthEndpoints.ResultAccountItem] = response.Account.Id;
                    SessionCookie.Write(c, auth.Token!, response.SessionExpiresDate, c.Request.IsHttps);
                }
                return Results.Ok(new SocialCompletion(result.Outcome, result.Authentication?.Response));
            }
            catch (UserImportException e) { await c.SignOutAsync(SocialAuthenticationSetup.TicketScheme); return Problem(e); }
        }).RequireRateLimiting(AuthEndpoints.SignInRatePolicy);
        g.MapPost("/reauthenticate", async (SocialPasswordProof request, HttpContext c, SocialAuthenticationService s, CancellationToken ct) =>
        {
            try
            {
                var (a, session) = Identity(c);
                if (!Expected(c, request.ExpectedAccountId)) return Results.Problem(statusCode: 409, title: "Your account changed. Review the request again.");
                await s.Reauthenticate(a!.Value, session!.Value, request.Password, ct); return Results.NoContent();
            }
            catch (UserImportException e) { return Problem(e); }
        }).RequireAuthorization().RequireRateLimiting(AuthEndpoints.SignInRatePolicy);
        g.MapPost("/disconnect", async (SocialDisconnectRequest request, HttpContext c, SocialAuthenticationService s, CancellationToken ct) =>
        {
            try
            {
                var (a, session) = Identity(c);
                if (!Expected(c, request.ExpectedAccountId)) return Results.Problem(statusCode: 409, title: "Your account changed. Review the request again.");
                await s.Disconnect(a!.Value, session!.Value, request.Provider, ct);
                SessionCookie.Delete(c, c.Request.IsHttps); return Results.NoContent();
            }
            catch (UserImportException e) { return Problem(e); }
        }).RequireAuthorization().RequireRateLimiting(AuthEndpoints.SignInRatePolicy);
        g.MapPost("/password/start", async (SocialEmailRequest request, HttpContext c, SocialAuthenticationService s, CancellationToken ct) =>
        {
            try
            {
                var (a, session) = Identity(c);
                if (!Expected(c, request.ExpectedAccountId)) return Results.Problem(statusCode: 409, title: "Your account changed. Review the request again.");
                await s.BeginPassword(a!.Value, session!.Value, request.Email, ct);
                return Results.Json(new { message = "Check your email. Open the verification link in this signed-in browser to choose a password." }, statusCode: 202);
            }
            catch (UserImportException e) { return Problem(e); }
        }).RequireAuthorization().RequireRateLimiting(AuthEndpoints.SignInRatePolicy);
        g.MapPost("/password/complete", async (SocialPasswordRequest request, HttpContext c, SocialAuthenticationService s, CancellationToken ct) =>
        {
            try
            {
                var (a, session) = Identity(c);
                if (!Expected(c, request.ExpectedAccountId)) return Results.Problem(statusCode: 409, title: "Your account changed. Review the request again.");
                await s.FinishPassword(a!.Value, session!.Value, request, ct);
                SessionCookie.Delete(c, c.Request.IsHttps); return Results.NoContent();
            }
            catch (UserImportException e) { return Problem(e); }
        }).RequireAuthorization().RequireRateLimiting(AuthEndpoints.SignInRatePolicy);
    }
}
