using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace myshoppinglist_api.Mcp;

public static class AiOAuthEndpoints
{
    private const string Server = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public static void MapAiOAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/connect/authorize", (HttpContext c, AiIntegrationOptions settings) =>
        {
            var request = c.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Missing validated OAuth request.");
            c.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(settings.FrontendOrigin.TrimEnd('/') + "/ai/connect?request_uri=" + Uri.EscapeDataString(request.RequestUri!)
                + "&client_id=" + Uri.EscapeDataString(request.ClientId!));
        });
        routes.MapPost("/api/ai/oauth/preview", PreviewAsync);
        routes.MapPost("/api/ai/oauth/authorize", AuthorizeAsync);
        routes.MapPost("/connect/token", TokenAsync);
        routes.MapGet("/api/ai/connection-info", (HttpContext c, AiIntegrationOptions settings) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { resource = settings.Resource });
        }).RequireAuthorization();
        var connections = routes.MapGroup("/api/ai/connections").RequireAuthorization();
        connections.MapGet("", async (HttpContext c, AiConnectionService service, CancellationToken ct) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ReadAsync(Account(c.User), ct));
        });
        connections.MapDelete("/{id:guid}", async (Guid id, HttpContext c, AiConnectionService service, CancellationToken ct) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            try { return await service.RevokeAsync(Account(c.User), id, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (UserImportException error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
        });
    }

    private static long Account(ClaimsPrincipal principal) => long.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    // These endpoints require a first-party cookie and the existing custom-header CSRF protection.
    // A browser session may approve a grant; it is never an MCP access token.
    private static bool CookieSession(HttpContext c) => c.User.Identity?.IsAuthenticated == true
        && c.User.FindFirstValue(SessionTokenAuthenticationHandler.TransportClaim) == "cookie";

    private static async Task<IResult> PreviewAsync(HttpContext c, IOpenIddictApplicationManager applications,
        AiIntegrationOptions settings, MyShoppingListDbContext db, CancellationToken ct)
    {
        c.Response.Headers.CacheControl = "no-store";
        if (!CookieSession(c)) return Results.Unauthorized();
        var request = c.GetOpenIddictServerRequest()!;
        if (!ValidResource(request, settings)) return Deny(Errors.InvalidTarget);
        if (!await db.UserAccounts.AnyAsync(a => a.Id == Account(c.User) && a.IsActive && !a.IsTrial, ct)) return Results.Forbid();
        var application = await applications.FindByClientIdAsync(request.ClientId!, ct);
        return Results.Ok(new { accountId = Account(c.User).ToString(CultureInfo.InvariantCulture),
            clientName = await applications.GetDisplayNameAsync(application!, ct),
            scopes = request.GetScopes().Where(s => s != Scopes.OfflineAccess).ToArray(),
            offlineAccess = request.HasScope(Scopes.OfflineAccess) });
    }

    private static bool ValidResource(OpenIddictRequest request, AiIntegrationOptions settings) =>
        settings.Clients.Any(c => c.ClientId == request.ClientId)
        && request.GetResources().SequenceEqual([settings.Resource], StringComparer.Ordinal)
        && request.GetScopes().Any(s => s != Scopes.OfflineAccess)
        && request.GetScopes().All(s => s == Scopes.OfflineAccess || settings.AllowedScopes.Contains(s, StringComparer.Ordinal));

    private static async Task<IResult> AuthorizeAsync(HttpContext c, AiConnectionService service,
        AiIntegrationOptions settings, CancellationToken ct)
    {
        c.Response.Headers.CacheControl = "no-store";
        if (!CookieSession(c)) return Results.Unauthorized();
        var request = c.GetOpenIddictServerRequest()!;
        if (!ValidResource(request, settings)) return Deny(Errors.InvalidTarget);
        var form = await c.Request.ReadFormAsync(ct);
        if (form["expected_account_id"].Count != 1 || form["expected_account_id"] != Account(c.User).ToString(CultureInfo.InvariantCulture))
            return Results.Problem(statusCode: 409, title: "Your account changed. Review the connection again.");
        if (form["decision"] != "approve") return Deny(Errors.AccessDenied);
        // Consent always covers the exact validated request. No hidden scope escalation or automatic re-consent.
        var selected = form["approved_scopes"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (selected.Length == 0 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length
            || selected.Any(s => !request.HasScope(s)) || selected.All(s => s == Scopes.OfflineAccess))
            return Results.Problem(statusCode: 400, title: "Choose permissions from this connection request.");
        try { return Results.SignIn(await service.ApproveAsync(Account(c.User), request.ClientId!, selected, ct), authenticationScheme: Server); }
        catch (UserImportException error) { return Results.Problem(statusCode: error.Status, title: error.Message); }
    }

    private static async Task<IResult> TokenAsync(HttpContext c, MyShoppingListDbContext db, AiIntegrationOptions settings, CancellationToken ct)
    {
        var request = c.GetOpenIddictServerRequest()!;
        if (!settings.Clients.Any(client => client.ClientId == request.ClientId)) return Deny(Errors.InvalidClient);
        var result = await c.AuthenticateAsync(Server);
        if (result.Principal is not { } principal || !long.TryParse(principal.GetClaim(Claims.Subject), out var accountId)
            || !await db.UserAccounts.AnyAsync(a => a.Id == accountId && a.IsActive && !a.IsTrial, ct)) return Deny(Errors.InvalidGrant);
        if (request.GetResources().Any(r => r != settings.Resource)) return Deny(Errors.InvalidTarget);
        if (request.IsRefreshTokenGrantType() && request.GetScopes().Length > 0)
        {
            if (request.GetScopes().Any(s => !principal.HasScope(s))) return Deny(Errors.InvalidScope);
            principal.SetScopes(request.GetScopes());
        }
        return Results.SignIn(principal, authenticationScheme: Server);
    }

    private static IResult Deny(string error) => Results.Forbid(new AuthenticationProperties(
        new Dictionary<string, string?> { [OpenIddictServerAspNetCoreConstants.Properties.Error] = error }), [Server]);
}
