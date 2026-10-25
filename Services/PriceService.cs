using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Services;

public sealed class PriceService(MyShoppingListDbContext db, IOptions<PriceOptions> options,
    TimeProvider clock, ProductUrlValidator urls)
{
    public Task<PriceUpdateResult> SaveAsync(ShopProduct mapping, ShopProductOffer offer, CancellationToken token, long? observation = null) =>
        SaveCoreAsync(null, mapping, offer, token, observation);

    public Task<PriceUpdateResult> SavePersonalAsync(long accountId, ShopProduct mapping, ShopProductOffer offer,
        CancellationToken token, long? observation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        return SaveCoreAsync(accountId, mapping, offer, token, observation);
    }

    private async Task<PriceUpdateResult> SaveCoreAsync(long? accountId, ShopProduct mapping, ShopProductOffer offer,
        CancellationToken token, long? observation)
    {
        await ProductService.LockCatalogueAsync(db, token);
        if (mapping.UserAccountId != accountId || !await db.ShopProducts.AsNoTracking().AnyAsync(p => p.Id == mapping.Id
            && p.UserAccountId == accountId && p.Product.UserAccountId == accountId
            && p.ProductId == mapping.ProductId && p.ShopId == mapping.ShopId && p.ProductUrl == mapping.ProductUrl, token))
            return Invalid("mapping_owner_mismatch");
        if (accountId is { } owner)
        {
            var accountNow = clock.GetUtcNow().UtcDateTime;
            if (!await db.UserAccounts.AnyAsync(a => a.Id == owner && a.IsActive && (!a.IsTrial || a.ExpiresDate > accountNow), token))
                return Invalid("inactive_account");
            if (observation is null) return Invalid("personal_observation_required");
            if (offer.SourceType != SourceType.BrowserExtension) return Invalid("personal_capture_required");
        }
        if (observation is { } observed) {
            var provenance = await db.ContributionObservations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == observed, token);
            if (provenance is null) return Invalid("invalid_observation");
            if (accountId is not null && (provenance.UserAccountId != accountId || provenance.Source != "PersonalAdd"
                || provenance.Url != mapping.ProductUrl || provenance.ImportJobId is { } job
                    && !await db.ProductImportJobs.AnyAsync(j => j.Id == job && j.UserAccountId == accountId, token)))
                return Invalid("observation_owner_mismatch");
            if (accountId is null && provenance.UserAccountId is { } account
                && !await db.UserAccounts.AnyAsync(a => a.Id == account && a.IsActive && !a.ContributionBlocked, token))
                return Invalid("contribution_blocked");
        }
        var shop = await db.Shops.SingleAsync(s => s.Id == mapping.ShopId, token);
        var sourceUrl = urls.Validate(offer.SourceUrl.OriginalString);
        if (accountId is not null && sourceUrl.ProductUrl?.AbsoluteUri != mapping.ProductUrl)
            return Invalid("observation_product_mismatch");
        if (!string.Equals(offer.ShopCode, shop.Code, StringComparison.OrdinalIgnoreCase) || !sourceUrl.IsValid
            || sourceUrl.Retailer!.Code != shop.Code || !Amount(offer.Price) || !Amount(offer.NormalPrice) || !Amount(offer.UnitPrice)
            || offer.Currency.Length != 3 || !offer.Currency.All(char.IsAsciiLetter)
            || !Enum.IsDefined(offer.PriceScope) || !Enum.IsDefined(offer.SourceType)
            || offer.CheckedDate == default || offer.CheckedDate > clock.GetUtcNow()
            || offer.SpecialStartDate > offer.SpecialEndDate || offer.NormalPrice < offer.Price
            || offer.SpecialType?.Length > 100 || offer.SpecialDescription?.Length > 2000)
            return Invalid("invalid_offer");

        long? locationId = null;
        if (offer.Location is { } context)
        {
            if (!string.Equals(context.ShopCode, shop.Code, StringComparison.OrdinalIgnoreCase)) return Invalid("location_retailer_mismatch");
            var location = context.ShopLocationId.HasValue
                ? await db.ShopLocations.SingleOrDefaultAsync(l => l.Id == context.ShopLocationId, token)
                : context.StoreCode is not null
                    ? await db.ShopLocations.SingleOrDefaultAsync(l => l.ShopId == shop.Id && l.StoreCode == context.StoreCode, token) : null;
            if (location is null || location.ShopId != shop.Id || !location.IsActive
                || context.StoreCode is not null && context.StoreCode != location.StoreCode) return Invalid("invalid_location");
            locationId = location.Id;
        }
        if (offer.PriceScope == PriceScope.StoreSpecific && locationId is null) return Invalid("location_required");
        var currency = offer.Currency.ToUpperInvariant();
        // PostgreSQL stores microseconds; compare at storage precision so retrying the same observation is idempotent.
        var checkedDate = StorageTime(offer.CheckedDate);
        var specialStart = offer.SpecialStartDate is { } start ? StorageTime(start) : (DateTime?)null;
        var specialEnd = offer.SpecialEndDate is { } end ? StorageTime(end) : (DateTime?)null;
        var current = await db.ShopProductPrices.SingleOrDefaultAsync(p => p.UserAccountId == accountId && p.ShopProductId == mapping.Id
            && p.ShopLocationId == locationId && p.PriceScope == offer.PriceScope && p.Currency == currency, token);
        if (current?.CheckedDate > checkedDate) return new(PriceUpdateStatus.OlderObservation);
        var changed = current is null || current.Price != offer.Price || current.NormalPrice != offer.NormalPrice
            || current.UnitPrice != offer.UnitPrice || current.SpecialType != offer.SpecialType
            || current.SpecialDescription != offer.SpecialDescription || current.SpecialStartDate != specialStart
            || current.SpecialEndDate != specialEnd;
        if (current?.CheckedDate == checkedDate && changed) return Invalid("conflicting_observation");
        var latestHistory = await db.ShopProductPriceHistory.Where(p => p.UserAccountId == accountId && p.ShopProductId == mapping.Id
            && p.ShopLocationId == locationId && p.PriceScope == offer.PriceScope && p.Currency == currency)
            .MaxAsync(p => (DateTime?)p.CheckedDate, token);
        var recordHistory = changed || latestHistory is null || checkedDate - latestHistory >= TimeSpan.FromHours(options.Value.HistorySampleHours);
        var now = clock.GetUtcNow().UtcDateTime;
        // Import processing may replay a price already saved by the extension queue.
        // Preserve its contributor rather than relabeling the same observation as server data.
        if (observation is null && current?.CheckedDate == checkedDate) observation = current.ContributionObservationId;
        if (observation is null) {
            var provenance = new ContributionObservation { Source = "Server", Url = sourceUrl.ProductUrl!.AbsoluteUri,
                CollectedAt = checkedDate, ReceivedAt = now, Outcome = "Accepted" };
            db.ContributionObservations.Add(provenance); await db.SaveChangesAsync(token); observation = provenance.Id;
        }
        if (current is null)
        {
            current = new() { UserAccountId = accountId, ShopProductId = mapping.Id, ShopLocationId = locationId, Currency = currency, PriceScope = offer.PriceScope, CreatedDate = now };
            db.ShopProductPrices.Add(current);
        }
        current.Price = offer.Price; current.NormalPrice = offer.NormalPrice; current.UnitPrice = offer.UnitPrice;
        current.SpecialType = offer.SpecialType; current.SpecialDescription = offer.SpecialDescription;
        current.SpecialStartDate = specialStart; current.SpecialEndDate = specialEnd;
        current.InStock = offer.InStock; current.SourceType = offer.SourceType; current.SourceUrl = sourceUrl.ProductUrl!.AbsoluteUri;
        current.CheckedDate = checkedDate; current.UpdatedDate = now;
        current.ContributionObservationId = observation;
        if (recordHistory) db.ShopProductPriceHistory.Add(new()
        {
            UserAccountId = accountId, ShopProductId = mapping.Id, ShopLocationId = locationId, Currency = currency,
            Price = current.Price, NormalPrice = current.NormalPrice, UnitPrice = current.UnitPrice,
            SpecialType = current.SpecialType, SpecialDescription = current.SpecialDescription,
            SpecialStartDate = current.SpecialStartDate, SpecialEndDate = current.SpecialEndDate,
            PriceScope = current.PriceScope, SourceType = current.SourceType, SourceUrl = current.SourceUrl,
            CheckedDate = checkedDate, CreatedDate = now, ContributionObservationId = observation
        });
        await db.SaveChangesAsync(token);
        return new(changed ? PriceUpdateStatus.Saved : PriceUpdateStatus.Unchanged, recordHistory);
    }

    private static bool Amount(decimal? value) => value is null || value >= 0 && value < 100_000_000_000_000m && decimal.Round(value.Value, 4) == value;
    private static DateTime StorageTime(DateTimeOffset value) => new(value.UtcTicks / 10 * 10, DateTimeKind.Utc);
    private static PriceUpdateResult Invalid(string code) => new(PriceUpdateStatus.InvalidOffer, ErrorCode: code);
}
