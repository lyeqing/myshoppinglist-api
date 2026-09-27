namespace myshoppinglist_api.Contracts;

public sealed record InStoreRetailer(long Id, string Name);
public sealed record InStoreList(long Id, string Name, int RemainingCount, InStoreRetailer[] Retailers);
public sealed record InStorePrice(long ShopId, string ShopName, decimal? Price, string Status, DateTime? CheckedDate);
public sealed record InStoreItem(ShoppingListItemResponse Item, InStorePrice[] Prices);
public sealed record InStoreBasket(long ShopId, string ShopName, decimal Subtotal, int PricedCount,
    decimal ComparableSubtotal, decimal SavingsBySplitting);
public sealed record InStoreDetail(long Id, string Name, InStoreRetailer[] Retailers, InStoreItem[] Items,
    int RemainingCount, InStoreBasket[] Baskets, decimal SplitSubtotal, int SplitPricedCount,
    int ComparableCount, decimal ComparableSplitSubtotal);
