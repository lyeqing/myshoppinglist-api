using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Mcp;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class ResearchService(MyShoppingListDbContext db, TimeProvider clock, ResearchEvidenceValidator validator,
    ProductUrlValidator urls, ProductMatchingService matching, ProductService products, ShopProductService mappings,
    ProductLibraryService library, McpMutationCoordinator commands, IOptions<ResearchOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] IssueCodes = ["retailer_inaccessible", "price_unavailable", "price_unverified",
        "identity_ambiguous", "missing_unit_information", "promotion_incomplete", "customer_selection_required"];

    private async Task Account(long account, CancellationToken ct)
    {
        if (!await db.UserAccounts.AnyAsync(a => a.Id == account && a.IsActive && !a.IsTrial, ct))
            throw new UserImportException(403, "Sign in with an active registered account to use research.");
    }

    public async Task<ResearchJobSummary[]> JobsAsync(long account, CancellationToken ct)
    {
        await Account(account, ct);
        return await db.ResearchJobs.AsNoTracking().Where(j => j.UserAccountId == account).OrderByDescending(j => j.CreatedAt)
            .Take(50).Select(j => new ResearchJobSummary(j.Id, j.Query, j.Status, j.CreatedAt,
                db.ResearchIssues.Count(i => i.ResearchJobId == j.Id && i.UserAccountId == account && i.Status == "Open"))).ToArrayAsync(ct);
    }

    public Task<McpMutationResult> SubmitAsync(long account, ResearchSubmission request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 500
            || request.WorkflowVersion != ResearchWorkflow.Version || request.Candidates is null || request.Candidates.Length > 20
            || request.Issues?.Length > 20 || request.Candidates.Length == 0 && (request.Issues?.Length ?? 0) == 0
            || request.Candidates.Any(r => r is null || string.IsNullOrWhiteSpace(r.Key) || r.Key.Length > 80
                || string.IsNullOrWhiteSpace(r.Retailer) || r.Retailer.Length > 40 || r.Url is null
                || JsonSerializer.SerializeToUtf8Bytes(r, Json).Length > 10000)
            || request.Candidates.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Length
            || JsonSerializer.SerializeToUtf8Bytes(request, Json).Length > 60000)
            throw new UserImportException(400, "Use the current workflow version and at most 20 uniquely keyed candidates/issues within 60 KB.");
        return commands.ExecuteAsync(account, request.RequestId, "submit_product_research", request, async () =>
        {
            await Account(account, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var job = new ResearchJob { Id = Guid.NewGuid(), UserAccountId = account, Query = request.Query.Trim(),
                WorkflowVersion = request.WorkflowVersion, CreatedAt = now };
            db.ResearchJobs.Add(job);
            var issues = new Dictionary<(string, string, string), ResearchIssue>();
            void Issue(string retailer, string code, string detail, string target = "") => issues.TryAdd((retailer, code, target), new()
            { Id = Guid.NewGuid(), UserAccountId = account, ResearchJobId = job.Id, Retailer = retailer,
                Code = code, Detail = detail, Target = target });
            var accepted = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in request.Candidates)
            {
                var errors = validator.Validate(input).ToList();
                var shop = await db.Shops.AsNoTracking().SingleOrDefaultAsync(s => s.Code == input.Retailer && s.IsActive, ct);
                if (shop is null || !await ShoppingPreferencesService.AllowsAsync(db, account, shop.Id, ct)) errors.Add("retailer_not_enabled");
                if (input.ShopLocationId is { } location && !await db.ShopLocations.AnyAsync(l => l.Id == location && l.IsActive && l.ShopId == (shop == null ? 0 : shop.Id), ct))
                    errors.Add("invalid_location");
                if (input.RelatedProductId is { } related)
                {
                    var original = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == related && p.UserAccountId == account && !p.IsDeleted, ct);
                    if (original is null) errors.Add("related_product_unavailable");
                    else if (input.Identity is not null && errors.Count == 0)
                    {
                        var identity = ProductMatchingService.Identity(original);
                        if (input.Match == "exact" && matching.Match(identity, input.Identity).Type != MatchType.Exact) errors.Add("exact_match_not_supported");
                        if (input.Match == "different_package" && !new ProductFamilyMatchingService().IsDifferentSize(identity, input.Identity)) errors.Add("package_match_not_supported");
                        if (input.Match == "alternative" && !new BrandSuggestionMatchingService().IsSuggestion(identity, input.Identity)) errors.Add("alternative_not_supported");
                    }
                }
                var validUrl = urls.Validate(input.Url);
                var report = validUrl.IsValid ? input with { Url = new UriBuilder(validUrl.ProductUrl!) { Query = "", Fragment = "" }.Uri.AbsoluteUri } : input;
                if (!seen.Add(report.Url)) errors.Add("duplicate_candidate");
                var acceptedCandidate = errors.Count == 0;
                if (acceptedCandidate) accepted++;
                db.ResearchCandidates.Add(new() { Id = Guid.NewGuid(), UserAccountId = account, ResearchJobId = job.Id,
                    Key = input.Key, ReportJson = JsonSerializer.Serialize(report, Json), Validation = acceptedCandidate ? "AcceptedUnverified" : "Rejected",
                    IssuesJson = JsonSerializer.Serialize(errors, Json) });
                Issue(input.Retailer, acceptedCandidate ? input.Price.HasValue ? "price_unverified" : "price_unavailable" : "candidate_rejected",
                    acceptedCandidate ? input.Price.HasValue
                        ? "Review the source and customer choice. AI-reported prices are not verified and are excluded from basket totals."
                        : "No price was supplied. You can select the product without a price or research the retailer again."
                        : "Candidate rejected: " + string.Join(", ", errors), input.Key);
                if (acceptedCandidate && input.Price.HasValue && ResearchEvidenceValidator.UnitPrice(input).Value is null)
                    Issue(input.Retailer, "missing_unit_information", "Package evidence is insufficient for a comparable unit price. Check the source; do not infer the missing quantity.", input.Key);
            }
            foreach (var issue in request.Issues ?? [])
            {
                if (issue is null || issue.Retailer is not ("coles" or "woolworths" or "aldi" or "iga" or "foodland")
                    || !IssueCodes.Contains(issue.Code) || string.IsNullOrWhiteSpace(issue.Detail) || issue.Detail.Length > 1000)
                    throw new UserImportException(400, "Use a supported retailer, issue code and a short explanation.");
                Issue(issue.Retailer, issue.Code, issue.Detail);
            }
            job.Status = accepted == 0 ? "Failed" : accepted < request.Candidates.Length || request.Issues?.Length > 0 ? "PartiallyCompleted" : "AwaitingSelection";
            db.ResearchIssues.AddRange(issues.Values);
            await db.SaveChangesAsync(ct);
            return new(1, "submitted", request.RequestId.ToString(), false, ResearchJobId: job.Id.ToString());
        }, ct);
    }

    public async Task<ResearchReview> ReviewAsync(long account, Guid id, CancellationToken ct)
    {
        await Account(account, ct);
        var job = await db.ResearchJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id && j.UserAccountId == account, ct)
            ?? throw new UserImportException(404, "Research was not found.");
        var candidates = await db.ResearchCandidates.AsNoTracking().Where(c => c.ResearchJobId == id && c.UserAccountId == account).OrderBy(c => c.Key).ToArrayAsync(ct);
        var issues = await db.ResearchIssues.AsNoTracking().Where(i => i.ResearchJobId == id && i.UserAccountId == account).OrderBy(i => i.Retailer).ThenBy(i => i.Code).ToArrayAsync(ct);
        return new(job.Id, job.Query, job.WorkflowVersion, job.Status, job.CreatedAt, candidates.Select(c =>
        {
            var report = JsonSerializer.Deserialize<ResearchCandidateInput>(c.ReportJson, Json)!;
            var unit = c.Validation == "AcceptedUnverified" ? ResearchEvidenceValidator.UnitPrice(report) : (null, (string?)null);
            return new ResearchCandidateView(c.Id, report, c.Validation, JsonSerializer.Deserialize<string[]>(c.IssuesJson, Json)!,
                "Unverified AI report", unit.Item1, unit.Item2, c.SelectedProductId);
        }).ToArray(), issues.Select(i => new ResearchIssueView(i.Id, i.Retailer, i.Code, i.Detail, i.Status, i.Revision, i.Resolution, i.Target)).ToArray());
    }

    public Task<McpMutationResult> SelectAsync(long account, ResearchSelection request, CancellationToken ct) =>
        commands.ExecuteAsync(account, request.RequestId, "select_research_candidate", request, async () =>
        {
            if (request.RequestId == Guid.Empty || request.CandidateId == Guid.Empty || request.Quantity is < 1 or > 10000)
                throw new UserImportException(400, "Choose a reviewed candidate, list and a package quantity between 1 and 10000.");
            await ProductService.LockCatalogueAsync(db, ct);
            await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {account} FOR UPDATE""").SingleAsync(ct);
            await Account(account, ct);
            if (!await db.ShoppingLists.AnyAsync(l => l.Id == request.ListId && l.UserAccountId == account && !l.IsArchived
                && (l.ExpiresDate == null || l.ExpiresDate > clock.GetUtcNow().UtcDateTime), ct)) throw new UserImportException(404, "Choose an active shopping list.");
            var candidate = await db.ResearchCandidates.SingleOrDefaultAsync(c => c.Id == request.CandidateId && c.UserAccountId == account, ct)
                ?? throw new UserImportException(404, "Candidate was not found.");
            if (candidate.Validation != "AcceptedUnverified") throw new UserImportException(400, "Choose a candidate that passed validation.");
            var report = JsonSerializer.Deserialize<ResearchCandidateInput>(candidate.ReportJson, Json)!;
            if (validator.Validate(report).Length > 0) throw new UserImportException(400, "This candidate is no longer usable. Research it again.");
            var shop = await db.Shops.SingleAsync(s => s.Code == report.Retailer && s.IsActive, ct);
            if (!await ShoppingPreferencesService.AllowsAsync(db, account, shop.Id, ct)) throw new UserImportException(409, "This retailer is no longer enabled.");
            var source = new ExtractedShopProduct { ShopCode = report.Retailer, ProductUrl = new(report.Url), Identity = report.Identity,
                ShopProductCode = ResearchEvidenceValidator.ProductCode(report.Retailer, new(report.Url)),
                SourceType = SourceType.AiResearch, CheckedDate = report.ObservedAt };
            var known = await mappings.FindPersonalAsync(account, shop.Id, source, ct);
            if (known.Count > 1) throw new UserImportException(409, "This retailer identity needs review before saving.");
            var resolved = await products.ResolvePersonalAsync(account, source, known.SingleOrDefault(), clock.GetUtcNow().UtcDateTime, ct, enrichExisting: false);
            if (resolved.Product is null) throw new UserImportException(409, "Product identity conflicts with a saved product. Research it again.");
            var normalisation = new ProductNormalisationService();
            if (source.Identity.Brand is { } brand && resolved.Product.Brand is { } savedBrand
                && normalisation.Text(brand) != normalisation.Text(savedBrand)
                || known.Count > 0 && matching.Match(source.Identity, ProductMatchingService.Identity(resolved.Product)).Type == MatchType.NoMatch
                    && normalisation.Text(source.Identity.Name) != normalisation.Text(resolved.Product.Name))
                throw new UserImportException(409, "The reported identity disagrees with your saved product. Review the source before choosing it.");
            await db.SaveChangesAsync(ct);
            // Never let unverified AI evidence overwrite an existing retailer observation/mapping.
            if (known.Count == 0)
            {
                var mapping = mappings.Save(shop, resolved.Product, null, source, 0);
                mapping.MatchType = MatchType.UserConfirmed;
                mapping.LastCheckedDate = null;
            }
            candidate.SelectedProductId = resolved.Product.Id;
            var job = await db.ResearchJobs.SingleAsync(j => j.Id == candidate.ResearchJobId && j.UserAccountId == account, ct);
            job.Status = "Selected";
            await db.SaveChangesAsync(ct);
            var added = await library.AddOwnedAsync(account, resolved.Product.Id, request.ListId, request.Quantity, ct);
            var item = await db.ShoppingListProducts.AsNoTracking().SingleAsync(i => i.Id == added.ItemId, ct);
            return new(1, added.AlreadyOnList ? "already_on_list" : "added", request.RequestId.ToString(), false,
                request.ListId.ToString(CultureInfo.InvariantCulture), added.ItemId.ToString(CultureInfo.InvariantCulture), item.UpdatedDate);
        }, ct);

    public Task<McpMutationResult> ResolveAsync(long account, Guid id, ResearchIssueUpdate request, CancellationToken ct) =>
        commands.ExecuteAsync(account, request.RequestId, "resolve_research_issue", new { id, request }, async () =>
        {
            await Account(account, ct);
            if (request.RequestId == Guid.Empty || request.Status is not ("Open" or "Resolved" or "CannotVerify")
                || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000)
                throw new UserImportException(400, "Choose an issue status and explain the action or remaining limitation.");
            var issue = await db.ResearchIssues.SingleOrDefaultAsync(i => i.Id == id && i.UserAccountId == account, ct)
                ?? throw new UserImportException(404, "Issue was not found.");
            if (issue.Revision != request.ExpectedRevision) throw new UserImportException(409, "This issue changed. Reload before updating.");
            // Acknowledgement closes workflow issues; it never upgrades evidence or modifies prices.
            issue.Status = request.Status; issue.Resolution = request.Reason; issue.Revision++;
            await db.SaveChangesAsync(ct);
            return new(1, "issue_updated", request.RequestId.ToString(), false);
        }, ct);

    public async Task<ResearchRefreshTask[]> RefreshTasksAsync(long account, long listId, int pageSize, CancellationToken ct)
    {
        await Account(account, ct);
        if (pageSize is < 1 or > 50) throw new UserImportException(400, "Choose between 1 and 50 tasks.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.ShoppingLists.AnyAsync(l => l.Id == listId && l.UserAccountId == account && !l.IsArchived
            && (l.ExpiresDate == null || l.ExpiresDate > now), ct)) throw new UserImportException(404, "Choose an active shopping list.");
        var allowed = ShoppingPreferencesService.AllowedShops(db, account).Select(s => s.Id);
        var query = db.ShopProducts.AsNoTracking().Include(m => m.Product).Include(m => m.Shop).Where(m => m.UserAccountId == account
            && m.Product.UserAccountId == account && m.IsActive && m.Shop.IsActive && !m.Product.IsDeleted && allowed.Contains(m.ShopId)
            && db.ShoppingListProducts.Any(i => i.ShoppingListId == listId && i.ProductId == m.ProductId && !i.IsHidden && !i.IsPurchased));
        var output = new List<ResearchRefreshTask>();
        // Read bounded pages instead of loading an unbounded catalogue. No claims or network work.
        long cursor = 0;
        while (output.Count < pageSize)
        {
            var rows = await query.Where(m => m.Id > cursor).OrderBy(m => m.Id).Take(100).ToArrayAsync(ct);
            if (rows.Length == 0) break;
            var ids = rows.Select(m => m.Id).ToArray();
            var prices = await db.ShopProductPrices.AsNoTracking().Where(p => ids.Contains(p.ShopProductId) && p.UserAccountId == account).ToArrayAsync(ct);
            foreach (var m in rows)
            {
                cursor = m.Id;
                var observations = prices.Where(p => p.ShopProductId == m.Id).ToArray();
                foreach (var p in observations.Length == 0 ? new ShopProductPrice?[] { null } : observations)
                {
                    var due = p is null ? (DateTime?)null : p.CheckedDate.AddHours(options.Value.ActivePriceHours);
                    if (p?.SpecialEndDate is { } end && end < due) due = end;
                    if (p?.SpecialStartDate is { } start && start > p.CheckedDate && start < due) due = start;
                    if (p is not null && p.CheckedDate <= now && due > now) continue;
                    var url = urls.Validate(m.ProductUrl);
                    if (!url.IsValid || ResearchEvidenceValidator.ProductCode(m.Shop.Code, url.ProductUrl!) is null) continue;
                    var safeUrl = new UriBuilder(url.ProductUrl!) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
                    var recentFailure = await db.ResearchIssues.AnyAsync(i => i.UserAccountId == account && i.Retailer == m.Shop.Code
                        && i.Status == "Open" && i.Code == "retailer_inaccessible"
                        && db.ResearchJobs.Any(j => j.Id == i.ResearchJobId && j.CreatedAt > now.AddHours(-options.Value.RetryHours)), ct);
                    if (recentFailure) continue;
                    output.Add(new(m.Id.ToString(CultureInfo.InvariantCulture), m.ProductId.ToString(CultureInfo.InvariantCulture), m.Product.Name,
                        m.Shop.Code, safeUrl, p is null ? "missing_price" : "stale_or_promotional_price", p?.CheckedDate, due, p?.Price,
                        p?.Currency ?? "AUD", p?.PriceScope.ToString() ?? "Unknown", p?.ShopLocationId?.ToString(CultureInfo.InvariantCulture)));
                    if (output.Count == pageSize) return output.ToArray();
                }
            }
        }
        return output.ToArray();
    }
}
