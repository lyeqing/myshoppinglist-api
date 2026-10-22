using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Services;

public sealed class BrandDiscoveryProcessor(MyShoppingListDbContext db, BrandDiscoveryService jobs,
    RetailerProviderRegistry registry, BrandDiscoveryPersistenceService persistence)
{
    public async Task RunAsync(BrandDiscoveryClaim claim, CancellationToken token)
    {
        var job = await jobs.ReadClaimAsync(claim, token);
        if (job is null) return;
        var found = 0;
        try
        {
            if (!await jobs.EligibleAsync(job, token)) { await jobs.FinishAsync(claim, "Cancelled", 0, "request_no_longer_eligible", token); return; }
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == job.ProductId, token);
            var shop = await db.Shops.AsNoTracking().SingleAsync(s => s.Id == job.ShopId, token);
            var query = BrandDiscoveryService.Query(product);
            if (query is null || registry.FindByCode(shop.Code)?.Provider is not IBrandAlternativeSearchProvider provider)
            { await jobs.FinishAsync(claim, "Failed", 0, "discovery_not_supported", token); return; }
            var result = await provider.SearchAlternativeBrandsAsync(query, token);
            if (result is ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure failure)
            {
                await jobs.FinishAsync(claim, "Failed", 0, failure.Error.Code, token,
                    failure.Error.IsRetryable ? failure.Error.RetryAfter ?? TimeSpan.FromMinutes(1) : null);
                return;
            }
            var candidates = ((ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success)result).Value;
            var failures = false;
            // Provider readers enforce the page limit too; never persist an unbounded response.
            var matcher = new BrandSuggestionMatchingService();
            foreach (var candidate in candidates.Take(8).DistinctBy(c => c.Product.ShopProductCode is { Length: > 0 } code ? "code:" + code.Trim() : c.Product.ProductUrl.GetLeftPart(UriPartial.Path)))
            {
                if (!await jobs.EligibleAsync(job, token)) { await jobs.FinishAsync(claim, "Cancelled", found, "request_no_longer_eligible", token); return; }
                if (!matcher.IsSuggestion(ProductMatchingService.Identity(product), candidate.Product.Identity)) continue;
                if (await persistence.SaveAsync(claim, candidate.Product, token)) found++;
                else failures = true;
            }
            await jobs.FinishAsync(claim, failures ? "Failed" : "Completed", found, failures ? "some_candidates_unavailable" : null, token);
        }
        catch (ColesWorkPendingException)
        { await jobs.FinishAsync(claim, "WaitingForExtension", found, null, token, TimeSpan.FromSeconds(30)); }
        catch (ColesWorkFailedException error)
        { await jobs.FinishAsync(claim, "Failed", found, "extension_task_failed", token, failedTaskId: error.TaskId); }
    }
}
