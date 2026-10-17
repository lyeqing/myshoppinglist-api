using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class PackAlternativeService(MyShoppingListDbContext db, TimeProvider clock)
{
    public async Task<Dictionary<long, PackAlternative[]>> ReadAsync(long accountId, Product[] originals, CancellationToken token)
    {
        var result = new Dictionary<long, PackAlternative[]>();
        var now = clock.GetUtcNow();
        if (!await db.UserAccounts.AnyAsync(a => a.Id == accountId && a.IsActive && a.IncludeDifferentPackSizes
            && (!a.IsTrial || a.ExpiresDate > now.UtcDateTime), token)) return result;
        var brands = originals.Where(p => !string.IsNullOrWhiteSpace(p.Brand)).Select(p => p.Brand!.Trim().ToLowerInvariant()).Distinct().ToArray();
        if (brands.Length == 0) return result;
        // Catalogue-only discovery: these reads never initiate retailer collection.
        var candidates = await db.Products.AsNoTracking().Where(p => !p.IsDeleted && p.Brand != null
            && brands.Contains(p.Brand.Trim().ToLower())).ToArrayAsync(token);
        var matcher = new ProductFamilyMatchingService();
        var packs = new PackComparisonService();
        // Parse each catalogue title once before checking the small set sharing its
        // residual product wording, rather than comparing every pair in a brand.
        var byFamily = candidates.Select(p => (Product: p, Quantity: packs.Read(ProductMatchingService.Identity(p))))
            .Where(p => p.Quantity is not null).GroupBy(p => p.Quantity!.FamilyName)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Product).ToArray());
        var families = originals.DistinctBy(p => p.Id).ToDictionary(p => p.Id, p =>
        {
            var quantity = packs.Read(ProductMatchingService.Identity(p));
            return quantity is null ? [] : byFamily.GetValueOrDefault(quantity.FamilyName, [])
                .Where(c => c.Id != p.Id && matcher.IsDifferentSize(ProductMatchingService.Identity(p), ProductMatchingService.Identity(c))).ToArray();
        });
        var ids = families.Where(f => f.Value.Length > 0).SelectMany(f => f.Value.Select(p => p.Id).Append(f.Key)).Distinct().ToArray();
        if (ids.Length == 0) return result;
        var allowed = ShoppingPreferencesService.AllowedShops(db, accountId).Select(s => s.Id);
        var mappings = await db.ShopProducts.AsNoTracking().Include(m => m.Shop).Where(m => ids.Contains(m.ProductId)
            && m.IsActive && m.Shop.IsActive && m.MatchType == MatchType.Exact && allowed.Contains(m.ShopId)).ToArrayAsync(token);
        var mappingIds = mappings.Select(m => m.Id).ToArray();
        var observations = await db.ShopProductPrices.AsNoTracking().Where(p => mappingIds.Contains(p.ShopProductId)
            && p.ShopLocationId == null).GroupBy(p => p.ShopProductId)
            .Select(g => g.OrderByDescending(p => p.CheckedDate).ThenByDescending(p => p.Id).First()).ToArrayAsync(token);
        var prices = observations.ToDictionary(p => p.ShopProductId);
        var freshness = new CatalogueFreshnessService();
        PackComparisonPrice Price(ShopProduct mapping, PackComparisonService.Quantity quantity)
        {
            prices.TryGetValue(mapping.Id, out var price);
            var status = price?.CheckedDate > now.UtcDateTime ? "Unverified price date" : InStoreShoppingService.PriceStatus(price, now, freshness);
            // Multibuy unit savings require buying several packages. Compare ordinary
            // single-package prices here, keeping those offer conditions separate.
            var amount = price is { Currency: "AUD", Price: >= 0 } ? price.Price : (decimal?)null;
            return new(mapping.ShopId, mapping.Shop.Name, amount,
                amount.HasValue ? PackComparisonService.UnitPrice(amount.Value, quantity) : null,
                status, price?.CheckedDate, mapping.ProductUrl);
        }
        foreach (var product in originals.DistinctBy(p => p.Id))
        {
            if (families[product.Id].Length == 0) continue;
            var originalQuantity = packs.Read(ProductMatchingService.Identity(product))!;
            var baseline = mappings.Where(m => m.ProductId == product.Id).GroupBy(m => m.ShopId).Where(g => g.Count() == 1).Select(g => Price(g.Single(), originalQuantity))
                .Where(p => p.Status == "Fresh" && p.Price.HasValue).OrderBy(p => p.Price).ThenBy(p => p.ShopId).FirstOrDefault();
            var alternatives = new List<PackAlternative>();
            foreach (var candidate in families[product.Id])
            {
                var quantity = packs.Read(ProductMatchingService.Identity(candidate))!;
                // Conflicting active mappings for a retailer are not a verified offer.
                foreach (var group in mappings.Where(m => m.ProductId == candidate.Id).GroupBy(m => m.ShopId))
                {
                    if (group.Count() != 1) continue;
                    var offer = Price(group.Single(), quantity);
                    if (offer.Price is null) continue;
                    var comparable = baseline is not null && offer.Status == "Fresh";
                    alternatives.Add(new("SameProductDifferentSize", new(candidate.Id, candidate.Name, candidate.Brand,
                        candidate.Variant, candidate.PackQuantity, candidate.PackSize, candidate.PackUnit, candidate.ImageUrl),
                        quantity.Total, quantity.Unit, quantity.Total / originalQuantity.Total, offer, baseline,
                        comparable ? PackComparisonService.DifferencePercent(baseline!.UnitPrice!.Value, offer.UnitPrice!.Value) : null,
                        comparable ? offer.Price - baseline!.Price : null));
                }
            }
            result[product.Id] = alternatives.OrderBy(a => a.TotalQuantity).ThenBy(a => a.Offer.ShopName).ToArray();
        }
        return result;
    }
}
