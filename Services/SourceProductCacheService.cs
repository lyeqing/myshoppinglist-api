using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Woolworths;
using myshoppinglist_api.Providers.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class SourceProductCacheService(MyShoppingListDbContext db, CatalogueFreshnessService freshness, TimeProvider clock)
{
    public async Task<ExtractedShopProduct?> FindAsync(string shop, Uri url, CancellationToken token)
    {
        var code = shop == "coles" ? ColesProductParser.ProductCode(url) : shop == "woolworths" ? WoolworthsProductParser.ProductCode(url) : null;
        if (code is null) return null;
        var matches = await db.ShopProducts.AsNoTracking().Include(p => p.Product).Where(p => p.Shop.Code == shop
            && p.ShopProductCode == code && p.Shop.IsActive && p.IsActive && !p.Product.IsDeleted && p.MatchType == MatchType.Exact).ToListAsync(token);
        if (matches.Count != 1) return null;
        var mapping = matches[0]; var now = clock.GetUtcNow();
        if (!freshness.IsFresh(mapping.LastCheckedDate, now)) return null;
        var prices = await db.ShopProductPrices.AsNoTracking().Where(p => p.ShopProductId == mapping.Id).ToListAsync(token);
        // Do not silently substitute a store-specific observation or a stale promotion.
        if (prices.Count == 0 || prices.Any(p => !freshness.IsFresh(p.CheckedDate, now) || p.Currency != "AUD"
            || p.ShopLocationId != null || p.PriceScope is not (PriceScope.Unknown or PriceScope.Online or PriceScope.National)
            || p.SpecialEndDate <= now.UtcDateTime || p.SpecialStartDate > now.UtcDateTime)) return null;
        var price = prices.OrderByDescending(p => p.CheckedDate).First();
        return new()
        {
            ShopCode = shop, ShopProductCode = code, Sku = mapping.ShopSku, ProductUrl = new(mapping.ProductUrl),
            Identity = ProductMatchingService.Identity(mapping.Product), Description = mapping.DescriptionAtShop,
            ImageUrl = Uri.TryCreate(mapping.ImageUrl, UriKind.Absolute, out var image) ? image : null,
            CheckedDate = new(mapping.LastCheckedDate!.Value, TimeSpan.Zero), SourceType = price.SourceType,
            Offer = new ProviderResult<ShopProductOffer>.Success(new()
            {
                ShopCode = shop, SourceUrl = new(price.SourceUrl), Price = price.Price, NormalPrice = price.NormalPrice,
                UnitPrice = price.UnitPrice, Currency = price.Currency, PriceScope = price.PriceScope, SourceType = price.SourceType,
                SpecialType = price.SpecialType, SpecialDescription = price.SpecialDescription,
                SpecialStartDate = price.SpecialStartDate, SpecialEndDate = price.SpecialEndDate,
                InStock = price.InStock, CheckedDate = new(price.CheckedDate, TimeSpan.Zero)
            })
        };
    }
}
