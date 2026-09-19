using System.Collections.Concurrent;

namespace myshoppinglist_api.Providers;

public sealed record RetailerDefinition(string Code, string Name, IReadOnlyList<string> AllowedHosts);

public sealed record RetailerRegistration(RetailerDefinition Retailer, IShopProductProvider? Provider)
{
    public bool IsImplemented => Provider is not null;
}

// Host policy has no provider dependencies, so the HTTP client cannot create a DI cycle.
public sealed class RetailerCatalog
{
    public static IReadOnlyList<RetailerDefinition> DefaultRetailers { get; } = Array.AsReadOnly(new[]
    {
        Define("coles", "Coles", "coles.com.au", "www.coles.com.au"),
        Define("woolworths", "Woolworths", "woolworths.com.au", "www.woolworths.com.au"),
        Define("aldi", "ALDI", "aldi.com.au", "www.aldi.com.au"),
        Define("iga", "IGA", "iga.com.au", "www.iga.com.au"),
        Define("foodland", "Foodland", "foodlandsa.com.au", "www.foodlandsa.com.au")
    });

    private readonly Dictionary<string, RetailerDefinition> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RetailerDefinition> _byHost = new(StringComparer.OrdinalIgnoreCase);
    public RetailerCatalog() : this(DefaultRetailers) { }
    public RetailerCatalog(IEnumerable<RetailerDefinition> retailers)
    {
        ArgumentNullException.ThrowIfNull(retailers);
        foreach (var retailer in retailers)
        {
            if (string.IsNullOrWhiteSpace(retailer.Code) || retailer.Code != retailer.Code.Trim()
                || string.IsNullOrWhiteSpace(retailer.Name) || retailer.AllowedHosts.Count == 0)
                throw new ArgumentException("Retailers require a stable code, name, and explicit host allow-list.", nameof(retailers));

            // Copy the host list so later changes to caller-owned collections cannot widen the allow-list.
            var definition = retailer with { AllowedHosts = Array.AsReadOnly(retailer.AllowedHosts.ToArray()) };
            if (!_byCode.TryAdd(retailer.Code, definition))
                throw new ArgumentException("Retailer codes must be unique.", nameof(retailers));
            foreach (var host in definition.AllowedHosts)
            {
                if (!IsValidConfiguredHost(host) || !_byHost.TryAdd(host, definition))
                    throw new ArgumentException("Allowed hosts must be explicit, unique, public DNS names.", nameof(retailers));
            }
        }
    }

    public RetailerDefinition? FindByCode(string code) => _byCode.GetValueOrDefault(code);

    // Exact host lookup only: a retailer suffix must never authorise arbitrary subdomains.
    public RetailerDefinition? FindByHost(string host) => _byHost.GetValueOrDefault(host);

    private static RetailerDefinition Define(string code, string name, params string[] hosts) =>
        new(code, name, Array.AsReadOnly(hosts));

    private static bool IsValidConfiguredHost(string host) =>
        !string.IsNullOrWhiteSpace(host) && host.All(c => c <= 127) && host.Contains('.')
        && !host.EndsWith('.') && Uri.CheckHostName(host) == UriHostNameType.Dns
        && !new[] { ".localhost", ".local", ".internal", ".test", ".invalid" }
            .Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}

public sealed class RetailerProviderRegistry
{
    private readonly RetailerCatalog _catalog;
    private readonly ConcurrentDictionary<string, RetailerRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<RetailerDefinition> DefaultRetailers => RetailerCatalog.DefaultRetailers;
    public RetailerProviderRegistry(IEnumerable<IShopProductProvider> providers) : this(new RetailerCatalog(), providers) { }
    public RetailerProviderRegistry(IEnumerable<RetailerDefinition> retailers, IEnumerable<IShopProductProvider> providers)
        : this(new RetailerCatalog(retailers), providers) { }

    public RetailerProviderRegistry(RetailerCatalog catalog, IEnumerable<IShopProductProvider> providers)
    {
        _catalog = catalog;
        foreach (var provider in providers)
        {
            var definition = string.IsNullOrWhiteSpace(provider.ShopCode) ? null : catalog.FindByCode(provider.ShopCode);
            if (definition is null || !_registrations.TryAdd(provider.ShopCode, new(definition, provider)))
                throw new ArgumentException("Providers require unique, known retailer codes.", nameof(providers));
        }
    }

    public RetailerRegistration? FindByCode(string code)
    {
        var definition = _catalog.FindByCode(code);
        if (definition is null) return null;
        return _registrations.GetOrAdd(code, _ => new(definition, null));
    }

    public RetailerRegistration? FindByHost(string host) =>
        _catalog.FindByHost(host) is { } definition ? FindByCode(definition.Code) : null;
}
