using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class RetailerComparisonService(IServiceScopeFactory scopes, IOptions<ProductImportOptions> options,
    ILogger<RetailerComparisonService> logger)
{
    public async Task RunAsync(ProductImportClaim claim, CancellationToken token)
    {
        await using var control = scopes.CreateAsyncScope();
        var db = control.ServiceProvider.GetRequiredService<MyShoppingListDbContext>();
        var jobs = control.ServiceProvider.GetRequiredService<ProductImportJobService>();
        var job = await jobs.ReadAsync(claim, token);
        if (job?.ProductId is null || job.ShoppingListProductId is null) return;
        var shopIds = await db.Shops.Where(s => s.IsActive && s.Id != job.SourceShopId).OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(token);
        var retry = false;
        TimeSpan? retryAfter = null;
        // Sequential bounded checks; each retailer gets an independent context, provider and transaction scope.
        foreach (var shopId in shopIds)
        {
            token.ThrowIfCancellationRequested();
            if (!await jobs.HasAccessAsync(claim, token)) break;
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var store = services.GetRequiredService<RetailerComparisonPersistenceService>();
            if (!await store.SaveStatusAsync(claim, shopId, RetailerLookupStatus.Checking, null, token)) continue;
            ProviderFailure? failure;
            try { failure = await CheckAsync(services, store, claim, shopId, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning("Comparison failed for job {JobId}, shop {ShopId}: {ExceptionType}", claim.JobId, shopId, exception.GetType().Name);
                failure = new(ProviderFailureKind.UnexpectedError, "comparison_failed", "The retailer check failed.");
            }
            if (failure is null) continue;
            var pending = failure.IsRetryable && job.AttemptCount < options.Value.MaxAttempts;
            if (await store.SaveStatusAsync(claim, shopId, pending ? RetailerLookupStatus.Pending : failure.Status, failure.Code, token) && pending)
            {
                retry = true;
                if (failure.RetryAfter is { } delay && (retryAfter is null || delay > retryAfter)) retryAfter = delay;
            }
        }
        if (retry && await jobs.HasAccessAsync(claim, token))
            await jobs.FailAsync(claim, "comparison_retry_pending", true, token, retryAfter);
        else await jobs.CompleteSourceStageAsync(claim, token);
    }

    private static async Task<ProviderFailure?> CheckAsync(IServiceProvider services, RetailerComparisonPersistenceService store,
        ProductImportClaim claim, long shopId, CancellationToken token)
    {
        if (await store.TryCacheAsync(claim, shopId, token)) return null;
        var db = services.GetRequiredService<MyShoppingListDbContext>();
        var jobs = services.GetRequiredService<ProductImportJobService>();
        if (!await jobs.HasAccessAsync(claim, token)) return null;
        var job = await jobs.ReadAsync(claim, token);
        if (job?.ProductId is null) return null;
        var shop = await db.Shops.AsNoTracking().SingleAsync(s => s.Id == shopId, token);
        var provider = services.GetRequiredService<RetailerProviderRegistry>().FindByCode(shop.Code)?.Provider;
        if (provider is null) return new(ProviderFailureKind.NotSupported, "comparison_not_supported", "This retailer is not supported.");
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == job.ProductId && !p.IsDeleted, token);
        var identity = ProductMatchingService.Identity(product);
        var matcher = services.GetRequiredService<ProductMatchingService>();
        var known = await db.ShopProducts.AsNoTracking().Where(p => p.ProductId == product.Id && p.ShopId == shopId
            && p.IsActive && p.MatchType == MatchType.Exact).ToListAsync(token);
        if (known.Count > 1) return Invalid("ambiguous_retailer_mapping");
        ExtractedShopProduct? selected = null;
        if (known.Count == 1)
        {
            var fetched = await provider.GetProductFromUrlAsync(new Uri(known[0].ProductUrl), token);
            if (fetched is ProviderResult<ExtractedShopProduct>.Failure error) return error.Error;
            selected = ((ProviderResult<ExtractedShopProduct>.Success)fetched).Value;
            if (matcher.Match(identity, selected.Identity).Type != MatchType.Exact
                || known[0].ShopProductCode is { } code && code != selected.ShopProductCode)
                return Invalid("retailer_identity_changed");
        }
        else
        {
            var search = await provider.SearchAsync(identity, null, token);
            if (search is ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure error) return error.Error;
            var candidates = ((ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success)search).Value;
            var ranked = candidates.Select(c => (Product: c.Product, Match: matcher.Match(identity, c.Product.Identity))).ToList();
            var exact = ranked.Where(c => c.Match.Type == MatchType.Exact).ToList();
            if (exact.Count > 1) return Invalid("ambiguous_exact_matches");
            if (exact.Count == 1) selected = exact[0].Product;
            else
            {
                var likely = ranked.Where(c => c.Match.Type == MatchType.Likely).OrderByDescending(c => c.Match.Confidence).FirstOrDefault();
                await store.SaveStatusAsync(claim, shopId, likely.Product is null ? RetailerLookupStatus.NotFound : RetailerLookupStatus.Likely,
                    likely.Product is null ? "no_verified_match_in_candidates" : "identity_unconfirmed", token,
                    likely.Product is null ? null : MatchType.Likely, likely.Product is null ? null : likely.Match.Confidence);
                return null;
            }
        }
        if (selected.Offer is ProviderResult<ShopProductOffer>.Failure offerFailure && offerFailure.Error.IsRetryable)
            return offerFailure.Error;
        return await store.SaveExactAsync(claim, shopId, selected, token) ? null : Invalid("comparison_persistence_rejected");
    }

    private static ProviderFailure Invalid(string code) => new(ProviderFailureKind.InvalidProduct, code, "The comparison could not be verified.");
}
