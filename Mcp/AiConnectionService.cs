using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace myshoppinglist_api.Mcp;

public sealed record AiConnectionSummary(Guid Id, string ClientName, string[] Scopes, DateTime ConnectedAt,
    DateTime? LastUsedAt, DateTime? RevokedAt);

public sealed class AiConnectionService(MyShoppingListDbContext db, IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations, IOpenIddictTokenManager tokens, AiIntegrationOptions settings, TimeProvider clock)
{
    public async Task EnsureClientsAsync(CancellationToken ct)
    {
        foreach (var client in settings.Clients)
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = client.ClientId, DisplayName = client.DisplayName, ClientType = ClientTypes.Public,
                ConsentType = ConsentTypes.Explicit
            };
            foreach (var uri in client.RedirectUris) descriptor.RedirectUris.Add(new(uri));
            descriptor.Permissions.UnionWith([Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation, Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code]);
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
            foreach (var scope in settings.AllowedScopes) descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
            descriptor.Permissions.Add(Permissions.Prefixes.Resource + settings.Resource);
            var existing = await applications.FindByClientIdAsync(client.ClientId, ct);
            if (existing is null) await applications.CreateAsync(descriptor, ct);
            else await applications.UpdateAsync(existing, descriptor, ct);
        }
    }

    public async Task<ClaimsPrincipal> ApproveAsync(long accountId, string clientId, string[] scopes, CancellationToken ct)
    {
        if (!settings.Clients.Any(c => c.ClientId == clientId) || scopes.Length == 0
            || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length || scopes.All(s => s == Scopes.OfflineAccess)
            || scopes.Any(s => s != Scopes.OfflineAccess && !settings.AllowedScopes.Contains(s, StringComparer.Ordinal)))
            throw new myshoppinglist_api.Services.UserImportException(400, "Invalid connection permissions.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.UserAccounts.AnyAsync(a => a.Id == accountId && a.IsActive && !a.IsTrial, ct))
            throw new myshoppinglist_api.Services.UserImportException(403, "A registered active account is required for AI connections.");
        var client = await applications.FindByClientIdAsync(clientId, ct) ?? throw new InvalidOperationException("Validated AI client was not found.");
        var identity = new ClaimsIdentity("MyShoppingListDelegatedToken", Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, accountId.ToString(CultureInfo.InvariantCulture));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, accountId.ToString(CultureInfo.InvariantCulture)).SetDestinations(Destinations.AccessToken));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources(settings.Resource);
        var authorization = await authorizations.CreateAsync(identity, accountId.ToString(CultureInfo.InvariantCulture),
            (await applications.GetIdAsync(client, ct))!, AuthorizationTypes.Permanent, scopes.ToImmutableArray(), ct);
        var authorizationId = (await authorizations.GetIdAsync(authorization, ct))!;
        principal.SetAuthorizationId(authorizationId);
        db.AiConnections.Add(new AiConnection
        {
            UserAccountId = accountId, AuthorizationId = authorizationId, ClientId = clientId,
            DisplayName = (await applications.GetDisplayNameAsync(client, ct)) ?? clientId,
            GrantedScopes = string.Join(' ', scopes), CreatedDate = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return principal;
    }

    public Task<AiConnectionSummary[]> ReadAsync(long accountId, CancellationToken ct) => db.AiConnections.AsNoTracking()
        .Where(c => c.UserAccountId == accountId).OrderByDescending(c => c.CreatedDate)
        .Select(c => new AiConnectionSummary(c.Id, c.DisplayName, c.GrantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            c.CreatedDate, c.LastUsedDate, c.RevokedDate)).ToArrayAsync(ct);

    public async Task<bool> RevokeAsync(long accountId, Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = await db.AiConnections.SingleOrDefaultAsync(c => c.Id == id && c.UserAccountId == accountId, ct);
        if (connection is null) return false;
        if (connection.RevokedDate is not null) return true;
        var authorization = await authorizations.FindByIdAsync(connection.AuthorizationId, ct);
        if (authorization is not null && !await authorizations.TryRevokeAsync(authorization, ct))
            throw new myshoppinglist_api.Services.UserImportException(409, "This connection changed. Refresh and try again.");
        await tokens.RevokeByAuthorizationIdAsync(connection.AuthorizationId, ct);
        connection.RevokedDate ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
