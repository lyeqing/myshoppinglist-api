using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class InStoreShoppingService(MyShoppingListDbContext db, TimeProvider clock, CatalogueFreshnessService freshness)
{
    private IQueryable<ShoppingList> Accessible(long accountId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.ShoppingLists.AsNoTracking().Where(l => l.UserAccountId == accountId && !l.IsArchived
            && (l.ExpiresDate == null || l.ExpiresDate > now) && l.UserAccount.IsActive
            && (!l.UserAccount.IsTrial || l.UserAccount.ExpiresDate > now));
    }

    public async Task<InStoreList[]> ListsAsync(long accountId, CancellationToken token)
    {
        var lists = await Accessible(accountId).OrderByDescending(l => l.UpdatedDate).ToArrayAsync(token);
        var ids = lists.Select(l => l.Id).ToArray();
        var items = await db.ShoppingListProducts.AsNoTracking().Where(i => ids.Contains(i.ShoppingListId)
            && !i.IsHidden && !i.Product.IsDeleted).Select(i => new { i.ShoppingListId, i.ProductId, i.IsPurchased }).ToArrayAsync(token);
        var productIds = items.Select(i => i.ProductId).Distinct().ToArray();
        var mappings = await Mappings(productIds).ToArrayAsync(token);
        return lists.Select(l => new InStoreList(l.Id, l.Name, items.Count(i => i.ShoppingListId == l.Id && !i.IsPurchased),
            Retailers(mappings.Where(m => items.Any(i => i.ShoppingListId == l.Id && i.ProductId == m.ProductId))))).ToArray();
    }

    private IQueryable<ShopProduct> Mappings(long[] ids) => db.ShopProducts.AsNoTracking().Include(m => m.Shop)
        .Where(m => ids.Contains(m.ProductId) && m.IsActive && m.Shop.IsActive && m.MatchType == MatchType.Exact
            && (m.Shop.Code == "coles" || m.Shop.Code == "woolworths"));
    private static InStoreRetailer[] Retailers(IEnumerable<ShopProduct> mappings) => mappings
        .Select(m => new InStoreRetailer(m.ShopId, m.Shop.Name)).Distinct().OrderBy(s => s.Name).ToArray();

    public async Task<InStoreDetail?> DetailAsync(long accountId, long listId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var list = await Accessible(accountId).SingleOrDefaultAsync(l => l.Id == listId, token);
        if (list is null) return null;
        var items = await db.ShoppingListProducts.AsNoTracking().Include(i => i.Product)
            .Where(i => i.ShoppingListId == listId && !i.IsHidden && !i.Product.IsDeleted).OrderBy(i => i.Product.Name).ToArrayAsync(token);
        var mappings = await Mappings(items.Select(i => i.ProductId).Distinct().ToArray()).ToArrayAsync(token);
        var mappingIds = mappings.Select(m => m.Id).ToArray();
        var prices = await db.ShopProductPrices.AsNoTracking().Where(p => mappingIds.Contains(p.ShopProductId)
            && p.ShopLocationId == null).ToArrayAsync(token);
        var rows = items.Select(i => new InStoreItem(new(i.Id, i.ShoppingListId,
            new(i.Product.Id, i.Product.Name, i.Product.Brand, i.Product.Variant, i.Product.PackQuantity, i.Product.PackSize, i.Product.PackUnit, i.Product.ImageUrl),
            i.Quantity, i.Notes, i.IsPurchased, i.PurchasedDate, i.IsHidden, i.PreferredShopId, i.AddedDate, i.UpdatedDate),
            mappings.Where(m => m.ProductId == i.ProductId).GroupBy(m => m.ShopId).Select(group =>
            {
                var mapping = group.First();
                var keys = group.Select(m => m.Id).ToArray();
                var price = prices.Where(p => keys.Contains(p.ShopProductId)).OrderByDescending(p => p.CheckedDate).ThenByDescending(p => p.Id).FirstOrDefault();
                var status = PriceStatus(price, clock.GetUtcNow(), freshness);
                var offer = status == "Fresh" ? SinglePriceMultibuy(price) : null;
                return new InStorePrice(mapping.ShopId, mapping.Shop.Name, status == "Fresh" ? price!.Price : null, status, price?.CheckedDate,
                    offer, status == "Fresh" ? PromotionCalculationService.Calculate(price!.Price, i.Quantity, offer) : null);
            }).OrderBy(p => p.ShopName).ToArray())).ToArray();
        if (!await Accessible(accountId).AnyAsync(l => l.Id == listId, token)) return null;
        await transaction.CommitAsync(token);
        return Summarize(list.Id, list.Name, Retailers(mappings), rows);
    }

    public static string PriceStatus(ShopProductPrice? price, DateTimeOffset now, CatalogueFreshnessService freshness)
    {
        if (price is null) return "No price";
        if (!freshness.IsFresh(price.CheckedDate, now)) return "Stale price";
        if (price.InStock == false) return "Out of stock";
        if (price.Currency != "AUD" || price.Price < 0 || price.ShopLocationId != null) return "Not comparable";
        if (price.SpecialStartDate > now.UtcDateTime || price.SpecialEndDate < now.UtcDateTime) return "Offer not current";
        if (SinglePriceMultibuy(price) is not null) return "Fresh";
        if (Regex.IsMatch($"{price.SpecialType} {price.SpecialDescription}", @"multi.?buy|multi.?save|member|loyalty|\bbuy\s+\d|\d\s+for\b", RegexOptions.IgnoreCase)) return "Conditional offer";
        return "Fresh";
    }

    public static ShoppingListMultibuy? SinglePriceMultibuy(ShopProductPrice? price)
    {
        // Coles' parser keeps pricing.now as the single-pack price, separate from multibuy promotions.
        // Do not reinterpret unverified, member-only or other retailer offers as ordinary prices.
        if (price is not { SourceType: SourceType.RetailerPage, Currency: "AUD", Price: > 0 }
            || price.SpecialType is not ("MULTI_SAVE" or "SPECIAL")
            || !Uri.TryCreate(price.SourceUrl, UriKind.Absolute, out var url) || url.Scheme != "https"
            || url.Host is not ("www.coles.com.au" or "coles.com.au")) return null;
        var match = Regex.Match(price.SpecialDescription ?? "",
            @"\A\s*(?:(?:pick\s+any|any|buy)\s+)?(?<quantity>[2-9]|[1-9][0-9])\s+for\s+\$(?<total>[0-9]{1,6}(?:\.[0-9]{1,2})?)\s*\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var quantity = int.Parse(match.Groups["quantity"].Value, CultureInfo.InvariantCulture);
        var total = decimal.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture);
        var savings = price.Price * quantity - total;
        if (total <= 0 || savings <= 0) return null;
        return new(quantity, total, total / quantity, savings);
    }

    public static InStoreDetail Summarize(long id, string name, InStoreRetailer[] retailers, InStoreItem[] items)
    {
        var remaining = items.Where(i => !i.Item.IsPurchased && !i.Item.IsHidden).ToArray();
        var priced = remaining.Where(i => i.Prices.Any(p => p.Price.HasValue)).ToArray();
        var comparable = remaining.Where(i => retailers.Length >= 2 && retailers.All(s => i.Prices.Any(p => p.ShopId == s.Id && p.Price.HasValue))).ToArray();
        decimal LineTotal(InStoreItem i, InStorePrice p) => PromotionCalculationService.Calculate(p.Price!.Value, i.Item.Quantity, p.Multibuy).Total;
        decimal Cheapest(InStoreItem i) => i.Prices.Where(p => p.Price.HasValue).Min(p => LineTotal(i, p));
        var split = comparable.Sum(Cheapest);
        var baskets = retailers.Select(s =>
        {
            var available = remaining.Where(i => i.Prices.Any(p => p.ShopId == s.Id && p.Price.HasValue)).ToArray();
            decimal Cost(InStoreItem i) => LineTotal(i, i.Prices.Single(p => p.ShopId == s.Id));
            var total = comparable.Sum(Cost);
            return new InStoreBasket(s.Id, s.Name, available.Sum(Cost), available.Length, total, total - split);
        }).ToArray();
        return new(id, name, retailers, items, remaining.Length, baskets, priced.Sum(Cheapest), priced.Length, comparable.Length, split);
    }
}
