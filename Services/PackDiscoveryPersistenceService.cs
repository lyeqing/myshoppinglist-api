using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Security;
using myshoppinglist_api.Services.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class PackDiscoveryPersistenceService(MyShoppingListDbContext db, PackDiscoveryService jobs,
    ProductService products, ShopProductService mappings, PriceService prices, ProductUrlValidator urls, TimeProvider clock)
{
    public async Task<bool> SaveAsync(PackDiscoveryClaim claim, ExtractedShopProduct source, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        try
        {
            await ProductService.LockCatalogueAsync(db, token);
            var job = await jobs.ReadClaimAsync(claim, token);
            if (job is null) return false;
            await db.UserAccounts.FromSqlInterpolated($"SELECT * FROM \"UserAccounts\" WHERE \"Id\" = {job.UserAccountId} FOR UPDATE").SingleOrDefaultAsync(token);
            await db.ShoppingLists.FromSqlInterpolated($"SELECT * FROM \"ShoppingLists\" WHERE \"Id\" = {job.ShoppingListId} FOR UPDATE").SingleOrDefaultAsync(token);
            await db.ShoppingListProducts.FromSqlInterpolated($"SELECT * FROM \"ShoppingListProducts\" WHERE \"Id\" = {job.ShoppingListProductId} FOR UPDATE").SingleOrDefaultAsync(token);
            var locked = await db.PackDiscoveryJobs.FromSqlInterpolated($"SELECT * FROM \"PackDiscoveryJobs\" WHERE \"Id\" = {claim.Id} FOR UPDATE").SingleOrDefaultAsync(token);
            if (locked is null || locked.Status != "Processing" || locked.ClaimToken != claim.Token || locked.LeaseExpiresDate <= clock.GetUtcNow().UtcDateTime
                || !await jobs.EligibleAsync(job, token)) return false;
            var original = await db.Products.SingleAsync(p => p.Id == job.ProductId, token);
            var shop = await db.Shops.SingleAsync(s => s.Id == job.ShopId && s.IsActive, token);
            var validation = urls.Validate(source.ProductUrl.OriginalString);
            if (!validation.IsValid || validation.Retailer!.Code != shop.Code || source.ShopCode != shop.Code
                || !new ProductFamilyMatchingService().IsDifferentSize(ProductMatchingService.Identity(original), source.Identity)
                || source.CheckedDate == default || source.CheckedDate > clock.GetUtcNow()
                || source.Identity.Name.Length is 0 or > 500 || source.Identity.Brand?.Length > 200
                || source.ShopProductCode?.Length > 200 || source.Sku?.Length > 200 || source.Description?.Length > 10000
                || source.Identity.GTIN?.Length > 14 || !Enum.IsDefined(source.SourceType)
                || source.ImageUrl is { } image && (!image.IsAbsoluteUri || image.Scheme != "https" || image.UserInfo.Length > 0 || image.AbsoluteUri.Length > 2048)
                || source.Offer is not ProviderResult<ShopProductOffer>.Success offer || offer.Value.SourceUrl != source.ProductUrl) return false;
            var known = await mappings.FindAsync(shop.Id, source, token);
            if (known.Count > 1 || known.Any(m => m.ProductId == original.Id || m.MatchType != MatchType.Exact)) return false;
            var resolved = await products.ResolveAsync(source, known.SingleOrDefault(), clock.GetUtcNow().UtcDateTime, token);
            if (resolved.Status != ProductPersistenceStatus.Success || resolved.Product is null || resolved.Product.Id == original.Id
                || !new ProductFamilyMatchingService().IsDifferentSize(ProductMatchingService.Identity(original), ProductMatchingService.Identity(resolved.Product))) return false;
            var product = resolved.Product;
            if (await db.ShopProducts.AnyAsync(m => m.ProductId == product.Id && m.ShopId == shop.Id && m.IsActive
                && m.MatchType == MatchType.Exact && !known.Select(k => k.Id).Contains(m.Id), token)) return false;
            var mapping = mappings.Save(shop, product, known.SingleOrDefault(), source, resolved.MatchConfidence);
            await db.SaveChangesAsync(token);
            var price = await prices.SaveAsync(mapping, offer.Value, token);
            if (price.Status == PriceUpdateStatus.InvalidOffer || !await jobs.EligibleAsync(job, token)
                || locked.LeaseExpiresDate <= clock.GetUtcNow().UtcDateTime) return false;
            await db.SaveChangesAsync(token); await tx.CommitAsync(token);
            return true;
        }
        finally { db.ChangeTracker.Clear(); }
    }
}
