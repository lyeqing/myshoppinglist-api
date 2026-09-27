using myshoppinglist_api.Providers.Coles;
using myshoppinglist_api.Providers.Models;
using myshoppinglist_api.Services;

namespace myshoppinglist_api.Tests;

public class ColesProductProviderTests
{
    private static readonly ProductIdentity Palmolive = new() { Name = "Palmolive Body Wash Shower Gel Naturals Milk Honey 1L", Brand = "Palmolive" };
    [Fact]
    public async Task Empty_full_search_tries_one_shorter_query_and_returns_its_candidates()
    {
        var queue = new Queue { EmptyFirst = true };
        var result = await new ColesProductProvider(queue).SearchAsync(Palmolive, null, default);
        Assert.Single(Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success>(result).Value);
        Assert.Equal(new[] { "palmolive body wash shower gel naturals milk honey 1l", "palmolive naturals milk honey 1l" }, queue.Queries);
    }
    [Fact]
    public async Task Successful_full_search_does_not_enqueue_a_fallback()
    {
        var queue = new Queue();
        await new ColesProductProvider(queue).SearchAsync(Palmolive, null, default);
        Assert.Single(queue.Queries);
    }
    [Fact]
    public async Task Pending_or_failed_search_never_becomes_an_empty_search()
    {
        var pending = new Queue { SearchPending = true };
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => new ColesProductProvider(pending).SearchAsync(Palmolive, null, default));
        Assert.Single(pending.Queries);
        var failure = new Queue { SearchFailure = true };
        Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure>(await new ColesProductProvider(failure).SearchAsync(Palmolive, null, default));
        Assert.Single(failure.Queries);
        var empty = new Queue { AlwaysEmpty = true };
        Assert.Empty(Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success>(await new ColesProductProvider(empty).SearchAsync(Palmolive, null, default)).Value);
        Assert.Equal(2, empty.Queries.Count);
    }
    [Fact]
    public async Task Product_and_search_use_extension_queue_only()
    {
        var queue = new Queue(); var provider = new ColesProductProvider(queue);
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Success>(await provider.GetProductFromUrlAsync(ColesProductParserTests.Url, default));
        Assert.Single(Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success>(
            await provider.SearchAsync(new() { Name = "Coca-Cola" }, null, default)).Value);
        Assert.Equal(1, queue.Products); Assert.Equal(1, queue.Searches);
    }
    [Theory]
    [InlineData("https://www.coles.com.au/browse/drinks")]
    [InlineData("https://www.woolworths.com.au/product/example-1849307")]
    public async Task Invalid_urls_never_enqueue(string url)
    {
        var queue = new Queue();
        Assert.IsType<ProviderResult<ExtractedShopProduct>.Failure>(await new ColesProductProvider(queue).GetProductFromUrlAsync(new(url), default));
        Assert.Equal(0, queue.Products);
    }
    [Fact]
    public async Task Unsupported_location_and_invalid_search_never_enqueue()
    {
        var queue = new Queue(); var provider = new ColesProductProvider(queue);
        Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure>(await provider.SearchAsync(new() { Name = "Coke" }, new() { ShopCode = "coles" }, default));
        Assert.IsType<ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure>(await provider.SearchAsync(new() { Name = " " }, null, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(new() { Name = "Coke" }, null, new(true)));
        Assert.Equal(0, queue.Searches);
    }
    [Fact]
    public async Task Waiting_for_extension_propagates_without_becoming_not_found()
    {
        await Assert.ThrowsAsync<ColesWorkPendingException>(() => new ColesProductProvider(new Queue { Pending = true })
            .GetProductFromUrlAsync(ColesProductParserTests.Url, default));
    }
    [Fact]
    public async Task Offer_refresh_rejects_changed_gtin()
    {
        var candidate = new ShopProductSearchResult(new() { ShopCode = "coles", ProductUrl = ColesProductParserTests.Url,
            Identity = new() { Name = "Product", GTIN = "different" }, SourceType = myshoppinglist_api.Models.SourceType.StructuredData, CheckedDate = ColesProductParserTests.Checked });
        var failure = Assert.IsType<ProviderResult<ShopProductOffer>.Failure>(await new ColesProductProvider(new Queue()).GetOfferAsync(candidate, null, default));
        Assert.Equal("offer_identity_changed", failure.Error.Code);
    }
    private sealed class Queue : IColesExtensionQueue
    {
        public int Products; public int Searches; public bool Pending;
        public bool EmptyFirst, AlwaysEmpty, SearchPending, SearchFailure;
        public List<string> Queries { get; } = [];
        public async Task<ProviderResult<ExtractedShopProduct>> ProductAsync(Uri url, CancellationToken token)
        {
            Products++; if (Pending) throw new ColesWorkPendingException();
            return await new ColesProductParser().ParseAsync(new(url, ColesProductParserTests.Fixture, ColesProductParserTests.Checked), token);
        }
        public async Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(string query, CancellationToken token)
        {
            Searches++;
            Queries.Add(query);
            if (SearchPending) throw new ColesWorkPendingException();
            if (SearchFailure) return new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Failure(new(ProviderFailureKind.AccessRestricted, "restricted", "Restricted"));
            if (AlwaysEmpty || EmptyFirst && Searches == 1) return new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success([]);
            var result = await new ColesProductParser().ParseAsync(new(ColesProductParserTests.Url, ColesProductParserTests.Fixture, ColesProductParserTests.Checked), token);
            return new ProviderResult<IReadOnlyList<ShopProductSearchResult>>.Success([new(((ProviderResult<ExtractedShopProduct>.Success)result).Value)]);
        }
    }
}
