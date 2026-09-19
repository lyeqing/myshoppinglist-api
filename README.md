# MyShoppingList API

Standalone ASP.NET Core 10 / EF Core / PostgreSQL backend following HandyTool conventions. Stages 1 and 2 provide the database and provider contracts. Stage 3A adds safe public-page fetching and Coles source extraction. Authentication endpoints, catalogue persistence services, job processing, other retailers, and the frontend will follow separately.

## Local setup

Requires .NET SDK 10, EF CLI 10.0.11, and PostgreSQL. The database name is exactly `MyShoppingList` (case-sensitive). Supply `ConnectionStrings__MyShoppingList` through the environment or an ignored `appsettings.Local.json`:

```json
{
  "ConnectionStrings": {
    "MyShoppingList": "Host=localhost;Port=5432;Database=MyShoppingList;Username=YOUR_USER;Password=YOUR_PASSWORD"
  }
}
```

Do not commit credentials. The local configuration is loaded before environment overrides. The runtime does not create or migrate databases automatically.

```powershell
dotnet restore myshoppinglist-api.slnx
dotnet ef database update --project myshoppinglist-api.csproj
dotnet run --project myshoppinglist-api.csproj --launch-profile http
```

EF creates the named database if absent when the configured PostgreSQL role has permission. The API listens on `http://localhost:5392`; development Swagger is at `/swagger`. This stage exposes only a liveness response, not shopping-list or import endpoints.

## Database design

- Shared `Products`, `Shops`, `ShopLocations`, and `ShopProducts` contain catalogue data, independent of account ownership. All five initial shops are seeded; a shop record does not mean a provider is implemented.
- `ShoppingListProducts` references a canonical product, with one row per list/product. The later service must explicitly choose quantity behaviour for repeat submissions.
- Current prices are unique by retailer mapping, optional location, scope, and currency. Separate filtered indexes enforce uniqueness when location is NULL. History preserves currency, promotion details, source, and check time. Amounts use `numeric(18,4)`; timestamps use PostgreSQL `timestamp with time zone` and must be supplied in UTC.
- GTIN is indexed but not unique. Future deterministic canonical resolution must handle concurrent imports and contradictory metadata rather than merging by title alone.
- Registered users require credentials. Trial accounts have no credentials and require expiry. Sessions store token hashes only; token issuance, authentication, and expiry enforcement are not implemented in this stage.
- `ProductImportJobs` persists quantity, ownership, URL, progress, retry scheduling, and claim lease information. Composite foreign keys enforce matching list ownership and resulting list-item identity.
- `ProductImportRetailerResults` preserves per-shop progress and failures even without a mapping or price. Each job has at most one result per shop.

## Background processing contract for the next stage

The database is the durable queue. A future `BackgroundService` must claim a queued job atomically, assigning a fresh `ClaimToken` and lease with its status transition. `Status` and `ClaimToken` are EF concurrency tokens; stale writes fail instead of silently overwriting a newer claim. Lease renewal and recovery must also use guarded writes. Child-result and catalogue writes must be fenced inside a transaction that verifies the current claim; a parent concurrency token alone cannot protect child-table writes.

The worker must recheck account/list access and expiry, validate outbound URLs at connection and redirect time, use separate scopes/contexts for concurrent retailer operations, and commit source results before comparisons finish. Restart recovery and retry processing are future implementation, not functionality supplied merely by these tables. Polling will expose partial results at approximately two-second intervals.

Later services must validate that a price's location belongs to its retailer, normalise email and GTIN, implement retry idempotency, and determine history insertion frequency. Business operations are not exposed yet.

## Retailer-provider contracts and URL validation

`IShopProductProvider` exposes cancellable extraction, search, and offer operations. Its models preserve original retailer text and optional metadata. Search candidates are not automatically exact matches; the later deterministic matching service makes that decision. An offer records its currency, location, price scope, provenance, and check timestamp separately from product identity.

Operations return a typed success or failure. Temporary timeout, network, rate-limit, and server failures are retryable; access restrictions, unsupported retailers, missing products, and parsing failures are not automatically retried. Failure status distinguishes unavailable data from a product not found. A successful search with no candidates means the search completed normally; an unsuccessful search must return a failure. Source extraction can succeed while its optional offer carries a price-retrieval failure. A null offer means no offer check was performed. Expected provider failures use these results; shutdown/request cancellation must propagate through the supplied token rather than being converted into a retailer failure. Providers must validate extracted values (including positive pack sizes, nonnegative prices, and matching location retailer) before returning success.

