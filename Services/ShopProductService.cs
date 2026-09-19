using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Services;

public sealed class ShopProductService(MyShoppingListDbContext db)
{
    public async Task<IReadOnlyList<ShopProduct>> FindAsync(long shopId, ExtractedShopProduct source, CancellationToken token)
    {
        await ProductService.LockCatalogueAsync(db, token);
        var code = string.IsNullOrWhiteSpace(source.ShopProductCode) ? null : source.ShopProductCode.Trim();
        var url = new UriBuilder(source.ProductUrl) { Fragment = string.Empty }.Uri.AbsoluteUri;
        return await db.ShopProducts.Where(p => p.ShopId == shopId
            && (code != null && p.ShopProductCode == code || p.ProductUrl == url)).ToListAsync(token);
    }

    public ShopProduct Save(Shop shop, Product product, ShopProduct? existing, ExtractedShopProduct source, int confidence)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Mapping writes require a transaction.");
        var checkedDate = source.CheckedDate.UtcDateTime;
        var mapping = existing ?? new ShopProduct
        {
            ShopId = shop.Id, Product = product, FirstFoundDate = checkedDate,
            MatchType = MatchType.Exact, MatchConfidence = confidence
        };
        if (existing is null) db.ShopProducts.Add(mapping);
        if (existing?.LastCheckedDate > checkedDate) return mapping;
        mapping.ShopProductCode ??= string.IsNullOrWhiteSpace(source.ShopProductCode) ? null : source.ShopProductCode.Trim();
        mapping.ShopSku = source.Sku ?? mapping.ShopSku;
        mapping.NameAtShop = source.Identity.Name;
        mapping.DescriptionAtShop = source.Description ?? mapping.DescriptionAtShop;
        mapping.ProductUrl = new UriBuilder(source.ProductUrl) { Fragment = string.Empty }.Uri.AbsoluteUri;
        mapping.ImageUrl = source.ImageUrl?.AbsoluteUri ?? mapping.ImageUrl;
        mapping.GTIN = source.Identity.GTIN ?? mapping.GTIN;
        mapping.LastFoundDate = checkedDate;
        mapping.LastCheckedDate = checkedDate;
        mapping.IsActive = true;
        return mapping;
    }
}
