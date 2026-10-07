using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class UserImportException(int status, string message) : Exception(message) { public int Status { get; } = status; }
public sealed record UserImportCandidate(string Url, ColesEvidence Evidence);

public sealed class UserExtensionImportService(MyShoppingListDbContext db, ProductImportSubmissionService submissions,
    SourceProductPersistenceService sourcePersistence, RetailerComparisonPersistenceService comparisons,
    ProductImportJobService jobs, ProductUrlValidator urls, ColesProductParser coles, WoolworthsProductParser woolworths,
    ProductMatchingService matching, TimeProvider clock, IHttpContextAccessor? http = null)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private async Task<long> Observe(long account, long? job, string url, CancellationToken ct)
    {
        var observation = new ContributionObservation
        {
            UserAccountId = account,
            ImportJobId = job,
            Url = url,
            Source = "PersonalAdd",
            ReceivedAt = Now,
            CollectedAt = Now,
            ExtensionVersion = ContributionService.Version(http?.HttpContext?.Request.Headers["X-Extension-Version"].ToString())
        };
        db.ContributionObservations.Add(observation); await db.SaveChangesAsync(ct); db.ChangeTracker.Clear(); return observation.Id;
    }
    private Task Outcome(long id, string outcome, CancellationToken ct) => db.ContributionObservations.Where(o => o.Id == id)
        .ExecuteUpdateAsync(s => s.SetProperty(o => o.Outcome, outcome), ct);
    private async Task QueueTrusted(long account, long jobId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ProductService.LockCatalogueAsync(db, ct);
        var removed = await db.UserExtensionImportTasks.Where(t => t.ProductImportJobId == jobId && t.ProductImportJob.UserAccountId == account).ExecuteDeleteAsync(ct);
        if (removed > 0) await db.ProductImportJobs.Where(j => j.Id == jobId && j.UserAccountId == account).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, ProductImportJobStatus.Queued).SetProperty(j => j.ProgressStage, ProductImportProgressStage.Queued)
            .SetProperty(j => j.ClaimToken, (Guid?)null).SetProperty(j => j.LeaseExpiresDate, (DateTime?)null)
            .SetProperty(j => j.AttemptCount, 0).SetProperty(j => j.ErrorCode, (string?)null).SetProperty(j => j.ErrorMessage, (string?)null)
            .SetProperty(j => j.NextAttemptDate, (DateTime?)null).SetProperty(j => j.CompletedDate, (DateTime?)null), ct);
        await tx.CommitAsync(ct); db.ChangeTracker.Clear();
    }
    public async Task<UserExtensionWork> StartAsync(long account, UserExtensionStart request, CancellationToken ct)
    {
        if (await db.UserAccounts.AnyAsync(a => a.Id == account && a.ContributionBlocked, ct))
        {
            var queued = await submissions.SubmitAsync(account, request.ListId, new(request.Url, request.Quantity), ct);
            if (queued.Response is null) throw new UserImportException(queued.StatusCode, queued.Message ?? "Could not queue import.");
            await QueueTrusted(account, queued.Response.JobId, ct);
            return new(queued.Response.JobId, "Queued", false, "trusted", Guid.Empty, null, null, null);
        }
        ExtractedShopProduct source;
        try { source = await ParseAsync(request.Url, request.Evidence, ct); }
        catch (UserImportException)
        {
            if (urls.Validate(request.Url).IsValid)
            {
                var rejected = await Observe(account, null, request.Url, ct);
                await Outcome(rejected, "InvalidSourceEvidence", ct);
            }
            throw;
        }
        var submitted = await submissions.SubmitAsync(account, request.ListId, new(request.Url, request.Quantity), ct, userExtension: true, userRequestId: request.RequestId);
        if (submitted.Response is null) throw new UserImportException(submitted.StatusCode, submitted.Message ?? "Could not start import.");
        return await Locked(account, submitted.Response.JobId, async (task, job) =>
        {
            if (task.Stage != "source") return View(task, job);
            var claim = await Claim(job, ct);
            if (job.ShoppingListProductId is null)
            {
                var observation = await Observe(account, job.Id, request.Url, ct);
                var saved = await sourcePersistence.SaveAsync(job.Id, account, claim.Token, source, ct, observation: observation);
                await Outcome(observation, saved.Status == ProductPersistenceStatus.Success ? "Accepted" : saved.ErrorCode ?? "Rejected", ct);
                if (saved.Status != ProductPersistenceStatus.Success)
                    return await Fail(task, job, claim, saved.ErrorCode ?? "invalid_source_evidence", ct);
            }
            job = (await jobs.ReadAsync(claim, ct))!;
            return await BeginComparison(task, job, claim, ct);
        }, ct);
    }

    public Task<UserExtensionWork> ReadAsync(long account, long jobId, CancellationToken ct) =>
        Locked(account, jobId, (task, job) => Task.FromResult(View(task, job)), ct);

    public Task<UserExtensionWork> RetryAsync(long account, long jobId, CancellationToken ct) => Locked(account, jobId, async (task, job) =>
    {
        if (await db.UserAccounts.AnyAsync(a => a.Id == account && a.ContributionBlocked, ct))
        {
            await QueueTrusted(account, jobId, ct);
            return new(jobId, "Queued", job.ShoppingListProductId != null, "trusted", Guid.Empty, null, null, null);
        }
        if (job.ShoppingListProductId is null) throw new UserImportException(409, "Open the original product page and click Add again to read it.");
        if (task.Status != "Failed" && job.Status is not (ProductImportJobStatus.Partial or ProductImportJobStatus.Failed)) return View(task, job);
        var claim = await Claim(job, ct);
        var other = await OtherShop(job, ct);
        await db.ProductImportRetailerResults.Where(r => r.ProductImportJobId == job.Id && r.ShopId == other.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RetailerLookupStatus.Pending).SetProperty(r => r.ErrorCode, (string?)null), ct);
        task.Status = "Waiting"; task.ErrorCode = null; task.HadFailures = false;
        task.LinksJson = "[]"; task.MatchesJson = "[]";
        return await BeginComparison(task, job, claim, ct);
    }, ct);

    public Task<UserExtensionWork> ResultAsync(long account, long jobId, UserExtensionResult result, CancellationToken ct) => Locked(account, jobId, async (task, job) =>
    {
        if (await db.UserAccounts.AnyAsync(a => a.Id == account && a.ContributionBlocked, ct))
        {
            await QueueTrusted(account, jobId, ct);
            return new(jobId, "Queued", job.ShoppingListProductId != null, "trusted", Guid.Empty, null, null, null);
        }
        // Duplicate deliveries return the current step, never apply old evidence to a newer step.
        if (task.StepToken != result.StepToken || task.Status != "Waiting") return View(task, job);
        if (task.Stage == "source") throw new UserImportException(409, "Submit the source using Add.");
        if (result.Url != task.Url) throw new UserImportException(400, "The result does not belong to this task URL.");
        var received = await Observe(account, jobId, task.Url, ct);
        await Outcome(received, result.Ok ? "ReceivedForValidation" : "ExtractionFailed", ct);
        if (job.Status is ProductImportJobStatus.Partial or ProductImportJobStatus.Failed)
            throw new UserImportException(409, "This comparison expired. Use Retry comparison.");
        var claim = await Claim(job, ct);
        var other = await OtherShop(job, ct);
        if (await db.ProductImportRetailerResults.AnyAsync(r => r.ProductImportJobId == job.Id && r.ShopId == other.Id
            && r.Status == RetailerLookupStatus.Exact, ct)) return await Complete(task, job, claim, ct);
        if (!result.Ok && result.ErrorCode is "retailer_access_restricted" or "read_timeout"
            or "tab_closed" or "network_error" or "browser_error")
            return await Fail(task, job, claim, result.ErrorCode, ct);
        if (task.Stage == "search")
        {
            if (!result.Ok) return await Fail(task, job, claim, "search_read_failed", ct);
            if (result.Links is null || result.Links.Length > 5 || result.Links.Length == 0 && !result.EmptyConfirmed)
                throw new UserImportException(400, "Invalid search evidence.");
            var links = result.Links.Select(link => urls.Validate(link)).ToArray();
            if (links.Any(link => !link.IsValid || link.Retailer!.Code != other.Code))
                throw new UserImportException(400, "Search links must be products from the requested retailer.");
            var canonical = links.Select(l => l.ProductUrl!.AbsoluteUri).Distinct().ToArray();
            task.LinksJson = JsonSerializer.Serialize(canonical); task.MatchesJson = "[]";
        }
        else if (task.Stage == "product")
        {
            if (result.Ok && result.Evidence is not null)
            {
                ExtractedShopProduct? candidate = null;
                try { candidate = await ParseAsync(task.Url, result.Evidence, ct); }
                catch (UserImportException) { task.HadFailures = true; }
                var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == job.ProductId, ct);
                if (task.Query is null && candidate is not null
                    && matching.Match(ProductMatchingService.Identity(product), candidate.Identity).Type != MatchType.Exact)
                    return await Fail(task, job, claim, "retailer_identity_changed", ct);
                if (candidate is not null && matching.Match(ProductMatchingService.Identity(product), candidate.Identity).Type == MatchType.Exact)
                {
                    var matches = JsonSerializer.Deserialize<List<UserImportCandidate>>(task.MatchesJson)!;
                    matches.Add(new(task.Url, result.Evidence)); task.MatchesJson = JsonSerializer.Serialize(matches);
                }
            }
            else task.HadFailures = true;
        }
        var remaining = JsonSerializer.Deserialize<List<string>>(task.LinksJson)!;
        if (remaining.Count > 0)
        {
            task.Stage = "product"; task.Url = remaining[0]; remaining.RemoveAt(0);
            task.LinksJson = JsonSerializer.Serialize(remaining);
            return await SaveView(task, job, ct);
        }
        var exact = JsonSerializer.Deserialize<List<UserImportCandidate>>(task.MatchesJson)!;
        if (task.HadFailures) return await Fail(task, job, claim, "candidate_read_failed", ct);
        if (exact.Count == 1)
        {
            var candidate = await ParseAsync(exact[0].Url, exact[0].Evidence, ct);
            var observation = await Observe(account, jobId, exact[0].Url, ct);
            var saved = await comparisons.SaveExactAsync(claim, other.Id, candidate, ct, observation);
            await Outcome(observation, saved ? "Accepted" : "Rejected", ct);
            if (!saved)
                return await Fail(task, job, claim, "comparison_validation_failed", ct);
        }
        else
        {
            // Try the next shorter query when retailer wording produced no exact matches.
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == job.ProductId, ct);
            var searches = ProductSearchQueryBuilder.BuildSearches(ProductMatchingService.Identity(product));
            var index = searches.ToList().IndexOf(task.Query!);
            if (exact.Count == 0 && index >= 0 && index + 1 < searches.Count)
            {
                task.Stage = "search"; task.Query = searches[index + 1]; task.Url = ProductSearchQueryBuilder.SearchUrl(other.Code, task.Query).AbsoluteUri;
                return await SaveView(task, job, ct);
            }
            await comparisons.SaveStatusAsync(claim, other.Id, exact.Count > 1 ? RetailerLookupStatus.Possible : RetailerLookupStatus.NotFound,
                exact.Count > 1 ? "ambiguous_match" : "no_exact_match", ct);
        }
        return await Complete(task, job, claim, ct);
    }, ct);

    private async Task<UserExtensionWork> BeginComparison(UserExtensionImportTask task, ProductImportJob job, ProductImportClaim claim, CancellationToken ct)
    {
        var other = await OtherShop(job, ct);
        var existing = await db.ProductImportRetailerResults.AsNoTracking().SingleOrDefaultAsync(r => r.ProductImportJobId == job.Id && r.ShopId == other.Id, ct);
        if (existing?.Status == RetailerLookupStatus.Exact || await comparisons.TryCacheAsync(claim, other.Id, ct))
            return await Complete(task, job, claim, ct);
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == job.ProductId, ct);
        var known = await db.ShopProducts.AsNoTracking().Where(p => p.ProductId == product.Id && p.ShopId == other.Id
            && p.IsActive && p.MatchType == MatchType.Exact).ToArrayAsync(ct);
        if (known.Length > 1) return await Fail(task, job, claim, "ambiguous_retailer_mapping", ct);
        if (known.Length == 1)
        {
            var link = urls.Validate(known[0].ProductUrl);
            if (!link.IsValid || link.Retailer!.Code != other.Code)
                return await Fail(task, job, claim, "invalid_product_link", ct);
            var code = other.Code == "coles" ? ColesProductParser.ProductCode(link.ProductUrl!) : WoolworthsProductParser.ProductCode(link.ProductUrl!);
            if (known[0].ShopProductCode is { } expected && expected != code)
                return await Fail(task, job, claim, "retailer_identity_changed", ct);
            // A null query identifies a direct refresh rather than a search candidate.
            task.Stage = "product"; task.Status = "Waiting"; task.ErrorCode = null;
            task.Query = null; task.Url = link.ProductUrl!.AbsoluteUri;
            task.LinksJson = "[]"; task.MatchesJson = "[]"; task.HadFailures = false;
            return await SaveView(task, job, ct);
        }
        var query = ProductSearchQueryBuilder.Build(ProductMatchingService.Identity(product));
        if (query is null) return await Fail(task, job, claim, "search_query_unavailable", ct);
        task.Stage = "search"; task.Status = "Waiting"; task.ErrorCode = null;
        task.Query = query; task.Url = ProductSearchQueryBuilder.SearchUrl(other.Code, query).AbsoluteUri;
        return await SaveView(task, job, ct);
    }

    private async Task<UserExtensionWork> Complete(UserExtensionImportTask task, ProductImportJob job, ProductImportClaim claim, CancellationToken ct)
    {
        var unsupported = await db.Shops.Where(s => s.IsActive && s.Code != "coles" && s.Code != "woolworths").Select(s => s.Id).ToArrayAsync(ct);
        foreach (var shop in unsupported) await comparisons.SaveStatusAsync(claim, shop, RetailerLookupStatus.NotSupported, "not_supported", ct);
        if (!await jobs.CompleteSourceStageAsync(claim, ct)) throw new UserImportException(409, "The import changed. Refresh its progress.");
        task.Status = "Completed"; task.Stage = "complete"; task.ErrorCode = null; task.MatchesJson = "[]";
        return await SaveView(task, job, ct);
    }

    private async Task<UserExtensionWork> Fail(UserExtensionImportTask task, ProductImportJob job, ProductImportClaim claim, string code, CancellationToken ct)
    {
        if (job.ShoppingListProductId.HasValue)
        {
            var other = await OtherShop(job, ct);
            await comparisons.SaveStatusAsync(claim, other.Id, RetailerLookupStatus.CheckFailed, code, ct);
            await jobs.CompleteSourceStageAsync(claim, ct);
        }
        else await jobs.FailAsync(claim, code, false, ct);
        task.Status = "Failed"; task.ErrorCode = code; task.MatchesJson = "[]";
        return await SaveView(task, job, ct);
    }

    private Task<Shop> OtherShop(ProductImportJob job, CancellationToken ct) => db.Shops.AsNoTracking()
        .SingleAsync(s => s.IsActive && s.Id != job.SourceShopId && (s.Code == "coles" || s.Code == "woolworths"), ct);

    private async Task<ProductImportClaim> Claim(ProductImportJob job, CancellationToken ct)
    {
        var claim = Guid.NewGuid();
        await db.ProductImportJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, ProductImportJobStatus.Processing).SetProperty(j => j.ClaimToken, claim)
            .SetProperty(j => j.LeaseExpiresDate, Now.AddMinutes(10)).SetProperty(j => j.LastActivityDate, Now)
            .SetProperty(j => j.ProgressStage, ProductImportProgressStage.WaitingForExtension)
            .SetProperty(j => j.CompletedDate, (DateTime?)null).SetProperty(j => j.ErrorCode, (string?)null), ct);
        return new(job.Id, job.UserAccountId, job.ShoppingListId, claim);
    }

    private async Task<ExtractedShopProduct> ParseAsync(string url, ColesEvidence evidence, CancellationToken ct)
    {
        var valid = urls.Validate(url);
        if (!valid.IsValid) throw new UserImportException(400, "Invalid product URL.");
        var html = ColesExtensionTaskService.EvidenceHtml(evidence, valid.Retailer!.Code);
        if (html is null) throw new UserImportException(400, "Invalid product evidence.");
        var page = new RetailerPage(valid.ProductUrl!, html, clock.GetUtcNow());
        var parsed = valid.Retailer.Code == "coles" ? await coles.ParseAsync(page, ct) : await woolworths.ParseAsync(page, ct);
        var code = valid.Retailer.Code == "coles" ? ColesProductParser.ProductCode(valid.ProductUrl!) : WoolworthsProductParser.ProductCode(valid.ProductUrl!);
        if (parsed is not ProviderResult<ExtractedShopProduct>.Success success || success.Value.ShopProductCode != code)
            throw new UserImportException(400, "The page evidence could not identify this product.");
        return success.Value;
    }

    private UserExtensionWork View(UserExtensionImportTask task, ProductImportJob job) => new(job.Id,
        task.Status == "Waiting" && job.ErrorCode == "claim_expired" ? "Failed"
            : task.Status == "Waiting" && job.Status is ProductImportJobStatus.Completed or ProductImportJobStatus.Partial ? "Completed" : task.Status,
        job.ShoppingListProductId.HasValue, task.Stage, task.StepToken, task.Status == "Waiting" ? task.Url : null,
        task.Query, task.ErrorCode ?? job.ErrorCode);

    private async Task<UserExtensionWork> SaveView(UserExtensionImportTask task, ProductImportJob job, CancellationToken ct)
    {
        task.StepToken = Guid.NewGuid(); task.UpdatedDate = Now;
        db.UserExtensionImportTasks.Update(task); await db.SaveChangesAsync(ct); db.ChangeTracker.Clear();
        var updated = await db.ProductImportJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, ct);
        return View(task, updated);
    }

    private async Task<UserExtensionWork> Locked(long account, long id, Func<UserExtensionImportTask, ProductImportJob, Task<UserExtensionWork>> action, CancellationToken ct)
    {
        // A session lock spans the existing persistence services' independent transactions.
        // It serializes only this import and is always released before returning the connection.
        await db.Database.OpenConnectionAsync(ct);
        var key = "user-extension-import:" + id;
        var locked = false;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({key}, 0))", ct); locked = true;
            var now = Now;
            var task = await db.UserExtensionImportTasks.AsNoTracking().SingleOrDefaultAsync(t => t.ProductImportJobId == id
                && t.ProductImportJob.UserAccountId == account && t.ProductImportJob.UserAccount.IsActive
                && (!t.ProductImportJob.UserAccount.IsTrial || t.ProductImportJob.UserAccount.ExpiresDate > now)
                && !t.ProductImportJob.ShoppingList.IsArchived && (t.ProductImportJob.ShoppingList.ExpiresDate == null || t.ProductImportJob.ShoppingList.ExpiresDate > now), ct);
            if (task is null) throw new UserImportException(404, "The import was not found.");
            var job = await db.ProductImportJobs.AsNoTracking().SingleAsync(j => j.Id == id, ct);
            return await action(task, job);
        }
        finally
        {
            db.ChangeTracker.Clear();
            try { if (locked) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({key}, 0))", CancellationToken.None); }
            finally { await db.Database.CloseConnectionAsync(); }
        }
    }
}
