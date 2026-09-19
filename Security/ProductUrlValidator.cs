using System.Net;
using System.Net.Sockets;
using myshoppinglist_api.Providers;

namespace myshoppinglist_api.Security;

public enum ProductUrlValidationError
{
    None,
    InvalidUrl,
    HttpsRequired,
    CredentialsNotAllowed,
    PortNotAllowed,
    UnsafeHost,
    UnsupportedRetailer
}

public sealed record ProductUrlValidationResult(
    ProductUrlValidationError Error, Uri? ProductUrl = null, RetailerDefinition? Retailer = null)
{
    public bool IsValid => Error == ProductUrlValidationError.None;
}

public sealed class ProductUrlValidator(RetailerCatalog catalog)
{
    public const int MaximumUrlLength = 2048;

    // Conservative special-purpose exclusions. This is a destination policy, not a reachability test.
    // https://www.iana.org/assignments/iana-ipv4-special-registry/
    // https://www.iana.org/assignments/iana-ipv6-special-registry/
    private static readonly IPNetwork[] BlockedNetworks = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16",
        "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20"
    }.Select(IPNetwork.Parse).ToArray();
    private static readonly IPNetwork GlobalIpv6 = IPNetwork.Parse("2000::/3");

    public ProductUrlValidationResult Validate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaximumUrlLength
            || input.Any(char.IsControl) || input.Contains('\\'))
            return new(ProductUrlValidationError.InvalidUrl);

        var text = input.Trim();
        if (text.Any(char.IsWhiteSpace) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return new(ProductUrlValidationError.InvalidUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            return new(ProductUrlValidationError.HttpsRequired);

        // UserInfo alone does not identify an empty credentials section (https://@host/).
        var authorityStart = text.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) return new(ProductUrlValidationError.InvalidUrl);
        var authority = text[(authorityStart + 3)..].Split('/', '?', '#')[0];
        if (authority.Contains('@') || !string.IsNullOrEmpty(uri.UserInfo))
            return new(ProductUrlValidationError.CredentialsNotAllowed);
        if (uri.Port != 443) return new(ProductUrlValidationError.PortNotAllowed);
        if (uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback
            || authority.Any(c => c > 127) || authority.Contains('%') || uri.IdnHost.EndsWith('.'))
            return new(ProductUrlValidationError.UnsafeHost);

        var retailer = catalog.FindByHost(uri.IdnHost);
        if (retailer is null) return new(ProductUrlValidationError.UnsupportedRetailer);

        // Fragments are browser-only. Preserve path and query semantics; do not guess tracking parameters.
        var normalised = new UriBuilder(uri) { Fragment = string.Empty }.Uri;
        return new(ProductUrlValidationError.None, normalised, retailer);
    }

    // The future HTTP transport must check every resolved address and connect to the checked address,
    // then repeat URL/destination validation for redirects. Validate() alone is not SSRF protection.
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.ScopeId != 0 || !GlobalIpv6.Contains(address))) return false;
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;
        return !BlockedNetworks.Any(network => network.Contains(address));
    }
}
