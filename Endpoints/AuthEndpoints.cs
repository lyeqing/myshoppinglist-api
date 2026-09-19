using System.Globalization;
using System.Security.Claims;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class AuthEndpoints
{
    public const string TrialRatePolicy = "trial-start";
    public static void MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth").WithTags("Authentication");
        group.MapPost("/trial", StartAsync).RequireRateLimiting(TrialRatePolicy)
            .Produces<TrialStartResponse>(201).Produces<TrialStartResponse>().ProducesProblem(403).ProducesProblem(409).ProducesProblem(429);
        group.MapGet("/me", MeAsync).RequireAuthorization().Produces<CurrentSessionResponse>().ProducesProblem(401);
        group.MapPost("/logout", LogoutAsync).RequireAuthorization().Produces(204).ProducesProblem(401).ProducesProblem(403);
    }
    private static (long Account, long Session) Identity(HttpContext context) =>
        (long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture),
         long.Parse(context.User.FindFirstValue(SessionTokenAuthenticationHandler.SessionIdClaim)!, CultureInfo.InvariantCulture));
    private static void NoStore(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    private static async Task<IResult> StartAsync(HttpContext context, TrialSessionService service, IWebHostEnvironment environment, CancellationToken token)
    {
        NoStore(context);
        if (context.Request.Headers.ContainsKey("Authorization") && context.User.Identity?.IsAuthenticated != true)
            return Results.Problem(statusCode: 401, title: "A valid session is required.");
        var identity = context.User.Identity?.IsAuthenticated == true ? Identity(context) : ((long Account, long Session)?)null;
        var result = await service.StartAsync(identity?.Account, identity?.Session, token);
        if (result.Response is null) return Results.Problem(statusCode: result.ErrorCode == "invalid_session" ? 401 : 409,
            title: result.ErrorCode == "registered_session" ? "You are already signed in to a registered account." : "The trial session is unavailable.");
        if (result.Token is not null) SessionCookie.Write(context, result.Token, result.Response.SessionExpiresDate,
            !environment.IsDevelopment() || context.Request.IsHttps);
        return result.Response.Reused ? Results.Ok(result.Response) : Results.Created("/api/auth/me", result.Response);
    }
    private static async Task<IResult> MeAsync(HttpContext context, TrialSessionService service, CancellationToken token)
    {
        NoStore(context);
        var identity = Identity(context);
        var current = await service.CurrentAsync(identity.Account, identity.Session, token);
        return current is null ? Results.Problem(statusCode: 401, title: "A valid session is required.") : Results.Ok(current);
    }
    private static async Task<IResult> LogoutAsync(HttpContext context, TrialSessionService service, IWebHostEnvironment environment, CancellationToken token)
    {
        NoStore(context);
        var identity = Identity(context);
        await service.LogoutAsync(identity.Account, identity.Session, token);
        SessionCookie.Delete(context, !environment.IsDevelopment() || context.Request.IsHttps);
        return Results.NoContent();
    }
}
