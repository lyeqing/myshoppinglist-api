using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using myshoppinglist_api.Contracts;
using myshoppinglist_api.Data;
using myshoppinglist_api.Models;
using MatchType = myshoppinglist_api.Models.MatchType;

namespace myshoppinglist_api.Services;

public sealed class ShoppingListSubstitutionService(MyShoppingListDbContext db, TimeProvider clock)
{
    // Exclude prices and timestamps: refreshing an offer must not revoke a deliberate choice.
    private static string Fingerprint(Product p) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Identity = ProductMatchingService.Identity(p), p.Description, p.ItemDetail, p.IsDeleted }))));

    public async Task ChangeAsync(long accountId, long listId, long itemId, long shopId,
        ShoppingListSubstitutionRequest request, CancellationToken ct)
    {
        if (request.ExpectedAccountId != accountId) throw new UserImportException(409, "Your account changed. Reload your list.");
        if (request.ExpectedUpdatedDate == default || request.ExpectedUpdatedDate.Kind != DateTimeKind.Utc
            || request.Quantity is < 1 or > 999 || request.AlternativeProductId is <= 0)
            throw new UserImportException(400, "Send a valid saved item version and between 1 and 999 packages.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ProductService.LockCatalogueAsync(db, ct);
            var account = await db.UserAccounts.FromSqlInterpolated($"SELECT * FROM \"UserAccounts\" WHERE \"Id\" = {accountId} FOR UPDATE").SingleOrDefaultAsync(ct);
            var list = await db.ShoppingLists.FromSqlInterpolated($"SELECT * FROM \"ShoppingLists\" WHERE \"Id\" = {listId} AND \"UserAccountId\" = {accountId} FOR UPDATE").SingleOrDefaultAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            if (account is null || !account.IsActive || account.IsTrial && account.ExpiresDate <= now
                || list is null || list.IsArchived || list.ExpiresDate <= now)
                throw new UserImportException(404, "The shopping list was not found.");
            var item = await db.ShoppingListProducts.FromSqlInterpolated($"SELECT * FROM \"ShoppingListProducts\" WHERE \"Id\" = {itemId} AND \"ShoppingListId\" = {listId} FOR UPDATE").SingleOrDefaultAsync(ct)
                ?? throw new UserImportException(404, "The shopping list item was not found.");
            if (item.UpdatedDate != request.ExpectedUpdatedDate) throw new UserImportException(409, "This item changed. Reload before choosing an alternative.");
            var choice = await db.ShoppingListItemSubstitutions.SingleOrDefaultAsync(s => s.ShoppingListProductId == itemId && s.ShopId == shopId, ct);
            if (request.AlternativeProductId is null)
            {
                // Undo must remain possible even after preferences or eligibility change.
                if (choice is not null) db.ShoppingListItemSubstitutions.Remove(choice);
            }
            else
            {
                if (item.IsPurchased || item.IsHidden) throw new UserImportException(409, "Choose alternatives for an active, unpurchased item.");
                var original = await db.Products.AsNoTracking().SingleAsync(p => p.Id == item.ProductId, ct);
                var alternatives = await BrandAlternativeService.ReadForProductsAsync(db, clock, accountId, [original], ct);
                var alternative = alternatives.GetValueOrDefault(original.Id)?.SingleOrDefault(a => a.Product.Id == request.AlternativeProductId && a.Offer.ShopId == shopId);
                if (alternative?.Offer is not { Status: "Fresh", Price: not null })
                    throw new UserImportException(409, "This alternative no longer has an eligible current price. Reload your list.");
                var mappings = await db.ShopProducts.AsNoTracking().Where(m => m.ProductId == request.AlternativeProductId
                    && m.ShopId == shopId && m.IsActive && m.MatchType == MatchType.Exact).ToArrayAsync(ct);
                if (mappings.Length != 1) throw new UserImportException(409, "This alternative could not be verified. Reload your list.");
                var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == request.AlternativeProductId, ct);
                if (choice is null)
                {
                    choice = new() { UserAccountId = accountId, ShoppingListId = listId, ShoppingListProductId = itemId,
                        ProductId = original.Id, ShopId = shopId };
                    db.ShoppingListItemSubstitutions.Add(choice);
                }
                choice.ShopProductId = mappings[0].Id; choice.Quantity = request.Quantity;
                choice.OriginalFingerprint = Fingerprint(original); choice.AlternativeFingerprint = Fingerprint(product);
                choice.UpdatedDate = new DateTime(clock.GetUtcNow().UtcTicks / 10 * 10, DateTimeKind.Utc);
            }
            now = new DateTime(clock.GetUtcNow().UtcTicks / 10 * 10, DateTimeKind.Utc);
            if (account.IsTrial && account.ExpiresDate <= now || list.ExpiresDate <= now)
                throw new UserImportException(404, "The shopping list was not found.");
            item.UpdatedDate = now > item.UpdatedDate ? now : item.UpdatedDate.AddTicks(10);
            if (item.UpdatedDate > list.UpdatedDate) list.UpdatedDate = item.UpdatedDate;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<Dictionary<long, ShoppingListSubstitutionChoice[]>> ReadAsync(long accountId, long listId,
        ShoppingListProduct[] items, Dictionary<long, BrandAlternative[]> alternatives, CancellationToken ct)
    {
        var itemIds = items.Select(i => i.Id).ToArray(); var now = clock.GetUtcNow();
        var allowed = ShoppingPreferencesService.AllowedShops(db, accountId).Select(s => s.Id);
        var choices = await db.ShoppingListItemSubstitutions.AsNoTracking().Include(s => s.ShopProduct).ThenInclude(m => m.Product)
            .Include(s => s.Shop).Where(s => s.UserAccountId == accountId && s.ShoppingListId == listId
                && itemIds.Contains(s.ShoppingListProductId) && allowed.Contains(s.ShopId)).ToArrayAsync(ct);
        // Hiding suggestions must not revoke a choice the customer already made.
        if (choices.Length > 0 && !await db.UserAccounts.AnyAsync(a => a.Id == accountId && a.IncludeDifferentBrands, ct))
            alternatives = await BrandAlternativeService.ReadForProductsAsync(db, clock, accountId, items.Select(i => i.Product).DistinctBy(p => p.Id).ToArray(), ct, respectDisplayPreference: false);
        var mappingIds = choices.Select(s => s.ShopProductId).ToArray();
        var prices = await db.ShopProductPrices.AsNoTracking().Where(p => mappingIds.Contains(p.ShopProductId) && p.ShopLocationId == null)
            .GroupBy(p => p.ShopProductId).Select(g => g.OrderByDescending(p => p.CheckedDate).ThenByDescending(p => p.Id).First())
            .ToDictionaryAsync(p => p.ShopProductId, ct);
        return choices.GroupBy(s => s.ShoppingListProductId).ToDictionary(g => g.Key, g => g.Select(s =>
        {
            var original = items.Single(i => i.Id == s.ShoppingListProductId).Product;
            var product = s.ShopProduct.Product; var quantity = new PackComparisonService().Read(ProductMatchingService.Identity(product));
            prices.TryGetValue(s.ShopProductId, out var price);
            var eligible = s.ShopProduct.IsActive && s.Shop.IsActive && s.ShopProduct.ShopId == s.ShopId
                && s.ShopProduct.MatchType == MatchType.Exact && s.OriginalFingerprint == Fingerprint(original)
                && s.AlternativeFingerprint == Fingerprint(product)
                && (alternatives.GetValueOrDefault(original.Id) ?? []).Any(a => a.Product.Id == product.Id && a.Offer.ShopId == s.ShopId);
            var status = !eligible ? "Choice needs review" : InStoreShoppingService.PriceStatus(price, now, new CatalogueFreshnessService());
            var amount = price is { Currency: "AUD", Price: >= 0 } ? price.Price : (decimal?)null;
            var offer = new PackComparisonPrice(s.ShopId, s.Shop.Name, amount,
                amount.HasValue && quantity is not null ? PackComparisonService.UnitPrice(amount.Value, quantity) : null,
                status, price?.CheckedDate, s.ShopProduct.ProductUrl, PriceObservationContextService.From(price, s.Shop.Code));
            return new ShoppingListSubstitutionChoice(s.ShopId, s.Shop.Name,
                new(product.Id, product.Name, product.Brand, product.Variant, product.PackQuantity, product.PackSize, product.PackUnit, product.ImageUrl),
                s.Quantity, status, s.UpdatedDate, offer, status == "Fresh" ? InStoreShoppingService.SinglePriceMultibuy(price) : null);
        }).OrderBy(s => s.ShopName).ToArray());
    }

    internal static ShoppingListPlanningItem Apply(ShoppingListPlanningItem row, ShoppingListSubstitutionChoice[] choices)
    {
        var prices = row.Prices.Where(p => !choices.Any(s => s.ShopId == p.ShopId)).Concat(choices.Select(s =>
            new ShoppingListPrice(s.ShopId, s.ShopName, s.Status == "Fresh" ? s.Offer?.Price : null,
                s.Status == "Fresh" && s.Offer?.Price is not null, s.Status, s.Offer?.ProductUrl, s.Offer?.CheckedDate, null,
                Multibuy: s.Multibuy, QuantityPrice: s.Status == "Fresh" && s.Offer?.Price is { } amount
                    ? PromotionCalculationService.Calculate(amount, s.Quantity, s.Multibuy) : null,
                Observation: s.Offer?.Observation, Substitution: s))).OrderBy(p => p.ShopName).ToArray();
        return row with { Prices = prices, Substitutions = choices };
    }
    internal static InStoreItem Apply(InStoreItem row, ShoppingListSubstitutionChoice[] choices)
    {
        var prices = row.Prices.Where(p => !choices.Any(s => s.ShopId == p.ShopId)).Concat(choices.Select(s =>
            new InStorePrice(s.ShopId, s.ShopName, s.Status == "Fresh" ? s.Offer?.Price : null, s.Status, s.Offer?.CheckedDate,
                s.Multibuy, s.Status == "Fresh" && s.Offer?.Price is { } amount ? PromotionCalculationService.Calculate(amount, s.Quantity, s.Multibuy) : null,
                PreviousPrice: s.Status != "Fresh" ? s.Offer?.Price : null, Observation: s.Offer?.Observation, Substitution: s)))
            .OrderBy(p => p.ShopName).ToArray();
        return row with { Prices = prices, Substitutions = choices };
    }
}
