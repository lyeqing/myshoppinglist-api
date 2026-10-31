using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using OpenIddict.Abstractions;

namespace myshoppinglist_api.Mcp;

public static class AiIntegrationEndpoints
{
    public static void UseAiResourceValidation(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<AiIntegrationOptions>>().Value.Enabled) return;
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/mcp")) { await next(context); return; }
            context.Response.Headers.CacheControl = "no-store";
            var result = await context.AuthenticateAsync(McpAuthenticationDefaults.AuthenticationScheme);
            if (result.Principal is not { } principal)
            {
                await context.ChallengeAsync(McpAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            if (principal.Identities.Count(i => i.IsAuthenticated) != 1
                || !long.TryParse(principal.GetClaim(OpenIddictConstants.Claims.Subject), NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
                || accountId <= 0 || !await context.RequestServices.GetRequiredService<MyShoppingListDbContext>().UserAccounts
                    .AnyAsync(a => a.Id == accountId && a.IsActive && !a.IsTrial, context.RequestAborted))
            {
                await context.ChallengeAsync(McpAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            var identity = (ClaimsIdentity)principal.Identity!;
            var settings = context.RequestServices.GetRequiredService<AiIntegrationOptions>();
            var authorization = principal.GetAuthorizationId();
            var presenters = principal.GetPresenters();
            var db = context.RequestServices.GetRequiredService<MyShoppingListDbContext>();
            if (presenters.Length != 1 || !settings.Clients.Any(c => c.ClientId == presenters[0])
                || !await db.AiConnections.AnyAsync(c => c.AuthorizationId == authorization && c.UserAccountId == accountId
                    && c.ClientId == presenters[0] && c.RevokedDate == null, context.RequestAborted))
            {
                await context.ChallengeAsync(McpAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            // Connection activity is transport metadata, separate from read-only shopping services.
            var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
            await db.AiConnections.Where(c => c.AuthorizationId == authorization && c.UserAccountId == accountId
                && c.RevokedDate == null && (c.LastUsedDate == null || c.LastUsedDate < now.AddMinutes(-1)))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedDate, now), context.RequestAborted);
            foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier).ToArray()) identity.RemoveClaim(claim);
            identity.AddClaim(new(ClaimTypes.NameIdentifier, accountId.ToString(CultureInfo.InvariantCulture)));
            var scopes = principal.GetScopes();
            foreach (var claim in identity.FindAll("scope").ToArray()) identity.RemoveClaim(claim);
            identity.AddClaim(new("scope", string.Join(' ', scopes)));
            context.User = principal;
            await next(context);
        });
    }

    public static void MapAiIntegration(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<AiIntegrationOptions>>().Value.Enabled) return;
        app.MapAiOAuthEndpoints();
        app.MapMcp("/mcp").RequireAuthorization(AiIntegrationSetup.AccessPolicy).RequireRateLimiting("AiProtocol");
    }
}

public sealed class AiClientRegistration(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AiConnectionService>().EnsureClientsAsync(ct);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
