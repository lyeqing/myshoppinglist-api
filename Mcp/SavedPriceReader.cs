using System.Globalization;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Services;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Mcp;

// Product IDs come only from an already ownership-checked list or private-library read.
public sealed class SavedPriceReader(MyShoppingListDbContext db, TimeProvider clock)
{
    public async Task<Dictionary<long, McpSavedPrice[]>> ReadAsync(long accountId, long[] productIds, CancellationToken ct)
    {
        var mappings = await db.ShopProducts.AsNoTracking().Include(m => m.Shop)
            .Where(m => productIds.Contains(m.ProductId) && m.IsActive && m.Shop.IsActive && !m.Product.IsDeleted
                && m.UserAccountId == m.Product.UserAccountId && (m.UserAccountId == null || m.UserAccountId == accountId))
            .ToArrayAsync(ct);
        var mappingIds = mappings.Select(m => m.Id).ToArray();
        var observations = await db.ShopProductPrices.AsNoTracking().Include(p => p.ShopLocation)
            .Where(p => mappingIds.Contains(p.ShopProductId) && p.UserAccountId == p.ShopProduct.UserAccountId).ToArrayAsync(ct);
        var freshness = new CatalogueFreshnessService();
        return mappings.GroupBy(m => m.ProductId).ToDictionary(g => g.Key, g => g.SelectMany(m => observations
            .Where(p => p.ShopProductId == m.Id).GroupBy(p => p.ShopLocationId).Select(location =>
            {
                var p = location.OrderByDescending(p => p.CheckedDate).ThenByDescending(p => p.Id).First();
                var source = PriceObservationContextService.From(p, m.Shop.Code)!;
                var status = m.MatchType != MatchType.Exact ? "match_unverified"
                    : p.ShopLocationId is not null ? "location_specific_saved_observation"
                    : InStoreShoppingService.PriceStatus(p, clock.GetUtcNow(), freshness) == "Fresh" ? "recent_saved_observation" : "stale_or_unavailable_saved_observation";
                return new McpSavedPrice(m.ShopId.ToString(CultureInfo.InvariantCulture), m.Shop.Name,
                    p.ShopLocationId?.ToString(CultureInfo.InvariantCulture), p.ShopLocation?.Name,
                    p is { Currency: "AUD", Price: >= 0 } ? p.Price : null, p.Currency,
                    p is { Currency: "AUD", UnitPrice: >= 0 } ? p.UnitPrice : null, "unknown",
                    p.CheckedDate, status, p.SpecialDescription, source.SourceType.ToString(), source.PriceScope.ToString(), source.SourceUrl, m.MatchType == MatchType.Exact);
            })).OrderBy(p => p.StoreId).ThenBy(p => p.LocationId).ToArray());
    }
}
