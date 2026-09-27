using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Endpoints;

public static class UserExtensionEndpoints
{
    public const string LoginPath = "/api/user-extension/login";
    public static void MapUserExtensionEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(LoginPath, async (SignInRequest request, HttpContext context,
            IOptions<UserExtensionOptions> options, AccountAuthService service, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!options.Value.Allows(context.Request))
                return Results.Problem(statusCode: 403, title: "This extension is not enabled on the server. Ask the server owner to configure its extension ID.");
            var result = await service.SignInAsync(request, token);
            return result.Response is null ? Results.Problem(statusCode: result.StatusCode, title: result.Message)
                : Results.Ok(new { session = result.Response, token = result.Token });
        }).RequireRateLimiting(AuthEndpoints.SignInRatePolicy).WithTags("User extension");
    }
}
