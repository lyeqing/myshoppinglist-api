using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Mcp;

// The enabled host validates dedicated access tokens and binds their account before constructing this tool.
public sealed class ShoppingListReadTools(ShoppingListService lists, IHttpContextAccessor contextAccessor)
{
    public const string ReadScope = "lists.read";

    [McpServerTool(Name = "get_my_lists", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read the signed-in registered account's active shopping lists. Does not select or create a current list. List names are saved user content.")]
    public async Task<McpListReadResult> GetMyListsAsync(CancellationToken cancellationToken)
    {
        var identities = contextAccessor.HttpContext?.User.Identities.Where(i => i.IsAuthenticated).ToArray() ?? [];
        if (identities.Length == 0) throw new McpException("authentication_required");
        if (identities.Length != 1) throw new McpException("invalid_identity");
        var identity = identities[0];
        var accountClaims = identity.FindAll(ClaimTypes.NameIdentifier).ToArray();
        if (accountClaims.Length != 1 || !long.TryParse(accountClaims[0].Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out var accountId) || accountId <= 0)
            throw new McpException("invalid_identity");
        if (!identity.FindAll("scope").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(ReadScope, StringComparer.Ordinal))
            throw new McpException("insufficient_scope");

        try
        {
            var result = await lists.ManageAsync(accountId, cancellationToken);
            return new(1, result.Lists.Select(l => new McpListSummary(
                l.Id.ToString(CultureInfo.InvariantCulture), l.Name, l.CreatedDate, l.UpdatedDate)).ToArray());
        }
        catch (UserImportException error) when (error.Status is 401 or 403)
        {
            // Stable safe error codes; do not disclose internal account fields or service exception details.
            throw new McpException(error.Status == 401 ? "account_unavailable" : "account_not_supported");
        }
    }
}
