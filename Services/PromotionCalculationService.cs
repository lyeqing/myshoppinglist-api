using myshoppinglist_api.Contracts;

namespace myshoppinglist_api.Services;

public static class PromotionCalculationService
{
    // The caller supplies only current, verified offers for this exact product.
    // Mixed-product baskets and membership eligibility are deliberately not inferred.
    public static ShoppingListQuantityPrice Calculate(decimal singlePrice, int quantity, ShoppingListMultibuy? offer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(singlePrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        var ordinaryTotal = singlePrice * quantity;
        if (offer is null || offer.Quantity < 2 || offer.Total <= 0 || offer.Total >= singlePrice * offer.Quantity)
            return new(quantity, ordinaryTotal, ordinaryTotal, 0, 0, quantity);
        var bundles = quantity / offer.Quantity;
        var remaining = quantity % offer.Quantity;
        var total = bundles * offer.Total + remaining * singlePrice;
        return new(quantity, total, ordinaryTotal, ordinaryTotal - total, bundles, remaining);
    }
}
