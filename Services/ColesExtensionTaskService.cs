using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Http;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Services;

public sealed class ColesWorkPendingException : Exception;
public sealed class ColesWorkFailedException(long taskId) : Exception { public long TaskId { get; } = taskId; }
public interface IColesExtensionQueue
{
    Task<ProviderResult<ExtractedShopProduct>> ProductAsync(Uri url, CancellationToken token);
    Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(string query, CancellationToken token);
}

public sealed class ColesExtensionTaskService(MyShoppingListDbContext db, SourceProductCacheService cache,
    CatalogueFreshnessService freshness, ColesProductParser parser, ProductService products,
    ShopProductService mappings, PriceService prices, TimeProvider clock) : IColesExtensionQueue
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string ProductKey(Uri url) => "product:" + (ColesProductParser.ProductCode(url) ?? throw new ArgumentException("Invalid product URL"));
    public static bool ValidWorker(string? value) => value is { Length: >= 1 and <= 100 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private async Task<ColesExtensionTask> EnsureAsync(string kind, string key, Uri url, string? query, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await ProductService.LockCatalogueAsync(db, token);
        var task = await db.ColesExtensionTasks.SingleOrDefaultAsync(t => t.Key == key, token);
        if (task is null)
        {
            task = new() { Key = key, Kind = kind, Url = url.AbsoluteUri, Query = query, CreatedAt = Now };
            db.Add(task);
        }
        else if (task.Status == "Completed")
        {
            var stale = !freshness.IsFresh(task.ClaimedAt, clock.GetUtcNow());
            if (!stale && kind == "product")
            {
                var parsed = await parser.ParseAsync(new(url, task.ResultJson!, new(task.ClaimedAt!.Value, TimeSpan.Zero)), token);
                if (parsed is ProviderResult<ExtractedShopProduct>.Success product
                    && product.Value.Offer is ProviderResult<ShopProductOffer>.Success offer)
                    stale = offer.Value.SpecialEndDate <= Now || offer.Value.SpecialStartDate > Now;
            }
            if (stale) Reset(task);
        }
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); db.ChangeTracker.Clear();
        return task;
    }
    private static void Reset(ColesExtensionTask task)
    {
        task.Status = "Waiting"; task.Attempts = 0; task.NextAttemptAt = null; task.ErrorCode = null;
        task.ClaimToken = null; task.ClaimedAt = null; task.LeaseExpiresAt = null; task.WorkerId = null;
        task.ResultJson = null; task.SubmissionHash = null; task.CompletedAt = null;
    }
    public async Task<ProviderResult<ExtractedShopProduct>> ProductAsync(Uri url, CancellationToken token)
    {
        var cached = await cache.FindAsync("coles", url, token);
        if (cached is not null) return new ProviderResult<ExtractedShopProduct>.Success(cached);
        var canonical = new Uri("https://www.coles.com.au/product/" + ColesProductParser.ProductCode(url));
        var task = await EnsureAsync("product", ProductKey(url), canonical, null, token);
        if (task.Status == "Failed") throw new ColesWorkFailedException(task.Id);
        if (task.Status != "Completed") throw new ColesWorkPendingException();
        // A product with no price can still be added with an honest unavailable offer.
        return await parser.ParseAsync(new(canonical, task.ResultJson!, new(task.ClaimedAt!.Value, TimeSpan.Zero)), token);
    }
    public async Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(string query, CancellationToken token)
    {
        var task = await EnsureAsync("search", "search:" + Hash(query.ToLowerInvariant()),
            ProductSearchQueryBuilder.SearchUrl("coles", query), query, token);
        if (task.Status == "Failed") throw new ColesWorkFailedException(task.Id);
        if (task.Status != "Completed") throw new ColesWorkPendingException();
        var links = JsonSerializer.Deserialize<string[]>(task.ResultJson!)!;
        var candidates = new List<ShopProductSearchResult>(); var pending = false;
        foreach (var link in links)
        {
            try
            {
                var product = await ProductAsync(new(link), token);
                if (product is ProviderResult<ExtractedShopProduct>.Success success) candidates.Add(new(success.Value));
                else return new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure(((ProviderResult<ExtractedShopProduct>.Failure)product).Error);
            }
            catch (ColesWorkPendingException) { pending = true; }
        }
        if (pending) throw new ColesWorkPendingException();
        return new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success(candidates);
    }
    private async Task RecoverAsync(CancellationToken token)
    {
        var now = Now;
        await db.ColesExtensionTasks.Where(t => t.Status == "Processing" && t.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, t => t.Attempts < 3 ? "Waiting" : "Failed")
                .SetProperty(t => t.NextAttemptAt, now.AddSeconds(30)).SetProperty(t => t.ErrorCode, "worker_interrupted")
                .SetProperty(t => t.LeaseExpiresAt, (DateTime?)null), token);
    }
    public async Task<IReadOnlyList<ColesTaskSummary>> ListAsync(CancellationToken token)
    {
        await RecoverAsync(token);
        var now = Now;
        return await db.ColesExtensionTasks.AsNoTracking().Where(t => t.Status == "Waiting" && (t.NextAttemptAt == null || t.NextAttemptAt <= now)
            || t.Status == "Failed").OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new ColesTaskSummary(t.Id, t.Kind, t.Url, t.Query, t.Status, t.Attempts, t.ErrorCode)).ToListAsync(token);
    }
    public async Task<ColesTaskClaim?> ClaimAsync(long id, string worker, CancellationToken token)
    {
        if (!ValidWorker(worker)) return null;
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await ProductService.LockCatalogueAsync(db, token);
        await RecoverAsync(token);
        var now = Now;
        var active = await db.ColesExtensionTasks.SingleOrDefaultAsync(t => t.Status == "Processing" && t.WorkerId == worker, token);
        if (active is not null) return active.Id == id ? Claim(active) : null;
        var task = await db.ColesExtensionTasks.SingleOrDefaultAsync(t => t.Id == id, token);
        if (task is null || task.Status != "Waiting" || task.Attempts >= 3 || task.NextAttemptAt > now) return null;
        task.Status = "Processing"; task.WorkerId = worker; task.ClaimToken = Guid.NewGuid();
        task.ClaimedAt = now; task.LeaseExpiresAt = now.AddMinutes(3); task.Attempts++;
        task.ErrorCode = null; task.SubmissionHash = null;
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); db.ChangeTracker.Clear();
        return Claim(task);
    }
    private static ColesTaskClaim Claim(ColesExtensionTask task) => new(task.Id, task.Kind, task.Url, task.Query, task.ClaimToken!.Value, task.LeaseExpiresAt!.Value);

    public async Task<string?> SubmitAsync(long id, ColesTaskSubmission submission, CancellationToken token)
    {
        if (!ValidWorker(submission.WorkerId) || submission.ClaimToken == Guid.Empty) return "invalid_submission";
        var digest = Hash(JsonSerializer.Serialize(submission));
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await ProductService.LockCatalogueAsync(db, token);
        var task = await db.ColesExtensionTasks.SingleOrDefaultAsync(t => t.Id == id, token);
        if (task is null) return "task_not_found";
        if (task.WorkerId != submission.WorkerId || task.ClaimToken != submission.ClaimToken) return "claim_lost";
        if (task.SubmissionHash is not null) return task.SubmissionHash == digest ? null : "submission_conflict";
        if (task.Status != "Processing" || task.LeaseExpiresAt <= Now) return "claim_lost";
        var leaseExpiresAt = task.LeaseExpiresAt;
        if (!submission.Ok)
        {
            var retryable = submission.ErrorCode is "read_timeout" or "browser_error" or "tab_closed" or "network_error";
            task.ErrorCode = submission.ErrorCode is "retailer_access_restricted" ? "retailer_access_restricted" : retryable ? submission.ErrorCode : "extraction_failed";
            task.Status = retryable && task.Attempts < 3 ? "Waiting" : "Failed";
            task.NextAttemptAt = Now.AddSeconds(30 * task.Attempts); task.LeaseExpiresAt = null;
        }
        else if (task.Kind == "search")
        {
            if (submission.Url != task.Url || submission.Links is null || submission.Links.Length > 5
                || submission.Links.Length == 0 && !submission.EmptyConfirmed) return "invalid_search_result";
            var links = new List<string>();
            foreach (var link in submission.Links)
            {
                if (link is null || link.Length > 2048 || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || ColesProductParser.ProductCode(uri) is not { } code)
                    return "invalid_product_link";
                links.Add("https://www.coles.com.au/product/" + code);
            }
            task.ResultJson = JsonSerializer.Serialize(links.Distinct().ToArray());
            task.Status = "Completed"; task.CompletedAt = Now; task.LeaseExpiresAt = null;
        }
        else
        {
            if (!Uri.TryCreate(submission.Url, UriKind.Absolute, out var uri) || ColesProductParser.ProductCode(uri) is not { } code
                || ProductKey(uri) != task.Key) return "product_identity_conflict";
            var html = EvidenceHtml(submission.Evidence);
            if (html is null) return "invalid_product_evidence";
            var parsed = await parser.ParseAsync(new(uri, html, new(task.ClaimedAt!.Value, TimeSpan.Zero)), token);
            if (parsed is not ProviderResult<ExtractedShopProduct>.Success success || success.Value.ShopProductCode != code) return "invalid_product_evidence";
            var source = success.Value;
            if (!SourceProductPersistenceService.ValidSource(source, new(new RetailerCatalog()), new(), clock))
                return "invalid_product_evidence";
            var shop = await db.Shops.SingleAsync(s => s.Code == "coles" && s.IsActive, token);
            var known = await mappings.FindAsync(shop.Id, source, token);
            if (known.Count > 1) return "ambiguous_retailer_mapping";
            var resolution = await products.ResolveAsync(source, known.SingleOrDefault(), Now, token);
            if (resolution.Status != ProductPersistenceStatus.Success) return resolution.ErrorCode ?? "identity_conflict";
            var mapping = mappings.Save(shop, resolution.Product!, known.SingleOrDefault(), source, resolution.MatchConfidence);
            await db.SaveChangesAsync(token);
            // A newer unavailable observation must not leave an older anonymous offer looking current.
            if (source.Offer is not ProviderResult<ShopProductOffer>.Success)
                await db.ShopProductPrices.Where(p => p.ShopProductId == mapping.Id && p.ShopLocationId == null
                    && p.CheckedDate <= task.ClaimedAt).ExecuteDeleteAsync(token);
            if (source.Offer is ProviderResult<ShopProductOffer>.Success offer)
            {
                var saved = await prices.SaveAsync(mapping, offer.Value, token);
                if (saved.Status == PriceUpdateStatus.InvalidOffer) return saved.ErrorCode ?? "invalid_offer";
            }
            task.ResultJson = html; task.Status = "Completed"; task.CompletedAt = Now; task.LeaseExpiresAt = null;
        }
        task.SubmissionHash = digest;
        await db.SaveChangesAsync(token);
        if (leaseExpiresAt <= Now) return "claim_lost";
        await tx.CommitAsync(token); db.ChangeTracker.Clear();
        return null;
    }
    public static string? EvidenceHtml(ColesEvidence? evidence)
    {
        if (evidence is null || evidence.JsonLd?.Length > 20) return null;
        var size = evidence.NextProductJson?.Length ?? 0;
        size += evidence.JsonLd?.Sum(s => s?.Length ?? 0) ?? 0;
        if (size > 2000000) return null;
        try
        {
            var html = new StringBuilder();
            if (evidence.NextProductJson is { } next)
            {
                using var doc = JsonDocument.Parse(next, new JsonDocumentOptions { MaxDepth = 64 });
                var state = JsonSerializer.Serialize(new { props = new { pageProps = new { product = doc.RootElement } } });
                html.Append("<script id='__NEXT_DATA__' type='application/json'>").Append(state.Replace("<", "\\u003c")).Append("</script>");
            }
            foreach (var ld in evidence.JsonLd ?? [])
            {
                if (ld is null) return null;
                using var doc = JsonDocument.Parse(ld, new JsonDocumentOptions { MaxDepth = 64 });
                html.Append("<script type='application/ld+json'>").Append(doc.RootElement.GetRawText().Replace("<", "\\u003c")).Append("</script>");
            }
            return html.ToString();
        }
        catch (JsonException) { return null; }
    }
    public async Task<bool> RetryAsync(long id, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await ProductService.LockCatalogueAsync(db, token);
        var task = await db.ColesExtensionTasks.SingleOrDefaultAsync(t => t.Id == id && t.Status == "Failed", token);
        if (task is null) return false;
        Reset(task); await db.SaveChangesAsync(token);
        var code = "extension_task_failed_" + id;
        var jobs = db.ProductImportJobs.Where(j => j.ErrorCode == code && (j.Status == ProductImportJobStatus.Failed || j.Status == ProductImportJobStatus.Partial));
        var ids = await jobs.Select(j => j.Id).ToListAsync(token);
        await jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, ProductImportJobStatus.Queued)
            .SetProperty(j => j.ProgressStage, ProductImportProgressStage.Queued).SetProperty(j => j.AttemptCount, 0)
            .SetProperty(j => j.NextAttemptDate, (DateTime?)null).SetProperty(j => j.CompletedDate, (DateTime?)null)
            .SetProperty(j => j.ErrorCode, (string?)null), token);
        await db.ProductImportRetailerResults.Where(r => ids.Contains(r.ProductImportJobId) && r.Shop.Code == "coles")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RetailerLookupStatus.Pending).SetProperty(r => r.ErrorCode, (string?)null), token);
        await tx.CommitAsync(token); db.ChangeTracker.Clear(); return true;
    }
}