The registry knows the five seeded retailer codes and explicitly approved root/www hosts. Coles is registered for source extraction and anonymous offer retrieval; its cross-retailer search returns `NotSupported`. Woolworths, ALDI, IGA, and Foodland remain known but unimplemented. Adding a retailer means adding its definition, database shop record, and provider registration; application dispatch does not need retailer-specific branches. Duplicate codes, conflicting hosts, and providers without a retailer definition fail configuration. The registry is scoped so providers can depend on scoped services. The separate immutable `RetailerCatalog` supplies host policy without resolving provider instances, avoiding circular dependencies with the HTTP client.

`ProductUrlValidator.Validate` accepts HTTPS on port 443, requires an exact approved hostname, and rejects embedded credentials, literal IP addresses, malformed inputs, internal/unknown hosts, and ambiguous authorities. Fragments are removed while path and query semantics are preserved. It now validates host policy independently of provider availability; callers use the registry's `IsImplemented` to decide whether an operation is supported. An allowed hostname does not imply extraction/search capabilities.

Validation alone does not fetch a page or resolve DNS. `SafeRetailerConnection` checks **all** DNS results using the IPv4/IPv6 destination policy, rejects empty or unsafe results, then connects using a checked numeric endpoint. Normal TLS certificate and hostname verification remains enabled. `RetailerHttpClient` uses `IHttpClientFactory`, forbids automatic redirects, and revalidates every redirect within the same retailer. Proxy routing and cookies are disabled to keep destination checks and anonymous pricing explicit. Connections are pooled briefly; a new connection repeats DNS validation. The client limits the full operation to 20 seconds, the decompressed body to 2 MiB, redirects to three, and response headers to 32 KiB. The `RetailerHttp` settings configure time, body size, and redirects with startup validation. No caller authentication headers or cookies are forwarded.

References: [Microsoft URI hostname handling](https://learn.microsoft.com/en-us/dotnet/api/system.uri.idnhost?view=net-10.0), [IANA IPv4 special-purpose registry](https://www.iana.org/assignments/iana-ipv4-special-registry/), and [IANA IPv6 special-purpose registry](https://www.iana.org/assignments/iana-ipv6-special-registry/).

## Coles extraction (Stage 3A)

The public [Coles product page for code 1849307](https://www.coles.com.au/product/coca-cola-classic-soft-drink-multipack-cans-375ml-10-pack-1849307) was inspected on 2026-09-19. It returned both schema.org Product JSON-LD and `__NEXT_DATA__.props.pageProps.product`. The parser selects by product ID and checks URL/sku consistency; it never treats a recommended product or another pack size as the requested item. Embedded fields supplement JSON-LD or provide a fallback. A reduced fixture documents the exact observed structures without retaining account/session state.

HTML is parsed with AngleSharp 1.8.2, without script execution or external resource loading. GTIN checksums are validated. Missing optional metadata remains null; contradictory GTIN or pack information is left unresolved. The observed title said 10 Pack while its description also mentioned 24 cans, so this fixture intentionally has unknown pack quantity. Conflicting source prices produce an explicit offer failure while preserving the identified product. A multibuy reward is never substituted for the single-pack price; its wording is retained separately.

Prices from anonymous pages have `PriceScope.Unknown` and no claimed user store. They are observed page prices, not national prices or a verified local-store quote. Requested location-specific offers return `NotSupported` for now. A later savings service must determine eligibility for these unverified-location prices. Missing prices return an offer-level parsing failure, not a fabricated zero or a claim that the product is not sold. HTTP 403/access-restriction pages return nonretryable access failures; no bypass or Playwright fallback is implemented. Search remains explicitly unsupported. This stage writes no products, prices, or jobs to the database and exposes no new public endpoint.

The implementation uses [SocketsHttpHandler.ConnectCallback](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0) for checked-address connections and the pinned [AngleSharp package](https://www.nuget.org/packages/AngleSharp/1.8.2) for parsing.

## Verification

```powershell
dotnet test myshoppinglist-api.slnx
dotnet ef migrations has-pending-model-changes --project myshoppinglist-api.csproj
```

Model tests run without a database. Set `MYSHOPPINGLIST_TEST_CONNECTION` to a migrated PostgreSQL database to also run relational constraint and optimistic-concurrency tests. Each database test uses a transaction and rolls back its records; PostgreSQL identity sequences may still advance. Tests never drop or recreate the database. Without the environment variable, relational tests are explicitly reported as skipped.

Parser/HTTP tests are deterministic and do not contact retailers. One separate opt-in test fetches the public Coles page through the safe transport:

```powershell
$env:MYSHOPPINGLIST_LIVE_COLES = '1'
dotnet test myshoppinglist-api.slnx --filter 'FullyQualifiedName~Live_public_page'
Remove-Item Env:MYSHOPPINGLIST_LIVE_COLES
```

This live check can fail when retailer access or page structures change. It must not be used as an always-on CI dependency.
