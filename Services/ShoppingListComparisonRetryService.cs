using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;

namespace myshoppinglist_api.Services;

public sealed class ShoppingListComparisonRetryService(MyShoppingListDbContext db, TimeProvider clock)
{
    private static long? TaskId(string? code) => code?.StartsWith("extension_task_failed_", StringComparison.Ordinal) == true
        && long.TryParse(code[22..], out var id) ? id : null;

    public async Task<Dictionary<long, ShoppingListComparison>> ReadAsync(long accountId, long listId, CancellationToken ct)
    {
        var jobs = await db.ProductImportJobs.AsNoTracking().Where(j => j.UserAccountId == accountId
            && j.ShoppingListId == listId && j.ShoppingListProductId != null
            && !db.ProductImportJobs.Any(newer => newer.ShoppingListId == listId && newer.UserAccountId == accountId
                && newer.ShoppingListProductId == j.ShoppingListProductId && newer.Id > j.Id)).ToArrayAsync(ct);
        var latest = jobs.DistinctBy(j => j.ShoppingListProductId).ToArray();
        var ids = latest.Select(j => j.Id).ToArray();
        var results = await db.ProductImportRetailerResults.AsNoTracking().Include(r => r.Shop)
            .Where(r => ids.Contains(r.ProductImportJobId)).ToArrayAsync(ct);
        var taskIds = latest.Select(j => TaskId(j.ErrorCode)).OfType<long>().ToArray();
        var tasks = await db.ColesExtensionTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        return latest.ToDictionary(j => j.ShoppingListProductId!.Value, j =>
        {
            var checks = results.Where(r => r.ProductImportJobId == j.Id && r.ShopId != j.SourceShopId
                && r.Shop.Code is "coles" or "woolworths").ToArray();
            var task = TaskId(j.ErrorCode) is { } id ? tasks.GetValueOrDefault(id) : null;
            var failed = checks.Any(r => r.Status == RetailerLookupStatus.CheckFailed)
                || j.Status == ProductImportJobStatus.Failed || task?.Status == "Failed";
            var active = j.Status is ProductImportJobStatus.Queued or ProductImportJobStatus.Processing;
            var blocked = task?.ErrorCode == "retailer_access_restricted"
                || checks.Any(r => r.ErrorCode == "retailer_access_restricted");
            DateTime? retryAfter = active ? j.NextAttemptDate : blocked
                ? (task?.CompletedAt ?? j.CompletedDate ?? j.LastActivityDate ?? j.CreatedDate).AddMinutes(15) : null;
            if (task?.NextAttemptAt is { } due && (retryAfter is null || due > retryAfter)) retryAfter = due;
            return new ShoppingListComparison(j.Id, active ? "Checking" : failed ? "Failed" : "Completed",
                task?.ErrorCode ?? checks.FirstOrDefault(r => r.Status == RetailerLookupStatus.CheckFailed)?.ErrorCode ?? j.ErrorCode,
                retryAfter, !active && failed && (retryAfter is null || retryAfter <= clock.GetUtcNow().UtcDateTime),
                checks.Select(r => new ShoppingListRetailerCheck(r.Shop.Name, r.Status.ToString(), r.ErrorCode)).ToArray());
        });
    }

    public async Task<ShoppingListComparison> RetryAsync(long accountId, long listId, long itemId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Follow the worker lock order and serialise double-clicks before changing a job or shared task.
        await ProductService.LockCatalogueAsync(db, ct);
        var account = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (account is null || !account.IsActive || account.IsTrial && !(account.ExpiresDate > now))
            throw new UserImportException(401, "Your session is unavailable.");
        var list = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {listId} AND "UserAccountId" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(ct);
        if (list is null) throw new UserImportException(404, "The shopping list was not found.");
        if (list.IsArchived || list.ExpiresDate <= now) throw new UserImportException(409, "This list is no longer active.");
        if (!await db.ShoppingListProducts.AnyAsync(i => i.Id == itemId && i.ShoppingListId == listId && !i.Product.IsDeleted, ct))
            throw new UserImportException(404, "The list item was not found.");
        var view = (await ReadAsync(accountId, listId, ct)).GetValueOrDefault(itemId)
            ?? throw new UserImportException(409, "There is no comparison to retry.");
        if (view.Status == "Checking") return view;
        if (!view.CanRetry) throw new UserImportException(409, view.RetryAfter > now
            ? "This comparison is cooling down. Please try again later." : "There is no failed comparison to retry.");
        var job = await db.ProductImportJobs.SingleAsync(j => j.Id == view.JobId, ct);
        if (TaskId(job.ErrorCode) is { } taskId)
        {
            var task = await db.ColesExtensionTasks.SingleOrDefaultAsync(t => t.Id == taskId, ct);
            if (task?.Status == "Failed") ColesExtensionTaskService.Reset(task);
        }
        // Hand personal-extension failures to the normal queue. Existing item identity prevents source re-import.
        await db.UserExtensionImportTasks.Where(t => t.ProductImportJobId == job.Id).ExecuteDeleteAsync(ct);
        job.Status = ProductImportJobStatus.Queued;
        job.ProgressStage = ProductImportProgressStage.Queued;
        job.AttemptCount = 0;
        job.ClaimToken = null;
        job.LeaseExpiresDate = null;
        job.CompletedDate = null;
        job.NextAttemptDate = null;
        job.ErrorCode = null;
        job.ErrorMessage = null;
        job.LastActivityDate = now;
        await db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == job.Id && r.ShopId != job.SourceShopId
            && r.Status == RetailerLookupStatus.CheckFailed).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RetailerLookupStatus.Pending).SetProperty(r => r.ErrorCode, (string?)null)
                .SetProperty(r => r.CompletedDate, (DateTime?)null).SetProperty(r => r.UpdatedDate, now), ct);
        await db.SaveChangesAsync(ct);
        var queued = (await ReadAsync(accountId, listId, ct))[itemId];
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return queued;
    }
}
