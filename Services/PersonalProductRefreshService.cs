using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Services;

// Refresh only existing private mappings. Never run Add or modify a list item.
public sealed class PersonalProductRefreshService(MyShoppingListDbContext db,
    ExtensionProductEvidenceParser parser, PriceService prices, TimeProvider clock,
    IOptions<UserExtensionOptions> options)
{
    private async Task CheckAsync(long accountId, long listId, CancellationToken ct)
    {
        if (!options.Value.PersonalCapturesEnabled)
            throw new UserImportException(503, "Personal price updates are not enabled.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.UserAccounts.AnyAsync(a => a.Id == accountId && a.IsActive
            && (!a.IsTrial || a.ExpiresDate > now), ct))
            throw new UserImportException(401, "Sign in again to update prices.");
        if (!await db.ShoppingLists.AnyAsync(l => l.Id == listId && l.UserAccountId == accountId
            && !l.IsArchived && (l.ExpiresDate == null || l.ExpiresDate > now), ct))
            throw new UserImportException(404, "This shopping list is no longer available.");
    }

    public async Task<PersonalRefreshTarget[]> TargetsAsync(long accountId, long listId, CancellationToken ct)
    {
        await CheckAsync(accountId, listId, ct);
        return await (from item in db.ShoppingListProducts.AsNoTracking()
            join mapping in db.ShopProducts on item.ProductId equals mapping.ProductId
            where item.ShoppingListId == listId && !item.IsPurchased && !item.IsHidden
                && mapping.UserAccountId == accountId && mapping.Product.UserAccountId == accountId
                && mapping.IsActive && mapping.Shop.IsActive
                && (mapping.Shop.Code == "coles" || mapping.Shop.Code == "woolworths" || mapping.Shop.Code == "aldi")
            orderby item.Id, mapping.Id
            select new PersonalRefreshTarget(item.Id, mapping.Id, mapping.NameAtShop, mapping.ProductUrl)).ToArrayAsync(ct);
    }

    public async Task<PersonalPriceRefreshResponse> SaveAsync(long accountId, PersonalPriceRefreshRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProductService.LockCatalogueAsync(db, ct);
        // Match the catalogue/account/list lock order used by capture and cleanup.
        await db.UserAccounts.FromSqlInterpolated($"""SELECT * FROM "UserAccounts" WHERE "Id" = {accountId} FOR UPDATE""").LoadAsync(ct);
        await db.ShoppingLists.FromSqlInterpolated($"""SELECT * FROM "ShoppingLists" WHERE "Id" = {request.ListId} AND "UserAccountId" = {accountId} FOR UPDATE""").LoadAsync(ct);
        await CheckAsync(accountId, request.ListId, ct);
        var mapping = await db.ShopProducts.SingleOrDefaultAsync(m => m.Id == request.MappingId
            && m.UserAccountId == accountId && m.Product.UserAccountId == accountId && m.IsActive && m.Shop.IsActive
            && db.ShoppingListProducts.Any(i => i.Id == request.ItemId && i.ShoppingListId == request.ListId
                && i.ProductId == m.ProductId && !i.IsPurchased && !i.IsHidden), ct)
            ?? throw new UserImportException(409, "This item is no longer eligible for a price update.");
        if (request.Evidence is null || request.ObservedAt == default || request.ObservedAt > clock.GetUtcNow()
            || request.ObservedAt < clock.GetUtcNow().AddMinutes(-10))
            throw new UserImportException(400, "Read the product again to obtain a recent observation.");
        var source = await parser.ParseAsync(mapping.ProductUrl, request.Evidence, ct, request.ObservedAt.ToUniversalTime());
        if (string.IsNullOrEmpty(mapping.ShopProductCode) || source.ShopProductCode != mapping.ShopProductCode
            || !string.IsNullOrEmpty(mapping.GTIN) && !string.IsNullOrEmpty(source.Identity.GTIN) && mapping.GTIN != source.Identity.GTIN)
            throw new UserImportException(409, "The retailer product identity changed. Review the product page.");
        if (source.Offer is not ProviderResult<ShopProductOffer>.Success offer)
            return new(false, "No verified price was found. The previous observation was kept.");
        var date = request.ObservedAt.UtcDateTime;
        var observation = await db.ContributionObservations.SingleOrDefaultAsync(o => o.UserAccountId == accountId
            && o.Source == "PersonalAdd" && o.Url == mapping.ProductUrl && o.CollectedAt == date && o.ImportJobId == null, ct);
        if (observation is null)
        {
            observation = new ContributionObservation { UserAccountId = accountId, Source = "PersonalAdd",
                Url = mapping.ProductUrl, CollectedAt = date, ReceivedAt = clock.GetUtcNow().UtcDateTime, Outcome = "Pending" };
            db.ContributionObservations.Add(observation);
            await db.SaveChangesAsync(ct);
        }
        var saved = await prices.SavePersonalAsync(accountId, mapping,
            offer.Value with { SourceType = SourceType.BrowserExtension }, ct, observation.Id);
        if (saved.Status == PriceUpdateStatus.InvalidOffer)
            throw new UserImportException(400, "The price could not be validated. The previous observation was kept.");
        observation.Outcome = "Accepted";
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return saved.Status == PriceUpdateStatus.OlderObservation
            ? new(false, "A newer observation is already saved.") : new(true, "Price observation updated.");
    }
}
