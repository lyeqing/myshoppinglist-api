using System.Net;
using myshoppinglist_api.Providers;
using myshoppinglist_api.Security;

namespace myshoppinglist_api.Tests;

public class ProductUrlValidatorTests
{
    private static ProductUrlValidator Validator() => new(new RetailerProviderRegistry(
        [new TestShopProductProvider("coles"), new TestShopProductProvider("woolworths")]));

    [Theory]
    [InlineData("https://coles.com.au/product/123", "coles")]
    [InlineData("HTTPS://WWW.COLES.COM.AU/product/123", "coles")]
    [InlineData("https://www.woolworths.com.au/shop/productdetails/123", "woolworths")]
    [InlineData("https://www.coles.com.au:443/product/123", "coles")]
    public void Implemented_retailer_urls_are_accepted(string url, string code)
    {
        var result = Validator().Validate(url);
        Assert.True(result.IsValid);
        Assert.Equal(code, result.Registration!.Retailer.Code);
        Assert.NotNull(result.ProductUrl);
    }

    [Fact]
    public void Normalisation_removes_fragment_but_preserves_path_and_query_meaning()
    {
        var result = Validator().Validate("  https://WWW.COLES.COM.AU:443/product/AbC?store=12&name=a%2Fb#offers  ");
        Assert.True(result.IsValid);
        Assert.Equal("https://www.coles.com.au/product/AbC?store=12&name=a%2Fb", result.ProductUrl!.AbsoluteUri);
    }

    [Fact]
    public void Known_unimplemented_retailer_is_distinct_from_unknown_retailer()
    {
        var known = Validator().Validate("https://www.aldi.com.au/product/example");
        Assert.False(known.IsValid);
        Assert.Equal(ProductUrlValidationError.ProviderNotImplemented, known.Error);
        Assert.Equal("aldi", known.Registration!.Retailer.Code);
        Assert.Null(known.Registration.Provider);
        var unknown = Validator().Validate("https://shop.example.com/product/example");
        Assert.Equal(ProductUrlValidationError.UnsupportedRetailer, unknown.Error);
        Assert.Null(unknown.Registration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/product/123")]
    [InlineData("www.coles.com.au/product/123")]
    [InlineData("https://www.coles.com.au/product/a b")]
    [InlineData("https://www.coles.com.au/product/123\r\n")]
    [InlineData("https://www.coles.com.au\\@evil.example/product/123")]
    public void Malformed_input_is_rejected(string? input) =>
        Assert.Equal(ProductUrlValidationError.InvalidUrl, Validator().Validate(input).Error);

    [Fact]
    public void Oversized_input_is_rejected() =>
        Assert.Equal(ProductUrlValidationError.InvalidUrl,
            Validator().Validate("https://www.coles.com.au/" + new string('x', ProductUrlValidator.MaximumUrlLength)).Error);

    [Theory]
    [InlineData("http://www.coles.com.au/product/123")]
    [InlineData("ftp://www.coles.com.au/product/123")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public void Non_https_schemes_are_rejected(string input) =>
        Assert.Equal(ProductUrlValidationError.HttpsRequired, Validator().Validate(input).Error);

    [Theory]
    [InlineData("https://user:password@www.coles.com.au/product/123")]
    [InlineData("https://@www.coles.com.au/product/123")]
    [InlineData("https://coles.com.au@evil.example/product/123")]
    public void Credentials_in_authority_are_rejected(string input) =>
        Assert.Equal(ProductUrlValidationError.CredentialsNotAllowed, Validator().Validate(input).Error);

    [Theory]
    [InlineData("https://www.coles.com.au:80/product/123")]
    [InlineData("https://www.coles.com.au:8443/product/123")]
    public void Nonstandard_ports_are_rejected(string input) =>
        Assert.Equal(ProductUrlValidationError.PortNotAllowed, Validator().Validate(input).Error);

    [Theory]
    [InlineData("https://127.0.0.1/product")]
    [InlineData("https://127.1/product")]
    [InlineData("https://2130706433/product")]
    [InlineData("https://0x7f000001/product")]
    [InlineData("https://10.0.0.1/product")]
    [InlineData("https://192.168.1.1/product")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/product")]
    [InlineData("https://[::ffff:127.0.0.1]/product")]
    [InlineData("https://8.8.8.8/product")]
    [InlineData("https://localhost/product")]
    [InlineData("https://www.coles.com.au./product")]
    [InlineData("https://www.cоles.com.au/product")]
    public void Literal_addresses_loopback_and_ambiguous_hosts_are_rejected(string input)
    {
        Assert.False(Validator().Validate(input).IsValid);
    }

    [Theory]
    [InlineData("https://www.coles.com.au.evil.example/product")]
    [InlineData("https://evilcoles.com.au/product")]
    [InlineData("https://unapproved.coles.com.au/product")]
    [InlineData("https://metadata.google.internal/product")]
    [InlineData("https://shop.local/product")]
    public void Host_allow_list_rejects_deceptive_and_internal_names(string input) =>
        Assert.Equal(ProductUrlValidationError.UnsupportedRetailer, Validator().Validate(input).Error);

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("192.168.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("3fff::1")]
    [InlineData("2001:4860:4860::8888%3")]
    public void Non_public_resolved_addresses_are_rejected(string address) =>
        Assert.False(ProductUrlValidator.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_resolved_addresses_pass_destination_policy(string address) =>
        Assert.True(ProductUrlValidator.IsPublicAddress(IPAddress.Parse(address)));
}
