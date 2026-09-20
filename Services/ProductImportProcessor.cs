using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Services;

public sealed class ProductImportProcessor(ProductImportJobService jobs, SourceProductPersistenceService persistence,
    RetailerProviderRegistry providers, ProductUrlValidator urls, ILogger<ProductImportProcessor> logger,
    RetailerComparisonService comparisons)
{
    public async Task ProcessAsync(ProductImportClaim claim, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var job = await jobs.ReadAsync(claim, token);
        if (job is null) return;
        if (!await jobs.HasAccessAsync(claim, token))
        { await jobs.FailAsync(claim, "access_expired", false, token); return; }
        // A restart after the source transaction committed must preserve its product and list item.
        if (job.ShoppingListProductId.HasValue)
        { await comparisons.RunAsync(claim, token); return; }
        var validated = urls.Validate(job.SourceUrl);
        if (!validated.IsValid)
        { await jobs.FailAsync(claim, "invalid_source_url", false, token); return; }
        var provider = providers.FindByCode(validated.Retailer!.Code)?.Provider;
        if (provider is null)
        { await jobs.FailAsync(claim, "source_not_supported", false, token); return; }
        if (!await jobs.ProgressAsync(claim, ProductImportProgressStage.ReadingSourceProduct, token)) return;
        logger.LogInformation("Reading source {SourceShop} for import job {ProductImportJobId}, list {ShoppingListId}",
            provider.ShopCode, claim.JobId, claim.ShoppingListId);
        var source = await provider.GetProductFromUrlAsync(validated.ProductUrl!, token);
        token.ThrowIfCancellationRequested();
        if (source is ProviderResult<ExtractedShopProduct>.Failure failure)
        {
            await jobs.FailAsync(claim, failure.Error.Code, failure.Error.IsRetryable, token, failure.Error.RetryAfter);
            return;
        }
        if (!await jobs.ProgressAsync(claim, ProductImportProgressStage.SavingSourceProduct, token)) return;
        var result = await persistence.SaveAsync(claim.JobId, claim.UserAccountId, claim.Token,
            ((ProviderResult<ExtractedShopProduct>.Success)source).Value, token);
        if (result.Status == ProductPersistenceStatus.Success)
            await comparisons.RunAsync(claim, token);
        else if (result.Status is not (ProductPersistenceStatus.LostClaim or ProductPersistenceStatus.NotFound))
            await jobs.FailAsync(claim, result.ErrorCode ?? "source_persistence_failed",
                result.Status == ProductPersistenceStatus.PersistenceConflict, token);
    }
}
