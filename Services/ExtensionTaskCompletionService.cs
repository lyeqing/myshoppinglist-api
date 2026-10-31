using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;

namespace myshoppinglist_api.Services;

public sealed partial class ExtensionTaskService
{
    public async Task<ExtensionTaskInfo?> DeviceTaskAsync(long account, Guid device, Guid taskId, CancellationToken ct)
    {
        var task = await db.ExtensionTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId && t.UserAccountId == account
            && (t.DeviceId == null || t.DeviceId == device), ct);
        return task is null ? null : await InfoAsync(task, ct);
    }

    public async Task<PersonalProductPreviewResponse> PreviewAsync(long account, Guid device, Guid taskId, ExtensionTaskCapture request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await ClaimedTaskAsync(account, device, taskId, request.ClaimToken, ct);
        Active(task); await OriginAndTargetAsync(task, ct);
        if (task.Type != "capture" || task.Status != "running") throw new UserImportException(409, "Start the approved capture before reading it.");
        var preview = await capture.PreviewAsync(account, new(task.Url, request.Evidence, request.ObservedAt), ct);
        await tx.CommitAsync(ct);
        return preview;
    }

    public async Task<ExtensionTaskInfo> CompleteCaptureAsync(long account, Guid device, Guid taskId, ExtensionTaskCapture request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Same catalogue/account order as personal persistence; task and product commit together.
            await ProductService.LockCatalogueAsync(db, ct);
            await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id"={account} FOR UPDATE""").SingleOrDefaultAsync(ct);
            var task = await ClaimedTaskAsync(account, device, taskId, request.ClaimToken, ct);
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request.Evidence, request.ObservedAt }, Json)));
            if (State(task) == "completed")
            {
                if (task.CompletionHash != hash) throw new UserImportException(409, "This request was completed using different evidence. Read its saved outcome.");
                return await InfoAsync(task, ct);
            }
            Active(task); await OriginAndTargetAsync(task, ct);
            if (task.Type != "capture" || task.Status != "running") throw new UserImportException(409, "This capture is not running in this browser.");
            if (request.ObservedAt == default || request.ObservedAt > clock.GetUtcNow() || request.ObservedAt.UtcDateTime < task.StartedAt!.Value.AddSeconds(-5))
                throw new UserImportException(400, "Read the approved product page again before saving this request.");
            var result = await capture.CaptureAsync(account, new(task.ShoppingListId!.Value, task.Url, task.Quantity,
                request.Evidence, task.Id, request.ObservedAt), ct);
            var json = JsonSerializer.Serialize(result, Json);
            var now = Now;
            await db.ExtensionTasks.Where(t => t.Id == taskId && t.UserAccountId == account && t.DeviceId == device && t.ClaimHash == task.ClaimHash)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "completed").SetProperty(t => t.ResultJson, json).SetProperty(t => t.CompletionHash, hash).SetProperty(t => t.CompletedAt, now), ct);
            await tx.CommitAsync(ct);
            var completed = await db.ExtensionTasks.AsNoTracking().SingleAsync(t => t.Id == taskId && t.UserAccountId == account, ct);
            return await InfoAsync(completed, ct);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<ExtensionTaskInfo> CompleteSearchAsync(long account, Guid device, Guid taskId, ExtensionTaskSearch request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var task = await ClaimedTaskAsync(account, device, taskId, request.ClaimToken, ct);
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request.Candidates, request.EmptyConfirmed, request.ObservedAt }, Json)));
            if (State(task) == "completed")
            {
                if (task.CompletionHash != hash) throw new UserImportException(409, "This request already has a different saved result.");
                return await InfoAsync(task, ct);
            }
            Active(task); await OriginAndTargetAsync(task, ct);
            if (task.Type != "search" || task.Status != "running" || request.Candidates is null || request.Candidates.Length > 5
                || request.Candidates.Length == 0 && !request.EmptyConfirmed || request.ObservedAt == default
                || request.ObservedAt > clock.GetUtcNow() || request.ObservedAt.UtcDateTime < task.StartedAt!.Value.AddSeconds(-5))
                throw new UserImportException(400, "Send at most five reviewed results from this approved search page.");
            var candidates = new List<ExtensionSearchCandidate>();
            foreach (var candidate in request.Candidates)
            {
                var valid = urls.Validate(candidate.Url);
                if (!valid.IsValid || valid.Retailer!.Code != task.Retailer
                    || candidate.Name is { } name && (name.Length > 200 || name.Any(char.IsControl)))
                    throw new UserImportException(400, "A search result is not a supported public product link.");
                var canonical = new UriBuilder(valid.ProductUrl!) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
                if (canonical.Length > 2000 || candidates.Any(c => c.Url == canonical)) throw new UserImportException(400, "Send unique supported product links.");
                candidates.Add(new(canonical, string.IsNullOrWhiteSpace(candidate.Name) ? null : candidate.Name.Trim()));
            }
            var json = JsonSerializer.Serialize(new { version = 1, status = candidates.Count == 0 ? "no_results" : "candidates",
                candidates, observedAt = request.ObservedAt.ToUniversalTime(), source = "customer_approved_browser_search",
                pricesVerified = false, note = "Candidate links only. Product identity, availability and prices require a separate approved capture." }, Json);
            if (json.Length > 12000) throw new UserImportException(400, "The reviewed search result is too large.");
            task.Status = "completed"; task.ResultJson = json; task.CompletionHash = hash; task.CompletedAt = Now;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return await InfoAsync(task, ct);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<ExtensionTaskInfo> EndAsync(long account, Guid device, Guid taskId, ExtensionTaskFailure request, bool cancel, CancellationToken ct)
    {
        string[] errors = ["user_declined", "permission_denied", "retailer_blocked", "unsupported_site", "tab_closed",
            "navigation_changed", "page_unreadable", "device_offline", "account_changed", "expired", "list_unavailable", "invalid_evidence"];
        if (!errors.Contains(request.ErrorCode)) throw new UserImportException(400, "Use a supported failure code without page contents or private URLs.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var task = await ClaimedTaskAsync(account, device, taskId, request.ClaimToken, ct);
        if (Terminal.Contains(State(task))) return await InfoAsync(task, ct);
        task.Status = cancel ? "cancelled" : "failed"; task.ErrorCode = request.ErrorCode; task.CompletedAt = Now;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await InfoAsync(task, ct);
    }
}
