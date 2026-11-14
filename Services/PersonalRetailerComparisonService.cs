using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Services;

public sealed class PersonalRetailerComparisonService(MyShoppingListDbContext db, ExtensionProductEvidenceParser parser,
    ProductMatchingService matching, ShopProductService mappings, PriceService prices, ProductUrlValidator urls,
    TimeProvider clock, IOptions<UserExtensionOptions> options, ILogger<PersonalRetailerComparisonService> logger)
{
    internal const string LogSource = "myshoppinglist_api.Services.PersonalRetailerComparisonService";
    public sealed record RejectedCandidate(string Url, string Reason);
    public sealed record PageFailure(string Url, string Kind, string ErrorCode);
    public sealed class RunState
    {
        public string ProductName { get; set; } = "";
        public Guid StepToken { get; set; } = Guid.NewGuid();
        public Guid? LastStepToken { get; set; }
        public List<RetailerState> Retailers { get; set; } = [];
    }
    public sealed class RetailerState
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "Pending";
        public string? Message { get; set; }
        public List<string> Queries { get; set; } = [];
        public int QueryIndex { get; set; }
        public List<string> Links { get; set; } = [];
        public List<string> Seen { get; set; } = [];
        public string? Url { get; set; }
        public string Kind { get; set; } = "search";
        public bool HadFailures { get; set; }
        public List<RejectedCandidate> RejectedCandidates { get; set; } = [];
        public List<PageFailure> PageFailures { get; set; } = [];
        public List<PersonalComparisonCandidate> ExactMatches { get; set; } = [];
        public List<PersonalComparisonCandidate> Alternatives { get; set; } = [];
    }
    private static RunState Decode(PersonalRetailerComparison run) => JsonSerializer.Deserialize<RunState>(run.StateJson)!;
    private static bool Active(RetailerState r) => r.Status is "Pending" or "Checking" or "NeedsVerification";
    private static PersonalComparisonResponse Response(PersonalRetailerComparison run, RunState state, long itemId, bool work = true)
    {
        var next = state.Retailers.FirstOrDefault(Active);
        var status = next?.Status == "NeedsVerification" ? "Paused" : next is not null ? "Checking"
            : state.Retailers.Any(r => r.Status == "Cancelled") ? "Cancelled"
            : state.Retailers.Any(r => r.Status is "UnableToCheck" or "Partial") ? "Partial" : "Completed";
        return new(run.Id, itemId, run.CaptureJobId, status, state.Retailers.Select(r => new PersonalComparisonRetailer(r.Code, r.Name,
            r.Status, r.Message, r.ExactMatches.ToArray(), r.Alternatives.ToArray())).ToArray(),
            work && next?.Url is not null ? new(state.StepToken, next.Code, next.Kind, next.Url) : null, state.ProductName);
    }
    private async Task<ProductImportJob> JobAsync(long accountId, long jobId, CancellationToken ct)
    {
        if (!options.Value.PersonalCapturesEnabled) throw new UserImportException(503, "Personal comparisons are not enabled.");
        var now = clock.GetUtcNow().UtcDateTime;
        var job = await db.ProductImportJobs.Include(j => j.Product).Include(j => j.SourceShop)
            .SingleOrDefaultAsync(j => j.Id == jobId && j.UserAccountId == accountId && j.CaptureRequestId != null
                && j.Status == ProductImportJobStatus.Completed && j.Product!.UserAccountId == accountId
                && !j.Product.IsDeleted && j.UserAccount.IsActive && (!j.UserAccount.IsTrial || j.UserAccount.ExpiresDate > now)
                && j.ShoppingList.UserAccountId == accountId && !j.ShoppingList.IsArchived
                && (j.ShoppingList.ExpiresDate == null || j.ShoppingList.ExpiresDate > now), ct)
            ?? throw new UserImportException(404, "The saved product is no longer available for comparisons.");
        if (job.SourceShop?.Code is not ("coles" or "woolworths" or "aldi") || job.ShoppingListProductId is null
            || !await db.ShoppingListProducts.AnyAsync(i => i.Id == job.ShoppingListProductId && i.ShoppingListId == job.ShoppingListId
                && i.ProductId == job.ProductId && !i.IsPurchased && !i.IsHidden, ct))
            throw new UserImportException(409, "This saved item is no longer eligible for comparisons.");
        return job;
    }
    private async Task LockAsync(long accountId, CancellationToken ct)
    {
        await ProductService.LockCatalogueAsync(db, ct);
        await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").LoadAsync(ct);
    }
    public async Task<PersonalComparisonResponse> StartAsync(long accountId, long jobId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(accountId, ct);
        var job = await JobAsync(accountId, jobId, ct);
        var run = await db.PersonalRetailerComparisons.SingleOrDefaultAsync(r => r.CaptureJobId == jobId && r.UserAccountId == accountId, ct);
        if (run is not null) return Response(run, Decode(run), job.ShoppingListProductId!.Value);
        var identity = ProductMatchingService.Identity(job.Product!);
        var queries = ProductSearchQueryBuilder.BuildComparisonSearches(identity).ToList();
        run = new() { UserAccountId = accountId, CaptureJobId = job.Id,
            CreatedDate = clock.GetUtcNow().UtcDateTime, UpdatedDate = clock.GetUtcNow().UtcDateTime };
        List<RetailerState> retailers = [
            new() { Code = "coles", Name = "Coles", Queries = [.. queries] },
            new() { Code = "woolworths", Name = "Woolworths", Queries = [.. queries] },
            new() { Code = "aldi", Name = "ALDI", Queries = [.. queries] }];
        var state = new RunState { ProductName = job.Product!.Name,
            Retailers = retailers.Where(r => r.Code != job.SourceShop!.Code).ToList() };
        Advance(state);
        run.StateJson = JsonSerializer.Serialize(state);
        db.PersonalRetailerComparisons.Add(run); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        LogOutcomes(logger, run, job, state, new Dictionary<string, string>());
        return Response(run, state, job.ShoppingListProductId!.Value);
    }
    public async Task<PersonalComparisonResponse[]> PendingAsync(long accountId, CancellationToken ct)
    {
        if (!options.Value.PersonalCapturesEnabled) throw new UserImportException(503, "Personal comparisons are not enabled.");
        var now = clock.GetUtcNow().UtcDateTime;
        var runs = await db.PersonalRetailerComparisons.AsNoTracking().Include(r => r.CaptureJob)
            .Where(r => r.UserAccountId == accountId && r.CaptureJob.UserAccountId == accountId
                && r.CaptureJob.ShoppingList.UserAccountId == accountId && !r.CaptureJob.ShoppingList.IsArchived
                && (r.CaptureJob.ShoppingList.ExpiresDate == null || r.CaptureJob.ShoppingList.ExpiresDate > now)
                && r.CaptureJob.UserAccount.IsActive && (!r.CaptureJob.UserAccount.IsTrial || r.CaptureJob.UserAccount.ExpiresDate > now)
                && db.ShoppingListProducts.Any(i => i.Id == r.CaptureJob.ShoppingListProductId && !i.IsPurchased && !i.IsHidden))
            .OrderByDescending(r => r.Id).Take(100).ToArrayAsync(ct);
        return runs.Where(r => r.CaptureJob.ShoppingListProductId != null).Select(r => Response(r, Decode(r), r.CaptureJob.ShoppingListProductId!.Value))
            .Where(r => r.Status != "Completed").OrderBy(r => r.Id).ToArray();
    }
    public async Task<PersonalComparisonResponse> ReadAsync(long accountId, long id, CancellationToken ct)
    {
        var run = await db.PersonalRetailerComparisons.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.UserAccountId == accountId, ct)
            ?? throw new UserImportException(404, "Comparison not found.");
        var job = await JobAsync(accountId, run.CaptureJobId, ct);
        return Response(run, Decode(run), job.ShoppingListProductId!.Value);
    }
    public async Task<PersonalComparisonResponse> UpdateAsync(long accountId, long id, string action,
        PersonalComparisonResult? result, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(accountId, ct);
        var run = await db.PersonalRetailerComparisons.SingleOrDefaultAsync(r => r.Id == id && r.UserAccountId == accountId, ct)
            ?? throw new UserImportException(404, "Comparison not found.");
        var job = await JobAsync(accountId, run.CaptureJobId, ct);
        var state = Decode(run);
        var previousStatuses = state.Retailers.ToDictionary(r => r.Code, r => r.Status);
        if (action == "cancel")
        {
            foreach (var r in state.Retailers.Where(Active)) { r.Status = "Cancelled"; r.Message = "Checks stopped. Your saved product and completed results were kept."; r.Url = null; }
            state.LastStepToken = null; state.StepToken = Guid.NewGuid();
        }
        else if (action == "retry")
        {
            foreach (var r in state.Retailers.Where(r => r.Status is "UnableToCheck" or "Partial" or "Cancelled" or "NeedsVerification"))
            {
                r.Status = "Pending"; r.Message = null; r.QueryIndex = 0; r.Links.Clear(); r.Seen.Clear(); r.Url = null; r.HadFailures = false;
                r.RejectedCandidates.Clear(); r.PageFailures.Clear();
            }
            state.LastStepToken = null; state.StepToken = Guid.NewGuid(); Advance(state);
        }
        else
        {
            if (result is null || result.StepToken == Guid.Empty) throw new UserImportException(400, "Send the comparison step token.");
            if (state.LastStepToken == result.StepToken) return Response(run, state, job.ShoppingListProductId!.Value);
            if (state.StepToken != result.StepToken) throw new UserImportException(409, "This comparison step has changed. Refresh its status.");
            var r = state.Retailers.FirstOrDefault(Active) ?? throw new UserImportException(409, "This comparison has finished.");
            if (r.Url != result.Url) throw new UserImportException(400, "The result does not belong to the requested page.");
            if (!result.Ok) RecordPageFailure(r, result.ErrorCode);
            if (!result.Ok && result.ErrorCode == "retailer_access_restricted")
            { r.Status = "NeedsVerification"; r.Message = "Open the retailer, complete verification, then resume checks."; }
            else
            {
                r.Status = "Checking";
                if (result.Ok && !r.HadFailures) r.Message = null;
                if (!result.Ok) { r.HadFailures = true; r.Message = "Some pages could not be checked. You can retry this retailer."; }
                else if (r.Kind == "search") ReadSearch(r, result);
                else await ReadCandidateAsync(accountId, job, r, result, ct);
                r.Url = null; Advance(state);
            }
            state.LastStepToken = result.StepToken; state.StepToken = Guid.NewGuid();
        }
        run.StateJson = JsonSerializer.Serialize(state); run.UpdatedDate = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await JobAsync(accountId, run.CaptureJobId, ct);
        await tx.CommitAsync(ct);
        LogOutcomes(logger, run, job, state, previousStatuses);
        return Response(run, state, job.ShoppingListProductId!.Value);
    }
    internal static void RecordPageFailure(RetailerState retailer, string? errorCode)
    {
        // The requested URL is server-owned. Never persist or log arbitrary client error text.
        var code = errorCode switch
        {
            "page_timeout" or "page_changed" or "page_unavailable" or "invalid_page" or "invalid_evidence"
                or "retailer_access_restricted" => errorCode,
            _ => "unknown_error"
        };
        var failure = new PageFailure(retailer.Url!, retailer.Kind, code);
        if (retailer.PageFailures.Count < 20 && !retailer.PageFailures.Contains(failure)) retailer.PageFailures.Add(failure);
    }
    internal static void LogOutcomes(ILogger<PersonalRetailerComparisonService> logger, PersonalRetailerComparison run,
        ProductImportJob job, RunState state, IReadOnlyDictionary<string, string> previousStatuses)
    {
        foreach (var r in state.Retailers)
        {
            if (previousStatuses.TryGetValue(r.Code, out var previous) && previous == r.Status) continue;
            var outcome = r.Status switch
            {
                "NoMatch" => r.Seen.Count == 0 ? "no_search_results" : "candidates_rejected",
                "UnableToCheck" or "Partial" => "unable_to_check",
                "NeedsVerification" => "verification_required",
                "Alternatives" => "no_confirmed_exact_match",
                _ => null
            };
            if (outcome is null) continue;
            logger.LogInformation(new EventId(6101, "CompetitorComparisonUnsuccessful"),
                "Competitor comparison {Outcome}: comparison {ComparisonId}, capture {CaptureJobId}, product {ProductId}, source {SourceRetailer}, competitor {CompetitorRetailer}, status {Status}. Queries={@SearchQueries}; candidate URLs={@CandidateUrls}; rejections={@Rejections}; page failures={@PageFailures}; alternatives={AlternativeCount}",
                outcome, run.Id, job.Id, job.ProductId, job.SourceShop!.Code, r.Code, r.Status,
                r.Queries.Take(r.QueryIndex).ToArray(), r.Seen.ToArray(), r.RejectedCandidates.ToArray(), r.PageFailures.ToArray(), r.Alternatives.Count);
        }
    }
    private void ReadSearch(RetailerState r, PersonalComparisonResult result)
    {
        if (result.Links is null || result.Links.Length > 5 || result.Links.Length == 0 && !result.EmptyConfirmed)
            throw new UserImportException(400, "Search results could not be verified.");
        foreach (var link in result.Links)
        {
            var valid = urls.Validate(link);
            if (!valid.IsValid || valid.Retailer!.Code != r.Code) throw new UserImportException(400, "The search returned an invalid retailer product.");
            var canonical = valid.ProductUrl!.AbsoluteUri;
            if (!r.Seen.Contains(canonical) && !r.Links.Contains(canonical) && r.Seen.Count + r.Links.Count < 5) r.Links.Add(canonical);
        }
    }
    private static string? AlternativeQuery(ProductIdentity identity)
    {
        var normalisation = new ProductNormalisationService();
        var quantity = new PackComparisonService().Read(identity);
        if (quantity is null) return null;
        var brand = normalisation.Text(identity.Brand).Split(' ').ToHashSet();
        var words = quantity.FamilyName.Split(' ').Where(w => !brand.Contains(w)).ToArray();
        var query = string.Join(' ', words);
        return words.Length > 0 && query.Length <= 160 ? query : null;
    }
    public static string? Relationship(ProductIdentity original, ProductIdentity candidate)
    {
        var normalisation = new ProductNormalisationService();
        var match = new ProductMatchingService(normalisation).Match(original, candidate);
        if (match.Type == MatchType.Exact) return "Exact";
        if (new ProductFamilyMatchingService().IsDifferentSize(original, candidate)) return "Different size";
        if (match.Type == MatchType.Likely) return "Unconfirmed match";
        var a = normalisation.Normalise(original); var b = normalisation.Normalise(candidate);
        if (!match.Contradiction && !string.IsNullOrEmpty(a.Brand) && a.Brand == b.Brand
            && a.PackSize is > 0 && a.PackSize == b.PackSize && a.PackUnit is "g" or "mL" && a.PackUnit == b.PackUnit)
        {
            var firstTerms = ProductSearchQueryBuilder.ComparisonTerms(original)
                .SelectMany(t => normalisation.Text(t).Split(' ')).ToHashSet(StringComparer.Ordinal);
            var secondTerms = ProductSearchQueryBuilder.ComparisonTerms(candidate)
                .SelectMany(t => normalisation.Text(t).Split(' ')).ToHashSet(StringComparer.Ordinal);
            var brandTerms = normalisation.Text(a.Brand).Split(' ').ToHashSet(StringComparer.Ordinal);
            var firstCount = ProductSearchQueryBuilder.FlavourCount(original);
            var secondCount = ProductSearchQueryBuilder.FlavourCount(candidate);
            // Retailer packaging wording can differ. Keep these as reviewable candidates,
            // never as exact mappings, and require more evidence than the brand alone.
            if (firstTerms.Any(t => !brandTerms.Contains(t)) && firstTerms.SetEquals(secondTerms)
                && (firstCount is null || secondCount is null || firstCount == secondCount))
                return "Unconfirmed match";
        }
        var x = new PackComparisonService().Read(original); var y = new PackComparisonService().Read(candidate);
        var first = AlternativeQuery(original); var second = AlternativeQuery(candidate);
        // Equal residual wording keeps varieties, dietary claims and formats.
        // Matching only the product noun would suggest jasmine for basmati rice.
        return (string.IsNullOrEmpty(a.Brand) || string.IsNullOrEmpty(b.Brand) || a.Brand != b.Brand)
            && a.Variant == b.Variant && a.ModelNumber == b.ModelNumber
            && first is not null && first == second && x is not null && y is not null
            && x.Unit == y.Unit && y.Total / x.Total is >= 0.25m and <= 4m
            ? string.IsNullOrEmpty(a.Brand) || string.IsNullOrEmpty(b.Brand) ? "Unconfirmed match · brand unavailable" : "Different brand" : null;
    }
    private async Task ReadCandidateAsync(long accountId, ProductImportJob job, RetailerState r, PersonalComparisonResult result, CancellationToken ct)
    {
        if (result.Evidence is null || result.ObservedAt is not { } date || date > clock.GetUtcNow() || date < clock.GetUtcNow().AddMinutes(-10))
            throw new UserImportException(400, "Read the candidate again to obtain recent evidence.");
        var source = await parser.ParseAsync(result.Url, result.Evidence, ct, date);
        if (source.ShopCode != r.Code || !SourceProductPersistenceService.ValidSource(source, urls, new ProductNormalisationService(), clock))
            throw new UserImportException(400, "The candidate product could not be validated.");
        var identity = ProductMatchingService.Identity(job.Product!);
        var match = matching.Match(identity, source.Identity);
        var relationship = Relationship(identity, source.Identity);
        if (relationship is null)
        {
            r.RejectedCandidates.Add(new(source.ProductUrl.AbsoluteUri, match.Reason));
            return;
        }
        var offer = (source.Offer as ProviderResult<ShopProductOffer>.Success)?.Value;
        var candidate = new PersonalComparisonCandidate(source.Identity.Name, source.ProductUrl.AbsoluteUri, relationship,
            offer?.Price, offer?.Currency, date, offer is null ? "Price unavailable" : offer.SpecialDescription);
        if (relationship == "Exact")
        {
            var shop = await db.Shops.SingleOrDefaultAsync(s => s.Code == r.Code && s.IsActive, ct)
                ?? throw new UserImportException(409, "This retailer is unavailable.");
            var known = await mappings.FindPersonalAsync(accountId, shop.Id, source, ct);
            if (known.Count > 1 || known.Any(m => m.ProductId != job.ProductId))
            { r.Alternatives.Add(candidate with { Relationship = "Already saved separately · review required" }); return; }
            var mapping = mappings.Save(shop, job.Product!, known.SingleOrDefault(), source, match.Confidence);
            await db.SaveChangesAsync(ct);
            if (offer is not null)
            {
                var observation = new ContributionObservation { UserAccountId = accountId, Source = "PersonalAdd", Url = mapping.ProductUrl,
                    CollectedAt = date.UtcDateTime, ReceivedAt = clock.GetUtcNow().UtcDateTime, Outcome = "Accepted" };
                db.ContributionObservations.Add(observation); await db.SaveChangesAsync(ct);
                var saved = await prices.SavePersonalAsync(accountId, mapping, offer with { SourceType = SourceType.BrowserExtension }, ct, observation.Id);
                if (saved.Status == Services.Models.PriceUpdateStatus.InvalidOffer) throw new UserImportException(400, "The candidate price could not be validated.");
            }
            r.ExactMatches.Add(candidate); r.Status = "Exact"; r.Message = offer is null ? "Exact product found; price unavailable." : "Exact product and price found.";
        }
        else if (!r.Alternatives.Any(c => c.Url == candidate.Url)) r.Alternatives.Add(candidate);
    }
    private static void Advance(RunState state)
    {
        foreach (var r in state.Retailers.Where(Active))
        {
            if (r.Url is not null) return;
            r.Status = "Checking";
            if (r.Links.Count > 0 && r.Seen.Count < 5)
            { r.Url = r.Links[0]; r.Links.RemoveAt(0); r.Seen.Add(r.Url); r.Kind = "product"; return; }
            if (r.QueryIndex < r.Queries.Count && r.Seen.Count < 5)
            { r.Url = ProductSearchQueryBuilder.SearchUrl(r.Code, r.Queries[r.QueryIndex++]).AbsoluteUri; r.Kind = "search"; return; }
            r.Status = r.HadFailures ? r.Alternatives.Count > 0 ? "Partial" : "UnableToCheck" : r.Alternatives.Count > 0 ? "Alternatives" : "NoMatch";
            r.Message ??= r.Status == "NoMatch" ? "No matching product found in the checked results." : "Alternatives need your review and are excluded from exact totals.";
        }
    }
    public static async Task<Dictionary<long, PersonalComparisonResponse>> ReadForListAsync(MyShoppingListDbContext db, long accountId, long listId, CancellationToken ct)
    {
        var runs = await db.PersonalRetailerComparisons.AsNoTracking().Include(r => r.CaptureJob)
            .Where(r => r.UserAccountId == accountId && r.CaptureJob.UserAccountId == accountId
                && r.CaptureJob.ShoppingListId == listId && r.CaptureJob.ShoppingListProductId != null).OrderByDescending(r => r.Id).ToArrayAsync(ct);
        return runs.DistinctBy(r => r.CaptureJob.ShoppingListProductId).ToDictionary(r => r.CaptureJob.ShoppingListProductId!.Value,
            r => Response(r, Decode(r), r.CaptureJob.ShoppingListProductId!.Value, false));
    }
}
