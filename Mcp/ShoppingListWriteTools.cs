using System.ComponentModel;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Mcp;

public sealed class ShoppingListWriteTools(McpMutationCoordinator commands, ShoppingListService lists, CurrentListService current,
    ProductLibraryService products, MyShoppingListDbContext db, IHttpContextAccessor context, TimeProvider clock,
    ResearchService? research = null)
{
    [McpServerTool(Name = "create_list", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create your own list within your existing account limits. Requires lists.write. Use a new UUID requestId for each intended change and reuse exactly that ID and inputs on retries. Does not select the new list as current.")]
    public Task<McpMutationResult> CreateListAsync(string requestId, string? name = null, CancellationToken cancellationToken = default)
    {
        var account = McpAccount.Require(context, AiScopes.ListsWrite);
        var request = Request(requestId);
        return Execute(account, request, "create_list", new { name }, async () =>
        {
            ShoppingListSummary list;
            try { list = await lists.CreateAsync(account, new(name), null, cancellationToken); }
            catch (UserImportException error) when (error.Status == 409) { throw new McpException("list_limit_reached"); }
            return new(1, "created", request.ToString(), false, Id(list.Id), UpdatedAt: list.UpdatedDate);
        }, cancellationToken);
    }

    [McpServerTool(Name = "set_current_list", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Explicitly select the account's current list shared with the website and extension. Requires lists.write and the exact selectionVersion returned by get_current_list. A deleted prior selection can retain a nonzero revision. Retry using the same UUID requestId and inputs.")]
    public Task<McpMutationResult> SetCurrentListAsync(string requestId, string listId, string expectedSelectionVersion, CancellationToken cancellationToken = default)
    {
        var account = McpAccount.Require(context, AiScopes.ListsWrite);
        var request = Request(requestId); var list = Positive(listId); var version = Version(expectedSelectionVersion);
        return Execute(account, request, "set_current_list", new { list, version }, async () =>
        {
            // Check ownership before returning any selection-conflict detail.
            if (!(await lists.ManageAsync(account, cancellationToken)).Lists.Any(l => l.Id == list)) throw new McpException("not_found");
            var selection = await current.ReadAsync(account, cancellationToken);
            if (selection.SelectionVersion != version) throw new McpWriteConflictException(new(1, "conflict", true, Id(list), CurrentSelectionVersion: Id(selection.SelectionVersion)));
            var selected = await current.SetAsync(account, new(account, list, version), cancellationToken);
            return new(1, "selected", request.ToString(), false, Id(list), SelectionVersion: Id(selected.SelectionVersion));
        }, cancellationToken);
    }

    [McpServerTool(Name = "add_product_to_list", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Add one reviewed candidateId after the customer's choice, an already saved productId, or a genericName to your own list. Supply exactly one. Candidate and generic quantities count packages; saved productId retains the existing quantity-one contract. A product already on the list is returned unchanged; use update_list_item to edit quantity. Candidate prices stay unverified and excluded from totals. Reuse UUID requestId and inputs on retries. Requires lists.write.")]
    public Task<McpMutationResult> AddProductToListAsync(string requestId, string listId, string? productId = null, string? genericName = null,
        int quantity = 1, CancellationToken cancellationToken = default, string? candidateId = null)
    {
        var account = McpAccount.Require(context, AiScopes.ListsWrite);
        var request = Request(requestId); var list = Positive(listId);
        if (candidateId is not null)
        {
            if (productId is not null || genericName is not null || research is null) throw new McpException("invalid_arguments");
            return ResearchReadTools.Safe(() => research.SelectAsync(account,
                new(request, ResearchReadTools.GuidId(candidateId), list, quantity), cancellationToken));
        }
        if ((productId is null) == (genericName is null) || quantity <= 0 || productId is not null && quantity != 1) throw new McpException("invalid_arguments");
        var product = productId is null ? (long?)null : Positive(productId);
        return Execute(account, request, "add_product_to_list", new { list, product, genericName, quantity }, async () =>
        {
            var added = product is { } id ? await products.AddAsync(account, id, list, new(account), cancellationToken)
                : await products.AddGenericAsync(account, list, genericName!, quantity, cancellationToken);
            var item = await Editable(account, list, added.ItemId, cancellationToken);
            return new(1, added.AlreadyOnList ? "already_on_list" : "added", request.ToString(), false, Id(list), Id(item.Id), item.UpdatedDate);
        }, cancellationToken);
    }

    [McpServerTool(Name = "update_list_item", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Change only supplied fields of your own saved list item. Requires lists.write, its current UTC updatedAt and a UUID requestId. Omitted fields are preserved; clearNotes=true explicitly removes notes. Completing all items may archive the list under existing product behaviour. On conflict, refresh before deciding whether to retry with a new requestId/version.")]
    public Task<McpMutationResult> UpdateListItemAsync(string requestId, string listId, string itemId, string expectedUpdatedAt,
        int? quantity = null, string? notes = null, bool clearNotes = false, bool? purchased = null, bool? hidden = null, CancellationToken cancellationToken = default)
    {
        var account = McpAccount.Require(context, AiScopes.ListsWrite);
        var request = Request(requestId); var list = Positive(listId); var itemIdValue = Positive(itemId); var expected = Timestamp(expectedUpdatedAt);
        if (clearNotes && notes is not null || quantity is <= 0 || notes?.Length > 4000
            || quantity is null && notes is null && !clearNotes && purchased is null && hidden is null) throw new McpException("invalid_arguments");
        return Execute(account, request, "update_list_item", new { list, itemIdValue, expected, quantity, notes, clearNotes, purchased, hidden }, async () =>
        {
            var item = await Editable(account, list, itemIdValue, cancellationToken);
            CheckVersion(item, list, expected);
            var result = await lists.UpdateAsync(account, list, itemIdValue, new(quantity ?? item.Quantity,
                clearNotes ? null : notes ?? item.Notes, purchased ?? item.IsPurchased, hidden ?? item.IsHidden, expected), cancellationToken);
            Status(result.StatusCode);
            return new(1, "updated", request.ToString(), false, Id(list), Id(itemIdValue), result.Item!.UpdatedDate, ListArchived: result.Item.ListArchived);
        }, cancellationToken);
    }

    [McpServerTool(Name = "remove_list_item", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Remove exactly one item from your own list, after the customer's explicit confirmation. Requires confirm=true, its current UTC updatedAt, lists.write and a UUID requestId. Does not remove unrelated products or lists. Reuse the exact requestId and inputs if the result is lost.")]
    public Task<McpMutationResult> RemoveListItemAsync(string requestId, string listId, string itemId, string expectedUpdatedAt, bool confirm,
        CancellationToken cancellationToken = default)
    {
        var account = McpAccount.Require(context, AiScopes.ListsWrite);
        var request = Request(requestId); var list = Positive(listId); var itemIdValue = Positive(itemId); var expected = Timestamp(expectedUpdatedAt);
        if (!confirm) throw new McpException("confirmation_required");
        return Execute(account, request, "remove_list_item", new { list, itemIdValue, expected, confirm }, async () =>
        {
            var item = await Editable(account, list, itemIdValue, cancellationToken);
            CheckVersion(item, list, expected);
            Status((await lists.DeleteAsync(account, list, itemIdValue, new(expected), cancellationToken)).StatusCode);
            var archived = await db.ShoppingLists.Where(l => l.Id == list && l.UserAccountId == account).Select(l => l.IsArchived).SingleAsync(cancellationToken);
            return new(1, "removed", request.ToString(), false, Id(list), Id(itemIdValue), ListArchived: archived);
        }, cancellationToken);
    }

    private async Task<myshoppinglist_api.Models.ShoppingListProduct> Editable(long account, long list, long item, CancellationToken ct) =>
        await db.ShoppingListProducts.AsNoTracking().SingleOrDefaultAsync(i => i.Id == item && i.ShoppingListId == list
            && i.ShoppingList.UserAccountId == account && !i.ShoppingList.IsArchived && !i.Product.IsDeleted
            && (i.ShoppingList.ExpiresDate == null || i.ShoppingList.ExpiresDate > clock.GetUtcNow().UtcDateTime)
            && (i.Product.UserAccountId == null || i.Product.UserAccountId == account), ct)
        ?? throw new McpException("not_found");
    private static void CheckVersion(myshoppinglist_api.Models.ShoppingListProduct item, long list, DateTime expected)
    {
        if (item.UpdatedDate != expected) throw new McpWriteConflictException(new(1, "conflict", true, Id(list), Id(item.Id), item.UpdatedDate));
    }
    private static void Status(int status)
    {
        if (status is >= 200 and < 300) return;
        throw new McpException(status switch { 404 => "not_found", 409 => "conflict", _ => "invalid_arguments" });
    }
    private async Task<McpMutationResult> Execute(long account, Guid request, string operation, object input, Func<Task<McpMutationResult>> action, CancellationToken ct)
    {
        try { return await commands.ExecuteAsync(account, request, operation, input, action, ct); }
        catch (UserImportException error) { throw new McpException(error.Status switch { 401 => "account_unavailable", 403 => "account_not_supported", 404 => "not_found", 409 => "conflict", _ => "invalid_arguments" }); }
    }
    private static Guid Request(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new McpException("invalid_arguments");
    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static long Positive(string value) => Version(value) is > 0 and var id ? id : throw new McpException("invalid_arguments");
    private static long Version(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 0 ? version : throw new McpException("invalid_arguments");
    private static DateTime Timestamp(string value) => value.Length <= 35 && value.EndsWith('Z') && value.Contains('T')
        && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) && timestamp.Kind == DateTimeKind.Utc
        ? timestamp : throw new McpException("invalid_arguments");
}
