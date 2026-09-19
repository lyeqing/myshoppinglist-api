using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services.Models;

namespace myshoppinglist_api.Services;

public sealed class ProductService(MyShoppingListDbContext db, ProductNormalisationService normalisation,
    ProductMatchingService matching)
{
    // V1 serialises short catalogue writes, not retailer HTTP work. All persistence entry points use this lock.
    internal static Task<int> LockCatalogueAsync(MyShoppingListDbContext db, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Catalogue writes require a transaction.");
        return db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(5067596, 1)", token);
    }

    public async Task<ProductResolution> ResolveAsync(ExtractedShopProduct source, ShopProduct? knownMapping,
        DateTime now, CancellationToken token)
    {
        await LockCatalogueAsync(db, token);
        var identity = normalisation.Normalise(source.Identity);
        var exact = new List<Product>();
        if (identity.GTIN is { } gtin)
        {
            var forms = new[] { 8, 12, 13, 14 }.Select(length => gtin[^length..])
                .Where(value => normalisation.Gtin(value) == gtin).ToArray();
            var candidates = await db.Products.Where(p => !p.IsDeleted && p.GTIN != null && forms.Contains(p.GTIN)).ToListAsync(token);
            if (candidates.Any(p => matching.Match(source.Identity, ProductMatchingService.Identity(p)).Contradiction))
                return new(ProductPersistenceStatus.IdentityConflict, ErrorCode: "contradictory_gtin");
            exact.AddRange(candidates.Where(p => matching.Match(source.Identity, ProductMatchingService.Identity(p)).Type == MatchType.Exact));
        }
        if (knownMapping is not null)
        {
            var mapped = await db.Products.SingleAsync(p => p.Id == knownMapping.ProductId, token);
            var match = matching.Match(source.Identity, ProductMatchingService.Identity(mapped));
            if (mapped.IsDeleted || match.Contradiction
                || exact.Any(p => p.Id != mapped.Id))
                return new(ProductPersistenceStatus.IdentityConflict, ErrorCode: "existing_mapping_conflict");
            if (knownMapping.MatchType is not (MatchType.Exact or MatchType.UserConfirmed) && match.Type != MatchType.Exact)
                return new(ProductPersistenceStatus.IdentityConflict, ErrorCode: "unverified_mapping");
            if (knownMapping.MatchType is not (MatchType.Exact or MatchType.UserConfirmed))
            {
                knownMapping.MatchType = MatchType.Exact;
                knownMapping.MatchConfidence = match.Confidence;
            }
            Enrich(mapped, source, identity, now);
            return new(ProductPersistenceStatus.Success, mapped, MatchConfidence: knownMapping.MatchConfidence ?? 100);
        }
        if (exact.Count == 0)
        {
            IQueryable<Product> candidates = db.Products.Where(p => !p.IsDeleted);
            if (identity.ManufacturerPartNumber is not null || identity.ModelNumber is not null)
                candidates = candidates.Where(p => identity.ManufacturerPartNumber != null && p.ManufacturerPartNumber == identity.ManufacturerPartNumber
                    || identity.ModelNumber != null && p.ModelNumber == identity.ModelNumber);
            else if (identity.PackQuantity is > 0 && identity.PackSize is > 0 && identity.PackUnit is not null)
                candidates = candidates.Where(p => p.PackQuantity == identity.PackQuantity && p.PackSize == identity.PackSize && p.PackUnit == identity.PackUnit);
            else candidates = candidates.Where(p => false);

            await foreach (var candidate in candidates.AsAsyncEnumerable().WithCancellation(token))
            {
                if (matching.Match(source.Identity, ProductMatchingService.Identity(candidate)).Type == MatchType.Exact)
                    exact.Add(candidate);
                if (exact.Count > 1) break;
            }
        }
        if (exact.Count > 1) return new(ProductPersistenceStatus.IdentityConflict, ErrorCode: "ambiguous_canonical_product");
        if (exact.Count == 1)
        {
            var confidence = matching.Match(source.Identity, ProductMatchingService.Identity(exact[0])).Confidence;
            Enrich(exact[0], source, identity, now);
            return new(ProductPersistenceStatus.Success, exact[0], MatchConfidence: confidence);
        }
        var product = new Product { Name = source.Identity.Name.Trim(), CreatedDate = now, UpdatedDate = now };
        Enrich(product, source, identity, now);
        db.Products.Add(product);
        return new(ProductPersistenceStatus.Success, product);
    }

    private static void Enrich(Product product, ExtractedShopProduct source, ProductIdentity normalised, DateTime now)
    {
        // Missing observations never erase established metadata; identity contradictions are checked before enrichment.
        product.Brand ??= source.Identity.Brand?.Trim();
        product.Variant ??= source.Identity.Variant?.Trim();
        product.GTIN ??= normalised.GTIN;
        product.ManufacturerPartNumber ??= normalised.ManufacturerPartNumber;
        product.ModelNumber ??= normalised.ModelNumber;
        product.PackQuantity ??= normalised.PackQuantity;
        product.PackSize ??= normalised.PackSize;
        product.PackUnit ??= normalised.PackUnit;
        product.Description ??= source.Description;
        product.ItemDetail ??= source.ItemDetail;
        product.Category ??= source.Identity.Category;
        product.SubCategory ??= source.Identity.SubCategory;
        product.ImageUrl ??= source.ImageUrl?.AbsoluteUri;
        product.UpdatedDate = now;
    }
}
