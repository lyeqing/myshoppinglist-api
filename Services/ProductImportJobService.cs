using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Services;

public sealed class ProductImportJobService(MyShoppingListDbContext db, IOptions<ProductImportOptions> options,
    TimeProvider clock, ILogger<ProductImportJobService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ProductImportClaim?> ClaimNextAsync(CancellationToken token)
    {
        // A single statement locks and transitions one row. It never holds a lock during HTTP work.
        var now = Now;
        var lease = now.AddSeconds(options.Value.LeaseSeconds);
        var claim = Guid.NewGuid();
        var rows = await db.ProductImportJobs.FromSqlInterpolated($"""
            WITH candidate AS (
                SELECT "Id" FROM "ProductImportJobs"
                WHERE "Status" = 'Queued' AND ("NextAttemptDate" IS NULL OR "NextAttemptDate" <= {now})
                  AND "AttemptCount" < {options.Value.MaxAttempts}
                ORDER BY "CreatedDate", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE "ProductImportJobs" j SET "Status" = 'Processing', "ProgressStage" = 'ValidatingUrl',
                "ClaimToken" = {claim}, "LeaseExpiresDate" = {lease}, "AttemptCount" = j."AttemptCount" + 1,
                "StartedDate" = COALESCE(j."StartedDate", {now}), "LastActivityDate" = {now},
                "NextAttemptDate" = NULL, "CompletedDate" = NULL, "ErrorCode" = NULL, "ErrorMessage" = NULL
            FROM candidate c WHERE j."Id" = c."Id" RETURNING j.*
            """).AsNoTracking().ToListAsync(token);
        var job = rows.SingleOrDefault();
        if (job is null) return null;
        logger.LogInformation("Import job {ProductImportJobId} claimed for list {ShoppingListId}, attempt {AttemptCount}", job.Id, job.ShoppingListId, job.AttemptCount);
        return new(job.Id, job.UserAccountId, job.ShoppingListId, claim);
    }

    public async Task<int> RecoverExpiredAsync(CancellationToken token)
    {
        var now = Now;
        // Bounded batches avoid a large startup transaction. Row-only operations never acquire catalogue locks afterwards.
        var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                SELECT "Id" FROM "ProductImportJobs"
                WHERE ("Status" = 'Processing' AND "LeaseExpiresDate" <= {now})
                   OR ("Status" = 'Queued' AND "AttemptCount" >= {options.Value.MaxAttempts})
                ORDER BY "CreatedDate", "Id" LIMIT 100 FOR UPDATE SKIP LOCKED
            )
            UPDATE "ProductImportJobs" j SET
                "Status" = CASE WHEN j."AttemptCount" < {options.Value.MaxAttempts} THEN 'Queued'
                    WHEN j."ShoppingListProductId" IS NOT NULL THEN 'Partial' ELSE 'Failed' END,
                "ProgressStage" = CASE WHEN j."AttemptCount" < {options.Value.MaxAttempts} THEN 'Queued'
                    WHEN j."ShoppingListProductId" IS NOT NULL THEN 'Completed' ELSE 'Failed' END,
                "ClaimToken" = NULL, "LeaseExpiresDate" = NULL, "LastActivityDate" = {now},
                "NextAttemptDate" = CASE WHEN j."AttemptCount" < {options.Value.MaxAttempts} THEN {now} ELSE NULL END,
                "CompletedDate" = CASE WHEN j."AttemptCount" < {options.Value.MaxAttempts} THEN NULL ELSE {now} END,
                "ErrorCode" = 'claim_expired', "ErrorMessage" = 'Processing was interrupted.'
            FROM expired e WHERE j."Id" = e."Id"
            """, token);
        if (count > 0) logger.LogInformation("Recovered {JobCount} interrupted import jobs", count);
        return count;
    }

    private IQueryable<ProductImportJob> Owned(ProductImportClaim claim, DateTime now) => db.ProductImportJobs.Where(j =>
        j.Id == claim.JobId && j.UserAccountId == claim.UserAccountId && j.ShoppingListId == claim.ShoppingListId
        && j.Status == ProductImportJobStatus.Processing && j.ClaimToken == claim.Token && j.LeaseExpiresDate > now);

    public async Task<bool> RenewAsync(ProductImportClaim claim, CancellationToken token)
    {
        var now = Now;
        return await Owned(claim, now).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.LeaseExpiresDate, now.AddSeconds(options.Value.LeaseSeconds))
            .SetProperty(j => j.LastActivityDate, now), token) == 1;
    }

    public Task<ProductImportJob?> ReadAsync(ProductImportClaim claim, CancellationToken token) =>
        Owned(claim, Now).AsNoTracking().SingleOrDefaultAsync(token);

    public Task<bool> HasAccessAsync(ProductImportClaim claim, CancellationToken token)
    {
        var now = Now;
        return Owned(claim, now).AnyAsync(j => j.UserAccount.IsActive
            && (!j.UserAccount.IsTrial || j.UserAccount.ExpiresDate > now)
            && !j.ShoppingList.IsArchived && (j.ShoppingList.ExpiresDate == null || j.ShoppingList.ExpiresDate > now), token);
    }

    public async Task<bool> ProgressAsync(ProductImportClaim claim, ProductImportProgressStage stage, CancellationToken token)
    {
        var now = Now;
        return await Owned(claim, now).ExecuteUpdateAsync(s => s.SetProperty(j => j.ProgressStage, stage)
            .SetProperty(j => j.LastActivityDate, now), token) == 1;
    }

    public async Task<bool> FailAsync(ProductImportClaim claim, string code, bool retryable, CancellationToken token,
        TimeSpan? retryAfter = null)
    {
        var job = await ReadAsync(claim, token);
        if (job is null) return false;
        var now = Now;
        var retry = retryable && job.AttemptCount < options.Value.MaxAttempts;
        var delay = Math.Max(options.Value.RetryDelaySeconds * Math.Pow(2, Math.Max(0, job.AttemptCount - 1)),
            retryAfter?.TotalSeconds ?? 0);
        var next = retry ? (DateTime?)now.AddSeconds(Math.Clamp(delay, 1, 3600)) : null;
        var status = retry ? ProductImportJobStatus.Queued : job.ShoppingListProductId.HasValue
            ? ProductImportJobStatus.Partial : ProductImportJobStatus.Failed;
        var stage = retry ? ProductImportProgressStage.Queued : job.ShoppingListProductId.HasValue
            ? ProductImportProgressStage.Completed : ProductImportProgressStage.Failed;
        var error = code[..Math.Min(code.Length, 100)];
        var changed = await Owned(claim, now).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status)
            .SetProperty(j => j.ProgressStage, stage).SetProperty(j => j.ClaimToken, (Guid?)null)
            .SetProperty(j => j.LeaseExpiresDate, (DateTime?)null).SetProperty(j => j.NextAttemptDate, next)
            .SetProperty(j => j.CompletedDate, retry ? (DateTime?)null : now).SetProperty(j => j.LastActivityDate, now)
            .SetProperty(j => j.ErrorCode, error).SetProperty(j => j.ErrorMessage, "The import could not finish this attempt."), token) == 1;
        if (changed) logger.LogInformation("Import job {ProductImportJobId} moved to {Status}: {ErrorCode}", claim.JobId, status, error);
        return changed;
    }

    public async Task<bool> CompleteSourceStageAsync(ProductImportClaim claim, CancellationToken token)
    {
        // Child results and completion share a transaction and the source persistence lock order.
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await ProductService.LockCatalogueAsync(db, token);
        var user = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {claim.UserAccountId} FOR UPDATE""").SingleOrDefaultAsync(token);
        var list = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {claim.ShoppingListId} FOR UPDATE""").SingleOrDefaultAsync(token);
        var job = await db.ProductImportJobs.FromSqlInterpolated($"""SELECT * FROM "ProductImportJobs" WHERE "Id" = {claim.JobId} FOR UPDATE""").SingleOrDefaultAsync(token);
        try
        {
            var now = Now;
            if (job is null || job.UserAccountId != claim.UserAccountId || job.ShoppingListId != claim.ShoppingListId
                || job.Status != ProductImportJobStatus.Processing || job.ClaimToken != claim.Token || !(job.LeaseExpiresDate > now)) return false;
            if (user is null || !user.IsActive || user.IsTrial && !(user.ExpiresDate > now)
                || list is null || list.UserAccountId != claim.UserAccountId || list.IsArchived || list.ExpiresDate <= now)
            {
                job.Status = ProductImportJobStatus.Cancelled;
                job.ErrorCode = "access_expired"; job.ErrorMessage = "The account or shopping list is no longer available.";
            }
            else
            {
                if (job.ShoppingListProductId is null || job.SourceShopId is null) return false;
                var shops = await db.Shops.Where(s => s.IsActive && s.Id != job.SourceShopId).Select(s => s.Id).ToListAsync(token);
                var results = await db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == job.Id).ToListAsync(token);
                foreach (var shopId in shops)
                {
                    var result = results.SingleOrDefault(r => r.ShopId == shopId);
                    if (result is not null && result.Status is not (RetailerLookupStatus.Pending or RetailerLookupStatus.Checking)) continue;
                    if (result is null)
                    {
                        result = new() { ProductImportJobId = job.Id, ShopId = shopId, CreatedDate = now };
                        db.ProductImportRetailerResults.Add(result); results.Add(result);
                    }
                    result.Status = RetailerLookupStatus.NotSupported;
                    result.ErrorCode = "comparison_not_implemented";
                    result.ErrorMessage = "Price comparison for this retailer is not implemented yet.";
                    result.CompletedDate = now; result.UpdatedDate = now;
                }
                var partial = !results.Any(r => r.ShopId == job.SourceShopId) || results.Any(r => r.Status is
                    RetailerLookupStatus.NotSupported or RetailerLookupStatus.Unavailable or RetailerLookupStatus.CheckFailed
                    or RetailerLookupStatus.Pending or RetailerLookupStatus.Checking);
                job.Status = partial ? ProductImportJobStatus.Partial : ProductImportJobStatus.Completed;
                job.ErrorCode = null; job.ErrorMessage = null;
            }
            // Lease/expiry can change with elapsed time even while rows are locked.
            now = Now;
            if (!(job.LeaseExpiresDate > now)) return false;
            if (user is not null && (user.IsTrial && !(user.ExpiresDate > now) || list?.ExpiresDate <= now))
            { job.Status = ProductImportJobStatus.Cancelled; job.ErrorCode = "access_expired"; }
            job.ProgressStage = ProductImportProgressStage.Completed;
            job.CompletedDate = now; job.LastActivityDate = now; job.NextAttemptDate = null;
            job.ClaimToken = null; job.LeaseExpiresDate = null;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            logger.LogInformation("Import job {ProductImportJobId}, list {ShoppingListId}, product {ProductId} finished as {Status}",
                job.Id, job.ShoppingListId, job.ProductId, job.Status);
            return true;
        }
        finally { db.ChangeTracker.Clear(); }
    }
}
