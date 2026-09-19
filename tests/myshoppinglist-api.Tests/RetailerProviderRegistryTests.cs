using myshoppinglist_api.Models;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Providers.Models;

namespace myshoppinglist_api.Tests;

public class RetailerProviderRegistryTests
{
    [Theory]
    [InlineData("coles", "www.coles.com.au")]
    [InlineData("woolworths", "www.woolworths.com.au")]
    [InlineData("aldi", "www.aldi.com.au")]
    [InlineData("iga", "www.iga.com.au")]
    [InlineData("foodland", "www.foodlandsa.com.au")]
    public void Known_retailers_are_not_implemented_until_a_provider_is_registered(string code, string host)
    {
        var registry = new RetailerProviderRegistry([]);
        var result = registry.FindByHost(host);
        Assert.NotNull(result);
        Assert.Equal(code, result.Retailer.Code);
        Assert.False(result.IsImplemented);
        Assert.Null(result.Provider);
        Assert.Same(result, registry.FindByCode(code));
    }

    [Fact]
    public void Registered_provider_resolves_by_code_and_exact_host_case_insensitively()
    {
        var provider = new TestShopProductProvider("coles");
        var registry = new RetailerProviderRegistry([provider]);
        Assert.Same(provider, registry.FindByCode("COLES")!.Provider);
        Assert.Same(provider, registry.FindByHost("WWW.COLES.COM.AU")!.Provider);
        Assert.Same(provider, registry.FindByHost("coles.com.au")!.Provider);
        Assert.True(registry.FindByCode("coles")!.IsImplemented);
        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData("coles.com.au.evil.example")]
    [InlineData("evilcoles.com.au")]
    [InlineData("unapproved.coles.com.au")]
    [InlineData("www.coles.com.au.")]
    [InlineData("localhost")]
    public void Unknown_hosts_do_not_resolve_by_suffix(string host) =>
        Assert.Null(new RetailerProviderRegistry([]).FindByHost(host));

    [Fact]
    public void Another_retailer_can_be_registered_without_changing_dispatch_logic()
    {
        var provider = new TestShopProductProvider("new-retailer");
        var registry = new RetailerProviderRegistry(
            [new("new-retailer", "New retailer", ["shop.example.com"])], [provider]);
        Assert.Same(provider, registry.FindByHost("shop.example.com")!.Provider);
        Assert.Null(registry.FindByHost("example.com"));
    }

    [Fact]
    public void Duplicate_provider_codes_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new RetailerProviderRegistry(
            [new TestShopProductProvider("coles"), new TestShopProductProvider("COLES")]));

    [Fact]
    public void Unrecognised_provider_code_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new RetailerProviderRegistry([new TestShopProductProvider("unknown")]));

    [Fact]
    public void Host_cannot_belong_to_two_retailers() =>
        Assert.Throws<ArgumentException>(() => new RetailerProviderRegistry(
            [new("one", "One", ["shop.example.com"]), new("two", "Two", ["SHOP.EXAMPLE.COM"])], []));

    [Fact]
    public void Retailer_codes_are_unique_case_insensitively() =>
        Assert.Throws<ArgumentException>(() => new RetailerProviderRegistry(
            [new("one", "One", ["one.example.com"]), new("ONE", "Other", ["two.example.com"])], []));

    [Theory]
    [InlineData("*.coles.com.au")]
    [InlineData("https://coles.com.au")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("2130706433")]
    [InlineData("::1")]
    [InlineData("shop.internal")]
    [InlineData("shop.localhost")]
    [InlineData("shop.local")]
    [InlineData("shop.test")]
    [InlineData("shop.invalid")]
    [InlineData("coles.com.au.")]
    public void Invalid_host_configuration_is_rejected(string host) =>
        Assert.Throws<ArgumentException>(() => new RetailerProviderRegistry([new("shop", "Shop", [host])], []));

    [Fact]
    public void Caller_cannot_mutate_registered_host_allow_list()
    {
        var hosts = new List<string> { "shop.example.com" };
        var registry = new RetailerProviderRegistry([new("shop", "Shop", hosts)], []);
        hosts.Add("evil.example.com");
        Assert.Null(registry.FindByHost("evil.example.com"));
        Assert.Single(registry.FindByCode("shop")!.Retailer.AllowedHosts);
    }

    [Theory]
    [InlineData(ProviderFailureKind.Timeout, RetailerLookupStatus.Unavailable, true)]
    [InlineData(ProviderFailureKind.NetworkError, RetailerLookupStatus.Unavailable, true)]
    [InlineData(ProviderFailureKind.RateLimited, RetailerLookupStatus.Unavailable, true)]
    [InlineData(ProviderFailureKind.RemoteServerError, RetailerLookupStatus.Unavailable, true)]
    [InlineData(ProviderFailureKind.AccessRestricted, RetailerLookupStatus.Unavailable, false)]
    [InlineData(ProviderFailureKind.NotFound, RetailerLookupStatus.NotFound, false)]
    [InlineData(ProviderFailureKind.NotSupported, RetailerLookupStatus.NotSupported, false)]
    [InlineData(ProviderFailureKind.InvalidUrl, RetailerLookupStatus.CheckFailed, false)]
    [InlineData(ProviderFailureKind.InvalidProduct, RetailerLookupStatus.CheckFailed, false)]
    [InlineData(ProviderFailureKind.ParseError, RetailerLookupStatus.CheckFailed, false)]
    [InlineData(ProviderFailureKind.UnexpectedError, RetailerLookupStatus.CheckFailed, false)]
    public void Provider_failures_preserve_meaning_and_bound_retry_eligibility(
        ProviderFailureKind kind, RetailerLookupStatus status, bool retryable)
    {
        var failure = new ProviderFailure(kind, "test_failure", "Test failure");
        Assert.Equal(status, failure.Status);
        Assert.Equal(retryable, failure.IsRetryable);
    }
}

// Only registry/validator tests use this double. It must never perform extraction or network calls.
internal sealed class TestShopProductProvider(string shopCode) : IShopProductProvider
{
    public string ShopCode => shopCode;
    public int CallCount { get; private set; }

    public Task<ProviderResult<ExtractedShopProduct>> GetProductFromUrlAsync(Uri productUrl, CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("Registry resolution must not call a provider.");
    }

    public Task<ProviderResult<IReadOnlyList<ShopProductSearchResult>>> SearchAsync(
        ProductIdentity product, ShopLocationContext? location, CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("Registry resolution must not call a provider.");
    }

    public Task<ProviderResult<ShopProductOffer>> GetOfferAsync(
        ShopProductSearchResult product, ShopLocationContext? location, CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("Registry resolution must not call a provider.");
    }
}
