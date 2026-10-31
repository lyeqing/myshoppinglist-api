using System.Globalization;
using System.Security.Claims;
using ModelContextProtocol;

namespace myshoppinglist_api.Mcp;

internal static class McpAccount
{
    public static long Require(IHttpContextAccessor context, string scope)
    {
        var identities = context.HttpContext?.User.Identities.Where(i => i.IsAuthenticated).ToArray() ?? [];
        if (identities.Length == 0) throw new McpException("authentication_required");
        if (identities.Length != 1) throw new McpException("invalid_identity");
        var identity = identities[0];
        var accounts = identity.FindAll(ClaimTypes.NameIdentifier).ToArray();
        if (accounts.Length != 1 || !long.TryParse(accounts[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var account) || account <= 0)
            throw new McpException("invalid_identity");
        if (!identity.FindAll("scope").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(scope, StringComparer.Ordinal))
            throw new McpException("insufficient_scope");
        return account;
    }
}
