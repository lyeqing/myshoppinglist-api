using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using myshoppinglist_api.Configuration;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class BrandAlternativeService(MyShoppingListDbContext db, TimeProvider clock, IOptions<AdminOptions> administrators)
{
    private async Task AuthorizeAsync(long administrator, CancellationToken ct)
    {
        if (!administrators.Value.AccountIds.Contains(administrator)
            || !await db.UserAccounts.AnyAsync(a => a.Id == administrator && a.IsActive && !a.IsTrial, ct))
            throw new UserImportException(403, "Administrator access required.");
    }

    public async Task<AdminAlternativeProductSearchResult> SearchAsync(long administrator, string? search, CancellationToken ct)
    {
        await AuthorizeAsync(administrator, ct);
        var term = search?.Trim() ?? "";
        if (search is not null && term.Length == 0 && search.Length <= 100) return new([], false);
        var numeric = long.TryParse(term, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0;
        if (search is null || search.Length > 100 || term.Length < 2 && !numeric || term.Any(char.IsControl))
            throw new UserImportException(400, "Enter at least two characters or a positive product ID, using at most 100 characters.");
        var lower = term.ToLowerInvariant();
        // Literal substring search: punctuation and SQL wildcard characters are not
        // treated as patterns. Retired records stay searchable for withdrawal.
        var products = await db.Products.AsNoTracking()
            .Where(p => numeric ? p.Id == id : p.Name.ToLower().Contains(lower) || p.Brand != null && p.Brand.ToLower().Contains(lower))
            .OrderBy(p => p.IsDeleted).ThenBy(p => p.Name).ThenBy(p => p.Id).Take(51)
            .Select(p => new AdminAlternativeProductSearchItem(new(p.Id, p.Name, p.Brand, p.Variant,
                p.PackQuantity, p.PackSize, p.PackUnit, p.ImageUrl), p.Category, p.SubCategory, p.IsDeleted)).ToArrayAsync(ct);
        return new(products.Take(50).ToArray(), products.Length > 50);
    }

    private static (long First, long Second) Pair(long first, long second)
    {
        if (first <= 0 || second <= 0 || first == second)
            throw new UserImportException(400, "Choose two different saved products.");
        return first < second ? (first, second) : (second, first);
    }

    // Persist the evidence-bearing fields, not UpdatedDate: price refreshes should not
    // invalidate a review, but changed names, formulation descriptions or identity must.
    private static string Fingerprint(Product product) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new
        {
            product.Name,
            product.Brand,
            product.Description,
            product.ItemDetail,
            product.GTIN,
            product.ManufacturerPartNumber,
            product.ModelNumber,
            product.Variant,
            product.PackQuantity,
            product.PackSize,
            product.PackUnit,
            product.Category,
            product.SubCategory,
            product.IsDeleted
        }))));

    private static bool Compatible(Product first, Product second)
    {
        if (first.IsDeleted || second.IsDeleted) return false;
        var normalisation = new ProductNormalisationService();
        var a = normalisation.Normalise(ProductMatchingService.Identity(first));
        var b = normalisation.Normalise(ProductMatchingService.Identity(second));
        // Missing attributes are not evidence of compatibility. A human must also
        // explicitly review use/format and dietary/composition suitability.
        if (string.IsNullOrEmpty(a.Brand) || string.IsNullOrEmpty(b.Brand) || a.Brand == b.Brand
            || a.Variant != b.Variant || a.ModelNumber != b.ModelNumber
            || string.IsNullOrWhiteSpace(first.Category) || string.IsNullOrWhiteSpace(second.Category)
            || normalisation.Text(first.Category) != normalisation.Text(second.Category)
            || string.IsNullOrWhiteSpace(first.SubCategory) || string.IsNullOrWhiteSpace(second.SubCategory)
            || normalisation.Text(first.SubCategory) != normalisation.Text(second.SubCategory)
            || a.GTIN is not null && a.GTIN == b.GTIN) return false;
        var packs = new PackComparisonService();
        var x = packs.Read(ProductMatchingService.Identity(first));
        var y = packs.Read(ProductMatchingService.Identity(second));
        return x is not null && y is not null && x.Unit == y.Unit
            && y.Total / x.Total is >= 0.1m and <= 10m;
    }

    private static bool Current(ProductAlternativeRelationship review, Product first, Product second) =>
        review.Approved && review.SameUseAndFormatConfirmed && review.DietaryAndCompositionChecked
        && review.ProductFingerprint == Fingerprint(first) && review.AlternativeFingerprint == Fingerprint(second)
        && Compatible(first, second);

    private static BrandAlternativeReviewResponse Response(ProductAlternativeRelationship review, Product first, Product second) =>
        new(review.Id, review.ProductId, review.AlternativeProductId, review.PreviousReviewId, review.Approved,
            review.Evidence, review.SameUseAndFormatConfirmed, review.DietaryAndCompositionChecked,
            review.ReviewerAccountId, review.ReviewedAt, review.Approved && !Current(review, first, second));

    public async Task<BrandAlternativeReviewResponse[]> HistoryAsync(long administrator, long productId, long alternativeProductId, CancellationToken ct) =>
        (await ContextAsync(administrator, productId, alternativeProductId, ct)).Reviews;

    public async Task<BrandAlternativeReviewContext> ContextAsync(long administrator, long productId, long alternativeProductId, CancellationToken ct)
    {
        await AuthorizeAsync(administrator, ct);
        var pair = Pair(productId, alternativeProductId);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var products = await db.Products.AsNoTracking().Where(p => p.Id == pair.First || p.Id == pair.Second).ToArrayAsync(ct);
        if (products.Length != 2) throw new UserImportException(404, "Product not found.");
        var reviews = await db.ProductAlternativeRelationships.AsNoTracking()
            .Where(r => r.ProductId == pair.First && r.AlternativeProductId == pair.Second)
            .OrderByDescending(r => r.Id).Take(100).ToArrayAsync(ct);
        BrandAlternativeReviewProduct Details(Product p) => new(new(p.Id, p.Name, p.Brand, p.Variant, p.PackQuantity,
            p.PackSize, p.PackUnit, p.ImageUrl), p.Category, p.SubCategory, p.Description, p.ItemDetail,
            p.GTIN, p.ManufacturerPartNumber, p.ModelNumber, p.IsDeleted, Fingerprint(p));
        var first = products.Single(p => p.Id == pair.First);
        var second = products.Single(p => p.Id == pair.Second);
        var result = new BrandAlternativeReviewContext(Details(first), Details(second), reviews.Select(r => Response(r, first, second)).ToArray());
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<BrandAlternativeReviewResponse> ReviewAsync(long administrator, long productId, long alternativeProductId,
        BrandAlternativeReviewRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(administrator, ct);
        var pair = Pair(productId, alternativeProductId);
        if (request.ExpectedReviewId < 0 || string.IsNullOrWhiteSpace(request.Evidence) || request.Evidence.Length > 2000)
            throw new UserImportException(400, "Enter review evidence of at most 2000 characters and a valid expected review ID.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Match catalogue writes and serialize concurrent first reviews and reversals.
        await ProductService.LockCatalogueAsync(db, ct);
        var products = await db.Products.AsNoTracking().Where(p => p.Id == pair.First || p.Id == pair.Second).ToArrayAsync(ct);
        if (products.Length != 2) throw new UserImportException(404, "Product not found.");
        var first = products.Single(p => p.Id == pair.First);
        var second = products.Single(p => p.Id == pair.Second);
        var previous = await db.ProductAlternativeRelationships.AsNoTracking()
            .Where(r => r.ProductId == pair.First && r.AlternativeProductId == pair.Second).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        if ((previous?.Id ?? 0) != request.ExpectedReviewId)
            throw new UserImportException(409, "This relationship changed. Reload its review history before saving.");
        if (request.Approved && (string.IsNullOrEmpty(request.ExpectedProductFingerprint) || string.IsNullOrEmpty(request.ExpectedAlternativeFingerprint)))
            throw new UserImportException(400, "Load the product review context before approving a relationship.");
        if (request.Approved && (request.ExpectedProductFingerprint != Fingerprint(first) || request.ExpectedAlternativeFingerprint != Fingerprint(second)))
            throw new UserImportException(409, "Product details changed. Reload and review both products before approving.");
        if (request.Approved && (!request.SameUseAndFormatConfirmed || !request.DietaryAndCompositionChecked || !Compatible(first, second)))
            throw new UserImportException(400, "Approval requires compatible products from different brands, clear pack sizes, matching categories and variants, and both suitability confirmations.");
        if (!request.Approved && previous is null)
            throw new UserImportException(400, "There is no reviewed relationship to withdraw.");
        var review = new ProductAlternativeRelationship
        {
            ProductId = pair.First,
            AlternativeProductId = pair.Second,
            PreviousReviewId = previous?.Id,
            Approved = request.Approved,
            Evidence = request.Evidence.Trim(),
            SameUseAndFormatConfirmed = request.SameUseAndFormatConfirmed,
            DietaryAndCompositionChecked = request.DietaryAndCompositionChecked,
            ProductFingerprint = Fingerprint(first),
            AlternativeFingerprint = Fingerprint(second),
            ReviewerAccountId = administrator,
            ReviewedAt = new DateTime(clock.GetUtcNow().UtcTicks / 10 * 10, DateTimeKind.Utc)
        };
        db.ProductAlternativeRelationships.Add(review);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Response(review, first, second);
    }

    // Catalogue-only query shared by planning and in-store views. This method cannot
    // dispatch retailer work and never returns reviewer identity or private evidence.
    public static async Task<Dictionary<long, BrandAlternative[]>> ReadForProductsAsync(MyShoppingListDbContext db,
        TimeProvider clock, long accountId, Product[] originals, CancellationToken ct, bool respectDisplayPreference = true)
    {
        var result = new Dictionary<long, BrandAlternative[]>();
        var now = clock.GetUtcNow();
        if (!await db.UserAccounts.AnyAsync(a => a.Id == accountId && a.IsActive && (!respectDisplayPreference || a.IncludeDifferentBrands)
            && (!a.IsTrial || a.ExpiresDate > now.UtcDateTime), ct)) return result;
        result = await ReadSuggestionsAsync(db, clock, accountId, originals, ct);
        var originalIds = originals.Select(p => p.Id).Distinct().ToArray();
        if (originalIds.Length == 0) return result;
        var reviews = await db.ProductAlternativeRelationships.AsNoTracking()
            .Where(r => originalIds.Contains(r.ProductId) || originalIds.Contains(r.AlternativeProductId))
            .GroupBy(r => new { r.ProductId, r.AlternativeProductId })
            .Select(g => g.OrderByDescending(r => r.Id).First()).ToArrayAsync(ct);
        var ids = reviews.SelectMany(r => new[] { r.ProductId, r.AlternativeProductId }).Distinct().ToArray();
        if (ids.Length == 0) return result;
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var approved = reviews.Where(r => Current(r, products[r.ProductId], products[r.AlternativeProductId])).ToArray();
        ids = approved.SelectMany(r => new[] { r.ProductId, r.AlternativeProductId }).Distinct().ToArray();
        if (ids.Length == 0) return result;
        var allowed = ShoppingPreferencesService.AllowedShops(db, accountId).Select(s => s.Id);
        var mappings = await db.ShopProducts.AsNoTracking().Include(m => m.Shop).Where(m => ids.Contains(m.ProductId)
            && m.IsActive && m.Shop.IsActive && m.MatchType == MatchType.Exact && allowed.Contains(m.ShopId)
            && (m.Shop.Code == "coles" || m.Shop.Code == "woolworths" || originalIds.Contains(m.ProductId) && m.Shop.Code == "aldi")).ToArrayAsync(ct);
        var mappingIds = mappings.Select(m => m.Id).ToArray();
        var observations = await db.ShopProductPrices.AsNoTracking().Where(p => mappingIds.Contains(p.ShopProductId)
            && p.ShopLocationId == null).GroupBy(p => p.ShopProductId)
            .Select(g => g.OrderByDescending(p => p.CheckedDate).ThenByDescending(p => p.Id).First()).ToArrayAsync(ct);
        var prices = observations.ToDictionary(p => p.ShopProductId);
        var freshness = new CatalogueFreshnessService();
        var packs = new PackComparisonService();
        PackComparisonPrice Price(ShopProduct mapping, PackComparisonService.Quantity quantity)
        {
            prices.TryGetValue(mapping.Id, out var price);
            var status = price?.CheckedDate > now.UtcDateTime ? "Unverified price date"
                : InStoreShoppingService.PriceStatus(price, now, freshness);
            var amount = price is { Currency: "AUD", Price: >= 0 } ? price.Price : (decimal?)null;
            return new(mapping.ShopId, mapping.Shop.Name, amount,
                amount.HasValue ? PackComparisonService.UnitPrice(amount.Value, quantity) : null,
                status, price?.CheckedDate, mapping.ProductUrl, PriceObservationContextService.From(price, mapping.Shop.Code));
        }
        IEnumerable<ShopProduct> UniqueMappings(long id) => mappings.Where(m => m.ProductId == id)
            .GroupBy(m => m.ShopId).Where(g => g.Count() == 1).Select(g => g.Single());
        foreach (var originalId in originalIds)
        {
            var relationships = approved.Where(r => r.ProductId == originalId || r.AlternativeProductId == originalId).ToArray();
            if (relationships.Length == 0) continue;
            var original = products[originalId];
            var quantity = packs.Read(ProductMatchingService.Identity(original))!;
            var baseline = UniqueMappings(originalId).Select(m => Price(m, quantity))
                .Where(p => p.Price.HasValue).OrderBy(p => p.Status != "Fresh").ThenBy(p => p.Status == "Fresh" ? p.Price : null).ThenByDescending(p => p.CheckedDate).ThenBy(p => p.ShopId).FirstOrDefault();
            var alternatives = new List<BrandAlternative>();
            foreach (var relationship in relationships)
            {
                var candidate = products[relationship.ProductId == originalId ? relationship.AlternativeProductId : relationship.ProductId];
                var candidateQuantity = packs.Read(ProductMatchingService.Identity(candidate))!;
                foreach (var mapping in UniqueMappings(candidate.Id).Where(m => m.Shop.Code == "coles" || m.Shop.Code == "woolworths"))
                {
                    var offer = Price(mapping, candidateQuantity);
                    if (offer.Price is null) continue;
                    var comparable = baseline is { Status: "Fresh" } && offer.Status == "Fresh";
                    alternatives.Add(new("EquivalentDifferentBrand", "Similar product — different brand",
                        new(candidate.Id, candidate.Name, candidate.Brand, candidate.Variant, candidate.PackQuantity, candidate.PackSize, candidate.PackUnit, candidate.ImageUrl),
                        candidateQuantity.Total, candidateQuantity.Unit, candidateQuantity.Total / quantity.Total, offer, baseline,
                        comparable ? PackComparisonService.DifferencePercent(baseline!.UnitPrice!.Value, offer.UnitPrice!.Value) : null,
                        comparable ? offer.Price - baseline!.Price : null));
                }
            }
            // Only comparable observations rank by unit price; historical offers remain labelled.
            result[originalId] = alternatives.Concat(result.GetValueOrDefault(originalId) ?? [])
                .DistinctBy(a => (a.Product.Id, a.Offer.ShopId)).OrderBy(a => a.Offer.Status != "Fresh")
                .ThenBy(a => a.Offer.Status == "Fresh" ? a.Offer.UnitPrice : null)
                .ThenBy(a => a.Product.Name).ThenBy(a => a.Offer.ShopId).ToArray();
        }
        return result;
    }
    private static async Task<Dictionary<long, BrandAlternative[]>> ReadSuggestionsAsync(MyShoppingListDbContext db,
        TimeProvider clock, long accountId, Product[] originals, CancellationToken ct)
    {
        var result = new Dictionary<long, BrandAlternative[]>();
        var ids = originals.Select(p => p.Id).ToArray(); var now = clock.GetUtcNow();
        var allowed = ShoppingPreferencesService.AllowedShops(db, accountId).Select(s => s.Id);
        var suggestions = await db.ProductBrandSuggestions.AsNoTracking()
            .Include(s => s.BrandDiscoveryJob).ThenInclude(j => j.ShoppingList)
            .Include(s => s.AlternativeProduct).Include(s => s.ShopProduct).ThenInclude(m => m.Shop)
            .Where(s => s.BrandDiscoveryJob.UserAccountId == accountId && ids.Contains(s.BrandDiscoveryJob.ProductId)
                && s.BrandDiscoveryJob.Status != "Cancelled" && !s.BrandDiscoveryJob.ShoppingList.IsArchived
                && (s.BrandDiscoveryJob.ShoppingList.ExpiresDate == null || s.BrandDiscoveryJob.ShoppingList.ExpiresDate > now.UtcDateTime)
                && !s.AlternativeProduct.IsDeleted && s.ShopProduct.IsActive && s.ShopProduct.Shop.IsActive
                && s.ShopProduct.ProductId == s.AlternativeProductId && s.ShopProduct.ShopId == s.BrandDiscoveryJob.ShopId
                && s.ShopProduct.MatchType == MatchType.Exact && allowed.Contains(s.ShopProduct.ShopId)
                && (s.ShopProduct.Shop.Code == "coles" || s.ShopProduct.Shop.Code == "woolworths")
                // An explicit review (including withdrawal) takes precedence over discovery.
                && !db.ProductAlternativeRelationships.Any(r =>
                    r.ProductId == s.BrandDiscoveryJob.ProductId && r.AlternativeProductId == s.AlternativeProductId
                    || r.AlternativeProductId == s.BrandDiscoveryJob.ProductId && r.ProductId == s.AlternativeProductId))
            .ToArrayAsync(ct);
        var originalMappings = await db.ShopProducts.AsNoTracking().Include(m => m.Shop).Where(m => ids.Contains(m.ProductId)
            && m.IsActive && m.Shop.IsActive && m.MatchType == MatchType.Exact && allowed.Contains(m.ShopId)).ToArrayAsync(ct);
        var mappingIds = suggestions.Select(s => s.ShopProductId).Concat(originalMappings.Select(m => m.Id)).Distinct().ToArray();
        var prices = await db.ShopProductPrices.AsNoTracking().Where(p => mappingIds.Contains(p.ShopProductId) && p.ShopLocationId == null)
            .GroupBy(p => p.ShopProductId).Select(g => g.OrderByDescending(p => p.CheckedDate).ThenByDescending(p => p.Id).First()).ToDictionaryAsync(p => p.ShopProductId, ct);
        var packs = new PackComparisonService(); var matcher = new BrandSuggestionMatchingService();
        foreach (var original in originals)
        {
            var quantity = packs.Read(ProductMatchingService.Identity(original)); if (quantity is null) continue;
            var originalOffers = originalMappings.Where(m => m.ProductId == original.Id).GroupBy(m => m.ShopId)
                .Where(g => g.Count() == 1).Select(g => g.Single()).Where(m => prices.ContainsKey(m.Id)).Select(m =>
                {
                    var p = prices[m.Id];
                    var state = p.CheckedDate > now.UtcDateTime ? "Unverified price date" : InStoreShoppingService.PriceStatus(p, now, new CatalogueFreshnessService());
                    var amount = p.Currency == "AUD" && p.Price >= 0 ? p.Price : (decimal?)null;
                    return new PackComparisonPrice(m.ShopId, m.Shop.Name, amount, amount.HasValue ? PackComparisonService.UnitPrice(amount.Value, quantity) : null,
                        state, p.CheckedDate, m.ProductUrl, PriceObservationContextService.From(p, m.Shop.Code));
                }).Where(o => o.Price.HasValue).OrderBy(o => o.Status != "Fresh").ThenByDescending(o => o.CheckedDate).ToArray();
            var baseline = originalOffers.FirstOrDefault();
            var rows = new List<BrandAlternative>();
            foreach (var suggestion in suggestions.Where(s => s.BrandDiscoveryJob.ProductId == original.Id).DistinctBy(s => s.ShopProductId))
            {
                var candidate = suggestion.AlternativeProduct; var mapping = suggestion.ShopProduct;
                if (!matcher.IsSuggestion(ProductMatchingService.Identity(original), ProductMatchingService.Identity(candidate))
                    || !prices.TryGetValue(mapping.Id, out var price) || price.Currency != "AUD" || price.Price < 0) continue;
                var size = packs.Read(ProductMatchingService.Identity(candidate))!;
                var status = price.CheckedDate > now.UtcDateTime ? "Unverified price date" : InStoreShoppingService.PriceStatus(price, now, new CatalogueFreshnessService());
                var offer = new PackComparisonPrice(mapping.ShopId, mapping.Shop.Name, price.Price, PackComparisonService.UnitPrice(price.Price, size), status,
                    price.CheckedDate, mapping.ProductUrl, PriceObservationContextService.From(price, mapping.Shop.Code));
                rows.Add(new("SuggestedDifferentBrand", "Suggested alternative — check suitability",
                    new(candidate.Id, candidate.Name, candidate.Brand, candidate.Variant, candidate.PackQuantity, candidate.PackSize, candidate.PackUnit, candidate.ImageUrl),
                    size.Total, size.Unit, size.Total / quantity.Total, offer, baseline,
                    baseline is { Status: "Fresh", UnitPrice: not null } && status == "Fresh" ? PackComparisonService.DifferencePercent(baseline.UnitPrice.Value, offer.UnitPrice!.Value) : null,
                    baseline is { Status: "Fresh", Price: not null } && status == "Fresh" ? offer.Price - baseline.Price : null));
            }
            if (rows.Count > 0) result[original.Id] = rows.OrderBy(a => a.Offer.Status != "Fresh").ThenBy(a => a.Product.Name).ThenBy(a => a.Offer.ShopId).ToArray();
        }
        return result;
    }

}
