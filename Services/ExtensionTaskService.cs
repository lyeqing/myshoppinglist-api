using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Security;
using OpenIddict.EntityFrameworkCore.Models;

namespace myshoppinglist_api.Services;

public sealed partial class ExtensionTaskService(MyShoppingListDbContext db, TimeProvider clock,
    IOptions<ExtensionBridgeOptions> options, ExtensionDeviceService devices, CurrentListService current,
    ProductUrlValidator urls, PersonalProductCaptureService capture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static readonly string[] Terminal = ["completed", "failed", "expired", "cancelled"];
    private string State(ExtensionTask task) => !Terminal.Contains(task.Status) && task.ExpiresAt <= Now ? "expired" : task.Status;
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private async Task<AiConnection> ConnectionAsync(long account, string authorization, CancellationToken ct) =>
        await db.AiConnections.AsNoTracking().SingleOrDefaultAsync(c => c.UserAccountId == account && c.AuthorizationId == authorization && c.RevokedDate == null
            && db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Any(a => a.Id == c.AuthorizationId && a.Status == "valid"), ct)
        ?? throw new UserImportException(403, "This AI connection is unavailable.");

    public async Task<ExtensionTaskRequestResult> RequestAsync(long account, string authorization, Guid requestId, string type,
        string value, string? retailer, long? listId, int quantity, CancellationToken ct)
    {
        if (!options.Value.Enabled) throw new UserImportException(503, "AI extension requests are not enabled.");
        if (requestId == Guid.Empty || type is not ("capture" or "search") || value is null || value.Length > 2048) throw new UserImportException(400, "Invalid extension request.");
        var requestedListId = listId;
        string target; string? query = null;
        if (type == "capture")
        {
            var valid = urls.Validate(value);
            if (!valid.IsValid || !ExtensionDeviceService.Retailers.Contains(valid.Retailer!.Code) || quantity <= 0)
                return new(1, "unsupported_site", null, "Use a supported Coles, Woolworths or ALDI product URL.");
            retailer = valid.Retailer.Code;
            var product = valid.ProductUrl!;
            target = new UriBuilder(product) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
        }
        else
        {
            query = value.Trim(); retailer = retailer?.ToLowerInvariant();
            if (retailer is not ("coles" or "woolworths")) return new(1, "unsupported_site", null, "Limited search supports Coles and Woolworths only. You can use your AI's own web search and request a supported product URL capture.");
            if (query.Length is < 1 or > 100 || query.Any(char.IsControl)) throw new UserImportException(400, "Use a search of 1–100 characters.");
            target = ProductSearchQueryBuilder.SearchUrl(retailer, query).AbsoluteUri;
            listId = null; quantity = 1;
        }
        if (target.Length > 2000) throw new UserImportException(400, "The supported page URL is too long.");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { type, target, query, requestedListId, quantity }, Json)));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var user = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id"={account} FOR UPDATE""").SingleOrDefaultAsync(ct);
            if (user is not { IsActive: true, IsTrial: false }) throw new UserImportException(403, "A registered active account is required.");
            var connection = await ConnectionAsync(account, authorization, ct);
            var previous = await db.ExtensionTasks.AsNoTracking().SingleOrDefaultAsync(t => t.UserAccountId == account && t.AiConnectionId == connection.Id && t.RequestId == requestId, ct);
            if (previous is not null)
            {
                if (previous.RequestHash != hash) throw new UserImportException(409, "This request ID was used for different instructions.");
                return new(1, State(previous), await InfoAsync(previous, ct), "This is the same request; it has not been queued twice.");
            }
            if (type == "capture" && listId is null)
            {
                var selection = await current.ReadAsync(account, ct);
                if (selection.List is null) return new(1, selection.Status, null, "Choose a current shopping list on the website or specify an owned active list.");
                listId = selection.List.Id;
            }
            var now = Now;
            if (type == "capture" && !await db.ShoppingLists.AnyAsync(l => l.Id == listId && l.UserAccountId == account && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now), ct))
                throw new UserImportException(404, "The shopping list was not found.");
            var presence = await devices.PresenceAsync(account, ct);
            if (presence.Status is "not_linked" or "offline" or "needs_permission") return new(1, presence.Status, null, presence.Instructions);
            if (!presence.Retailers.Contains(retailer!)) return new(1, "needs_permission", null, "Enable this retailer in Chrome's extension site permissions, then refresh AI requests.");
            if (await db.ExtensionTasks.CountAsync(t => t.UserAccountId == account && !Terminal.Contains(t.Status) && t.ExpiresAt > now
                && t.AiConnection.RevokedDate == null && db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Any(a => a.Id == t.AiConnection.AuthorizationId && a.Status == "valid"), ct) >= options.Value.MaximumPendingTasks)
                return new(1, "busy", null, "Finish or cancel existing extension requests before queuing another.");
            var task = new ExtensionTask { UserAccountId = account, AiConnectionId = connection.Id, RequestId = requestId, RequestHash = hash,
                Type = type, Retailer = retailer!, Url = target, Query = query, ShoppingListId = listId, Quantity = quantity,
                CreatedAt = now, ExpiresAt = now.AddMinutes(options.Value.TaskLifetimeMinutes) };
            db.ExtensionTasks.Add(task); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(1, "queued", await InfoAsync(task, ct), "Open AI requests from your own extension toolbar and approve this request. Nothing has opened or been captured yet.");
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<ExtensionTaskInfo?> ReadAsync(long account, string authorization, Guid id, CancellationToken ct)
    {
        var task = await db.ExtensionTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.UserAccountId == account
            && t.AiConnection.UserAccountId == account && t.AiConnection.AuthorizationId == authorization && t.AiConnection.RevokedDate == null, ct);
        return task is null ? null : await InfoAsync(task, ct);
    }

    public async Task<ExtensionTaskInfo[]> PendingAsync(long account, Guid device, CancellationToken ct)
    {
        var now = Now;
        var tasks = await db.ExtensionTasks.Where(t => t.UserAccountId == account && t.ExpiresAt > now && t.AiConnection.RevokedDate == null
            && db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Any(a => a.Id == t.AiConnection.AuthorizationId && a.Status == "valid")
            && (t.Type == "search" || db.ShoppingLists.Any(l => l.Id == t.ShoppingListId && l.UserAccountId == account && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now)))
            && (t.Status == "queued" || t.Status == "awaiting_user" || t.DeviceId == device && (t.Status == "acknowledged" || t.Status == "running")))
            .OrderBy(t => t.CreatedAt).Take(options.Value.MaximumPendingTasks).ToArrayAsync(ct);
        foreach (var task in tasks) if (task.Status == "queued") task.Status = "awaiting_user";
        await db.SaveChangesAsync(ct);
        var result = new List<ExtensionTaskInfo>();
        foreach (var task in tasks) result.Add(await InfoAsync(task, ct));
        return result.ToArray();
    }

    public async Task<ExtensionTaskClaim> DecideAsync(long account, Guid device, Guid taskId, bool approve, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var task = await OwnedTaskAsync(account, taskId, ct);
            if (task.DeviceId is not null && task.DeviceId != device) throw new UserImportException(409, "This request is being handled in another of your browsers.");
            if (State(task) == "completed") return new(1, await InfoAsync(task, ct), null);
            Active(task);
            await OriginAndTargetAsync(task, ct);
            if (!approve)
            {
                task.Status = "cancelled"; task.ErrorCode = "user_declined"; task.CompletedAt = Now;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return new(1, await InfoAsync(task, ct), null);
            }
            var capability = await db.ExtensionDevices.AsNoTracking().SingleAsync(d => d.Id == device && d.UserAccountId == account && d.RevokedAt == null, ct);
            if (!capability.SupportedRetailers.Contains(task.Retailer)) throw new UserImportException(403, "Allow this retailer in Chrome before approving.");
            var secret = SessionToken.Create();
            task.DeviceId = device; task.ClaimHash = SessionToken.Hash(secret); task.Status = "acknowledged";
            task.AcknowledgedAt = Now; task.Attempts++;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(1, await InfoAsync(task, ct), secret);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<ExtensionTaskInfo> StartAsync(long account, Guid device, Guid taskId, string proof, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await ClaimedTaskAsync(account, device, taskId, proof, ct);
        if (State(task) == "completed") return await InfoAsync(task, ct);
        Active(task); await OriginAndTargetAsync(task, ct); task.Status = "running"; task.StartedAt ??= Now;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await InfoAsync(task, ct);
    }

    private async Task<ExtensionTask> OwnedTaskAsync(long account, Guid task, CancellationToken ct) =>
        await db.ExtensionTasks.FromSqlInterpolated($"""SELECT * FROM "ExtensionTasks" WHERE "Id"={task} AND "UserAccountId"={account} FOR UPDATE""").SingleOrDefaultAsync(ct)
        ?? throw new UserImportException(404, "The extension request was not found.");
    private async Task<ExtensionTask> ClaimedTaskAsync(long account, Guid device, Guid id, string proof, CancellationToken ct)
    {
        var task = await OwnedTaskAsync(account, id, ct);
        if (task.DeviceId != device || !SessionToken.IsValidFormat(proof) || task.ClaimHash != SessionToken.Hash(proof))
            throw new UserImportException(403, "This request is not assigned to this browser session.");
        return task;
    }
    private void Active(ExtensionTask task)
    {
        if (Terminal.Contains(State(task))) throw new UserImportException(409, "This request has ended or expired. Ask for a new request.");
    }
    private async Task OriginAndTargetAsync(ExtensionTask task, CancellationToken ct)
    {
        var now = Now;
        if (!await db.AiConnections.AnyAsync(c => c.Id == task.AiConnectionId && c.UserAccountId == task.UserAccountId && c.RevokedDate == null
            && db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Any(a => a.Id == c.AuthorizationId && a.Status == "valid"), ct))
            throw new UserImportException(409, "The AI connection was revoked. This request cannot continue.");
        if (task.Type == "capture" && !await db.ShoppingLists.AnyAsync(l => l.Id == task.ShoppingListId && l.UserAccountId == task.UserAccountId && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now), ct))
            throw new UserImportException(404, "The target shopping list is no longer available.");
    }
    private async Task<ExtensionTaskInfo> InfoAsync(ExtensionTask task, CancellationToken ct)
    {
        var connection = await db.AiConnections.AsNoTracking().SingleAsync(c => c.Id == task.AiConnectionId && c.UserAccountId == task.UserAccountId, ct);
        var now = Now;
        var name = task.ShoppingListId is null ? null : await db.ShoppingLists.AsNoTracking().Where(l => l.Id == task.ShoppingListId && l.UserAccountId == task.UserAccountId && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now)).Select(l => l.Name).SingleOrDefaultAsync(ct);
        var state = State(task); var error = state == "expired" ? "expired" : task.ErrorCode;
        if (!Terminal.Contains(state))
        {
            if (connection.RevokedDate is not null || !await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AnyAsync(a => a.Id == connection.AuthorizationId && a.Status == "valid", ct)) { state = "cancelled"; error = "origin_revoked"; }
            else if (task.Type == "capture" && name is null) { state = "failed"; error = "list_unavailable"; }
        }
        JsonElement? result = task.ResultJson is null ? null : JsonSerializer.Deserialize<JsonElement>(task.ResultJson);
        return new(1, task.Id.ToString(), task.Type, state, task.Retailer, task.Url, task.Query,
            task.ShoppingListId is { } list ? Id(list) : null, name, task.Quantity, connection.DisplayName, task.CreatedAt, task.ExpiresAt,
            error, result);
    }
}
