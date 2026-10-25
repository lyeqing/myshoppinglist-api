using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Services;

public sealed class PersonalProductCaptureService(MyShoppingListDbContext db, ExtensionProductEvidenceParser parser,
    SourceProductPersistenceService persistence, ProductUrlValidator urls, TimeProvider clock, IOptions<UserExtensionOptions> options)
{
    public async Task<PersonalProductPreviewResponse> PreviewAsync(long accountId, PersonalProductPreviewRequest request, CancellationToken ct)
    {
        if (!options.Value.PersonalCapturesEnabled)
            throw new UserImportException(503, "Personal product capture is not enabled yet.");
        CheckAccount(await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct));
        if (request.Evidence is null || request.ObservedAt == default || request.ObservedAt > clock.GetUtcNow())
            throw new UserImportException(400, "Send product evidence and a valid observation date.");
        var source = await parser.ParseAsync(request.Url, request.Evidence, ct, request.ObservedAt.ToUniversalTime());
        if (!SourceProductPersistenceService.ValidSource(source, urls, new ProductNormalisationService(), clock))
            throw new UserImportException(400, "The extracted product could not be validated. Check the page and read it again.");
        var offer = (source.Offer as ProviderResult<ShopProductOffer>.Success)?.Value;
        CheckAccount(await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, ct));
        return new(source.ProductUrl.AbsoluteUri, source.Identity.Name, source.Identity.Brand,
            source.Identity.PackQuantity, source.Identity.PackSize, source.Identity.PackUnit,
            offer?.Price, offer?.Currency, request.ObservedAt.ToUniversalTime(),
            offer is null ? "No verified price is available. You can save the product without a price." : null);
    }

    public async Task<PersonalProductCaptureResponse> CaptureAsync(long accountId, PersonalProductCaptureRequest request, CancellationToken ct)
    {
        if (!options.Value.PersonalCapturesEnabled)
            throw new UserImportException(503, "Personal product capture is not enabled yet.");
        if (request.RequestId == Guid.Empty || request.ListId <= 0 || request.Quantity <= 0 || request.Evidence is null)
            throw new UserImportException(400, "Send a capture request ID, shopping list, product evidence and a positive whole-number quantity.");
        if (request.ObservedAt == default || request.ObservedAt > clock.GetUtcNow())
            throw new UserImportException(400, "The observation date is missing or in the future. Check your device clock and capture the product again.");
        var valid = urls.Validate(request.Url);
        if (!valid.IsValid || valid.Retailer!.Code is not ("coles" or "woolworths" or "aldi"))
            throw new UserImportException(400, "Use a Coles, Woolworths or ALDI product URL.");
        var canonical = valid.ProductUrl!.AbsoluteUri;
        var date = request.ObservedAt.ToUniversalTime();
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.ListId, Url = canonical, request.Quantity, request.Evidence, ObservedAt = date
        })));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Keep the same lock order as persistence, avoiding account/catalogue inversion.
            await ProductService.LockCatalogueAsync(db, ct);
            var account = await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""")
                .SingleOrDefaultAsync(ct);
            CheckAccount(account);
            var previous = await db.ProductImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.UserAccountId == accountId
                && j.CaptureRequestId == request.RequestId, ct);
            if (previous is not null && previous.CaptureRequestHash != hash)
                throw new UserImportException(409, "This capture request was already used with different information. Start a new capture.");
            var list = await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {request.ListId} AND "UserAccountId" = {accountId} FOR UPDATE""")
                .SingleOrDefaultAsync(ct);
            CheckList(list);
            if (previous is not null)
            {
                if (previous.CaptureResponseJson is null || previous.Status != ProductImportJobStatus.Completed)
                    throw new UserImportException(409, "This capture has not completed. Retry the same request.");
                var saved = JsonSerializer.Deserialize<PersonalProductCaptureResponse>(previous.CaptureResponseJson)
                    ?? throw new InvalidOperationException("A completed capture must have a saved response.");
                if (!await db.ShoppingListProducts.AnyAsync(i => i.Id == saved.ShoppingListProductId
                    && i.ShoppingListId == list!.Id && i.ProductId == saved.ProductId && i.Product.UserAccountId == accountId, ct))
                    throw new UserImportException(409, "The previously captured item was removed. Start a new capture to add it again.");
                CheckAccount(account); CheckList(list);
                return saved with { Reused = true };
            }

            var extracted = await parser.ParseAsync(canonical, request.Evidence, ct, date);
            var source = extracted with
            {
                SourceType = SourceType.BrowserExtension,
                Offer = extracted.Offer is ProviderResult<ShopProductOffer>.Success offer
                    ? new ProviderResult<ShopProductOffer>.Success(offer.Value with { SourceType = SourceType.BrowserExtension }) : extracted.Offer
            };
            var now = clock.GetUtcNow().UtcDateTime;
            var shop = await db.Shops.AsNoTracking().SingleOrDefaultAsync(s => s.Code == source.ShopCode && s.IsActive, ct)
                ?? throw new UserImportException(400, "This retailer is currently unavailable for captures.");
            var claim = Guid.NewGuid();
            var job = new ProductImportJob
            {
                UserAccountId = accountId, ShoppingListId = list!.Id, SourceShopId = shop.Id, SourceUrl = canonical,
                NormalisedSourceUrl = canonical, RequestedQuantity = request.Quantity,
                CaptureRequestId = request.RequestId, CaptureRequestHash = hash,
                Status = ProductImportJobStatus.Processing, ProgressStage = ProductImportProgressStage.SavingSourceProduct,
                ClaimToken = claim, LeaseExpiresDate = now.AddMinutes(10), CreatedDate = now, LastActivityDate = now
            };
            db.ProductImportJobs.Add(job);
            await db.SaveChangesAsync(ct);
            var observation = new ContributionObservation
            {
                UserAccountId = accountId, ImportJobId = job.Id, Source = "PersonalAdd", Url = canonical,
                CollectedAt = date.UtcDateTime, ReceivedAt = now, Outcome = "Pending"
            };
            db.ContributionObservations.Add(observation);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            var result = await persistence.SavePersonalAsync(job.Id, accountId, claim, source, ct, observation.Id);
            if (result.Status != ProductPersistenceStatus.Success)
                throw CaptureError(result);
            var response = new PersonalProductCaptureResponse(job.Id, result.ProductId!.Value,
                result.ShoppingListProductId!.Value, request.Quantity, result.PriceSaved, result.ExistingListItem, false, date);
            await db.ProductImportJobs.Where(j => j.Id == job.Id && j.UserAccountId == accountId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.CaptureResponseJson, JsonSerializer.Serialize(response)), ct);
            await db.ContributionObservations.Where(o => o.Id == observation.Id && o.UserAccountId == accountId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Outcome, "Accepted"), ct);
            CheckAccount(account); CheckList(list);
            await transaction.CommitAsync(ct);
            return response;
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private void CheckAccount(UserAccount? account)
    {
        if (account is null || !account.IsActive || account.IsTrial && !(account.ExpiresDate > clock.GetUtcNow().UtcDateTime))
            throw new UserImportException(401, "A valid session is required.");
    }
    private void CheckList(ShoppingList? list)
    {
        if (list is null) throw new UserImportException(404, "The shopping list was not found.");
        if (list.IsArchived || list.ExpiresDate <= clock.GetUtcNow().UtcDateTime)
            throw new UserImportException(409, "This shopping list is no longer available for captures.");
    }
    private static UserImportException CaptureError(ProductPersistenceResult result) => result.Status switch
    {
        ProductPersistenceStatus.AccessDenied or ProductPersistenceStatus.ExpiredAccount => new(401, "Your account is unavailable. Sign in again."),
        ProductPersistenceStatus.NotFound or ProductPersistenceStatus.ListUnavailable => new(404, "The shopping list or item is no longer available."),
        ProductPersistenceStatus.InvalidData => new(400, "The extracted product or price could not be validated. Review the product page and capture again."),
        _ => new(409, "The product could not be saved because its details changed or conflict. Review the page and capture again.")
    };
}
