using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace myshoppinglist_api.Endpoints;

public static class UserExtensionEndpoints
{
    public const string LoginPath = "/api/user-extension/login";
    public static void MapUserExtensionEndpoints(this IEndpointRouteBuilder routes)
    {
        var imports = routes.MapGroup("/api/user-extension/imports").RequireAuthorization();
        imports.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UserImportException e) { return Results.Problem(statusCode: e.Status, title: e.Message); }
        });
        static long Account(HttpContext c) => long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        imports.MapPost("/", async (UserExtensionStart request, HttpContext context, UserExtensionImportService service, CancellationToken ct) =>
            Results.Ok(await service.StartAsync(Account(context), request, ct)))
            .WithMetadata(new RequestSizeLimitAttribute(2500000)).RequireRateLimiting(ProductImportEndpoints.SubmissionRatePolicy);
        imports.MapGet("/{id:long}", async (long id, HttpContext context, UserExtensionImportService service, CancellationToken ct) =>
            Results.Ok(await service.ReadAsync(Account(context), id, ct)));
        imports.MapPost("/{id:long}/result", async (long id, UserExtensionResult request, HttpContext context, UserExtensionImportService service, CancellationToken ct) =>
            Results.Ok(await service.ResultAsync(Account(context), id, request, ct))).WithMetadata(new RequestSizeLimitAttribute(2500000));
        imports.MapPost("/{id:long}/retry", async (long id, HttpContext context, UserExtensionImportService service, CancellationToken ct) =>
            Results.Ok(await service.RetryAsync(Account(context), id, ct)));
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
