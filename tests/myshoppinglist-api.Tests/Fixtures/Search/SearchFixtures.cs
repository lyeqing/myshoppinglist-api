namespace myshoppinglist_api.Tests;

// Reduced from rendered public search DOM observed 2026-09-20. No prices or identity
// claims are taken from these links; the provider must fetch the product pages.
internal static class SearchFixtures
{
    internal const string Coles = """
        <title>Search coca-cola | Coles</title>
        <section class="coles-targeting-search-content-container">
          <a href="/product/coca-cola-classic-soft-drink-bottle-1.25l-123011">Coca-Cola Classic Soft Drink Bottle | 1.25L</a>
          <a href="/product/coca-cola-classic-soft-drink-bottle-1.25l-123011">Coca-Cola Classic Soft Drink Bottle | 1.25L</a>
          <a href="/product/coca-cola-classic-soft-drink-multipack-cans-375ml-10-pack-1849307">Coca-Cola Classic Soft Drink Multipack Cans 375ml | 10 Pack</a>
        </section>
        """;
    // The browser flattens only links from wc-product-tile shadow roots in this result container.
    internal const string Woolworths = """
        <title>coca-cola - Woolworths Online</title>
        <section data-testid="search-results-product-scrollable-content">
          <a href="/shop/productdetails/84552/coca-cola-classic-soft-drink-cans">Coca-Cola Classic Soft Drink Cans 375mL x 30 pack</a>
          <a href="/shop/productdetails/84552/coca-cola-classic-soft-drink-cans#product-reviews">21</a>
          <a href="/shop/productdetails/32731/coca-cola-classic-soft-drink-bottle">Coca-Cola Classic Soft Drink Bottle 1.25L</a>
        </section>
        """;
}
