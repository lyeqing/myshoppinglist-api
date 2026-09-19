namespace myshoppinglist_api.Providers;

public sealed record RetailerDefinition(string Code, string Name, IReadOnlyList<string> AllowedHosts);

public sealed record RetailerRegistration(RetailerDefinition Retailer, IShopProductProvider? Provider)
{
    public bool IsImplemented => Provider is not null;
}

public sealed class RetailerProviderRegistry
{
    public static IReadOnlyList<RetailerDefinition> DefaultRetailers { get; } = Array.AsReadOnly(new[]
    {
        Define("coles", "Coles", "coles.com.au", "www.coles.com.au"),
        Define("woolworths", "Woolworths", "woolworths.com.au", "www.woolworths.com.au"),
        Define("aldi", "ALDI", "aldi.com.au", "www.aldi.com.au"),
        Define("iga", "IGA", "iga.com.au", "www.iga.com.au"),
        Define("foodland", "Foodland", "foodlandsa.com.au", "www.foodlandsa.com.au")
    });

    private readonly Dictionary<string, RetailerRegistration> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RetailerRegistration> _byHost = new(StringComparer.OrdinalIgnoreCase);

    public RetailerProviderRegistry(IEnumerable<IShopProductProvider> providers) : this(DefaultRetailers, providers) { }

    public RetailerProviderRegistry(IEnumerable<RetailerDefinition> retailers, IEnumerable<IShopProductProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(retailers);
        ArgumentNullException.ThrowIfNull(providers);
        var implementations = new Dictionary<string, IShopProductProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.ShopCode) || !implementations.TryAdd(provider.ShopCode, provider))
                throw new ArgumentException("Provider codes must be nonempty and unique.", nameof(providers));
        }

        foreach (var retailer in retailers)
        {
            if (string.IsNullOrWhiteSpace(retailer.Code) || retailer.Code != retailer.Code.Trim()
                || string.IsNullOrWhiteSpace(retailer.Name) || retailer.AllowedHosts.Count == 0)
                throw new ArgumentException("Retailers require a stable code, name, and explicit host allow-list.", nameof(retailers));

            // Copy the host list so later changes to caller-owned collections cannot widen the allow-list.
            var definition = retailer with { AllowedHosts = Array.AsReadOnly(retailer.AllowedHosts.ToArray()) };
            var entry = new RetailerRegistration(definition, implementations.GetValueOrDefault(retailer.Code));
            if (!_byCode.TryAdd(retailer.Code, entry))
                throw new ArgumentException("Retailer codes must be unique.", nameof(retailers));
            foreach (var host in definition.AllowedHosts)
            {
                if (!IsValidConfiguredHost(host) || !_byHost.TryAdd(host, entry))
                    throw new ArgumentException("Allowed hosts must be explicit, unique, public DNS names.", nameof(retailers));
            }
        }
        if (implementations.Keys.Any(code => !_byCode.ContainsKey(code)))
            throw new ArgumentException("Every provider must have a retailer definition.", nameof(providers));
    }

    public RetailerRegistration? FindByCode(string code) => _byCode.GetValueOrDefault(code);

    // Exact host lookup only: a retailer suffix must never authorise arbitrary subdomains.
    public RetailerRegistration? FindByHost(string host) => _byHost.GetValueOrDefault(host);

    private static RetailerDefinition Define(string code, string name, params string[] hosts) =>
        new(code, name, Array.AsReadOnly(hosts));

    private static bool IsValidConfiguredHost(string host) =>
        !string.IsNullOrWhiteSpace(host) && host.All(c => c <= 127) && host.Contains('.')
        && !host.EndsWith('.') && Uri.CheckHostName(host) == UriHostNameType.Dns
        && !new[] { ".localhost", ".local", ".internal", ".test", ".invalid" }
            .Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
