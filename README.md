# MyShoppingList API

Standalone ASP.NET Core 10 / EF Core / PostgreSQL backend following HandyTool conventions. Stages 1 and 2 provide the database and provider contracts. Stage 3A adds safe public-page fetching and Coles source extraction. Stage 3B adds transactional source-product persistence and deterministic matching. Stage 3C adds the durable source-import worker. Stage 4A adds trial accounts and secure sessions. Stage 4B adds authenticated import submission and status polling. Stage 5A adds paginated import discovery and the separate Next.js frontend. Registered-account login and other retailers will follow separately.

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

EF creates the named database if absent when the configured PostgreSQL role has permission. The API listens on `http://localhost:5392`; development Swagger is at `/swagger`. The API exposes liveness, trial/session endpoints, and product-import submission/status. General shopping-list management is not implemented yet.

## Database design

- Shared `Products`, `Shops`, `ShopLocations`, and `ShopProducts` contain catalogue data, independent of account ownership. All five initial shops are seeded; a shop record does not mean a provider is implemented.
- `ShoppingListProducts` references a canonical product, with one row per list/product. Repeat source imports return the existing item without changing its quantity, notes, purchased state, or hidden state.
- Current prices are unique by retailer mapping, optional location, scope, and currency. Separate filtered indexes enforce uniqueness when location is NULL. History preserves currency, promotion details, source, and check time. Amounts use `numeric(18,4)`; timestamps use PostgreSQL `timestamp with time zone` and must be supplied in UTC.
- GTIN is indexed but not unique. Deterministic canonical resolution serialises catalogue writes and rejects ambiguous or contradictory identity data.
- Registered users require credentials; registration/login are future work. Trial accounts have no passwords and require expiry. Session issuance, validation, and revocation are implemented for trials. Sessions store token hashes only.
- `ProductImportJobs` persists quantity, ownership, URL, progress, retry scheduling, and claim lease information. Composite foreign keys enforce matching list ownership and resulting list-item identity.
- `ProductImportRetailerResults` preserves per-shop progress and failures even without a mapping or price. Each job has at most one result per shop.

## Background processing contract

The database is the durable queue. `ProductImportWorker` claims a queued job atomically, assigning a fresh `ClaimToken` and lease with its status transition. `Status` and `ClaimToken` are EF concurrency tokens; stale writes fail instead of silently overwriting a newer claim. Lease renewal and recovery also use guarded writes. Child-result and catalogue writes are fenced inside a transaction that verifies the current claim; a parent concurrency token alone cannot protect child-table writes.

The worker rechecks account/list access and expiry before extraction and persistence, uses the safe outbound transport, and commits source results before finalising comparisons. Processing and lease renewal use separate scopes/contexts. Future comparison operations must each use their own scope and save results incrementally. The status endpoint and frontend polling expose partial results at approximately two-second intervals.

Source persistence now validates price-location ownership, normalises GTIN, preserves retry idempotency, and samples price history. Authentication will handle email normalisation. Business operations are not exposed yet.

## Retailer-provider contracts and URL validation

`IShopProductProvider` exposes cancellable extraction, search, and offer operations. Its models preserve original retailer text and optional metadata. Search candidates are not automatically exact matches; the later deterministic matching service makes that decision. An offer records its currency, location, price scope, provenance, and check timestamp separately from product identity.

Operations return a typed success or failure. Temporary timeout, network, rate-limit, and server failures are retryable; access restrictions, unsupported retailers, missing products, and parsing failures are not automatically retried. Failure status distinguishes unavailable data from a product not found. A successful search with no candidates means the search completed normally; an unsuccessful search must return a failure. Source extraction can succeed while its optional offer carries a price-retrieval failure. A null offer means no offer check was performed. Expected provider failures use these results; shutdown/request cancellation must propagate through the supplied token rather than being converted into a retailer failure. Providers must validate extracted values (including positive pack sizes, nonnegative prices, and matching location retailer) before returning success.

The registry knows the five seeded retailer codes and explicitly approved root/www hosts. Coles and Woolworths are registered for source extraction and anonymous offer retrieval. Stage 6A adds their search-provider implementations; background comparison integration is still pending. ALDI, IGA, and Foodland remain known but unimplemented. Adding a retailer means adding its definition, database shop record, and provider registration; application dispatch does not need retailer-specific branches. Duplicate codes, conflicting hosts, and providers without a retailer definition fail configuration. The registry is scoped so providers can depend on scoped services. The separate immutable `RetailerCatalog` supplies host policy without resolving provider instances, avoiding circular dependencies with the HTTP client.

`ProductUrlValidator.Validate` accepts HTTPS on port 443, requires an exact approved hostname, and rejects embedded credentials, literal IP addresses, malformed inputs, internal/unknown hosts, and ambiguous authorities. Fragments are removed while path and query semantics are preserved. It now validates host policy independently of provider availability; callers use the registry's `IsImplemented` to decide whether an operation is supported. An allowed hostname does not imply extraction/search capabilities.

Validation alone does not fetch a page or resolve DNS. `SafeRetailerConnection` checks **all** DNS results using the IPv4/IPv6 destination policy, rejects empty or unsafe results, then connects using a checked numeric endpoint. Normal TLS certificate and hostname verification remains enabled. `RetailerHttpClient` uses `IHttpClientFactory`, forbids automatic redirects, and revalidates every redirect within the same retailer. Proxy routing and cookies are disabled to keep destination checks and anonymous pricing explicit. Connections are pooled briefly; a new connection repeats DNS validation. The client limits the full operation to 20 seconds, the decompressed body to 2 MiB, redirects to three, and response headers to 32 KiB. The `RetailerHttp` settings configure time, body size, and redirects with startup validation. No caller authentication headers or cookies are forwarded.

References: [Microsoft URI hostname handling](https://learn.microsoft.com/en-us/dotnet/api/system.uri.idnhost?view=net-10.0), [IANA IPv4 special-purpose registry](https://www.iana.org/assignments/iana-ipv4-special-registry/), and [IANA IPv6 special-purpose registry](https://www.iana.org/assignments/iana-ipv6-special-registry/).

## Coles extraction (Stage 3A)

The public [Coles product page for code 1849307](https://www.coles.com.au/product/coca-cola-classic-soft-drink-multipack-cans-375ml-10-pack-1849307) was inspected on 2026-09-19. It returned both schema.org Product JSON-LD and `__NEXT_DATA__.props.pageProps.product`. The parser selects by product ID and checks URL/sku consistency; it never treats a recommended product or another pack size as the requested item. Embedded fields supplement JSON-LD or provide a fallback. A reduced fixture documents the exact observed structures without retaining account/session state.

HTML is parsed with AngleSharp 1.8.2, without script execution or external resource loading. GTIN checksums are validated. Missing optional metadata remains null; contradictory GTIN or pack information is left unresolved. The observed title said 10 Pack while its description also mentioned 24 cans, so this fixture intentionally has unknown pack quantity. Conflicting source prices produce an explicit offer failure while preserving the identified product. A multibuy reward is never substituted for the single-pack price; its wording is retained separately.

Prices from anonymous pages have `PriceScope.Unknown` and no claimed user store. They are observed page prices, not national prices or a verified local-store quote. Requested location-specific offers return `NotSupported` for now. A later savings service must determine eligibility for these unverified-location prices. Missing prices return an offer-level parsing failure, not a fabricated zero or a claim that the product is not sold. HTTP 403/access-restriction pages return nonretryable access failures; source extraction has no browser fallback. Stage 6A separately adds browser-rendered searches, with live-access limitations documented below. This stage writes no products, prices, or jobs to the database and exposes no new public endpoint.

The implementation uses [SocketsHttpHandler.ConnectCallback](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0) for checked-address connections and the pinned [AngleSharp package](https://www.nuget.org/packages/AngleSharp/1.8.2) for parsing.

## Source persistence (Stage 3B)

`SourceProductPersistenceService.SaveAsync` accepts an extracted source product plus a processing job ID, account ID, and current claim token. It requires a fresh or cleared scoped DbContext. It checks ownership, account/trial eligibility, list availability, and the unexpired claim before writing, then checks eligibility again before committing. Products, retailer mappings, current prices/history, list items, and the source retailer result are saved atomically. Invalid offers and identity conflicts roll back the operation. An explicit offer-retrieval failure still preserves the identified product and records the failure. The job advances to `CheckingRetailers`; this service does not claim or complete jobs.

Valid GTINs are checksum-checked and padded to 14 digits for comparison. Existing retailer mappings are reused only without identity contradictions. New canonical matches require an equal GTIN, brand plus manufacturer/model identifier, or complete matching brand/variant/pack metadata with supporting title similarity. Contradictory variants, identifiers, or pack data prevent merging. Title similarity alone is insufficient. Missing metadata stays unresolved and does not erase established values; original retailer text is retained on the mapping.

A transaction-scoped PostgreSQL advisory lock serialises the short catalogue write phase, including separate-context concurrent imports. Retailer HTTP work must happen before entering this phase. Row locks follow catalogue, account, list, then job order; future worker and cleanup writers must preserve that order. Lower-level persistence services require a transaction; use the source service as the orchestration entry point. An existing caller transaction is protected with a savepoint and remains the caller's responsibility to commit.

Prices are validated against their retailer and optional active location. Store-specific prices require a location; anonymous prices retain their supplied scope. Currency and scope remain separate current-price keys. Older observations cannot replace newer prices, and conflicting observations at the same timestamp are rejected. Timestamps are aligned to PostgreSQL microsecond precision so identical retries remain idempotent. History is inserted for an initial observation, a price/promotion change, or an unchanged observation after `Price:HistorySampleHours` (default 24); unchanged refreshes still update the current check time.

No database migration or new public endpoint is added in Stage 3B. The worker supplies an already-claimed job and invokes this service in its own scope.

## Durable source-import worker (Stage 3C)

The hosted worker runs one job at a time per application instance. It uses a PostgreSQL `UPDATE ... RETURNING` statement with a `FOR UPDATE SKIP LOCKED` candidate to claim one eligible queued job. Competing instances cannot claim the same row. Claiming, renewal, and recovery lock only job rows and never acquire catalogue/account/list locks afterwards. Source persistence and completion retain the catalogue → account → list → job lock order. No transaction spans a retailer request.

`ProductImport` settings are validated at startup:

| Setting | Default | Purpose |
| --- | --- | --- |
| `Enabled` | `true` | Enable the hosted worker; set `ProductImport__Enabled=false` to disable it. |
| `PollSeconds` | `1` | Idle queue polling interval. |
| `MaxAttempts` | `3` | Maximum claims, including recovered attempts. |
| `LeaseSeconds` | `600` | Time before an unrenewed claim expires. |
| `RenewalSeconds` | `30` | Renewal interval; must be less than half the lease duration. |
| `RetryDelaySeconds` | `30` | Initial retry delay, doubled on later attempts. |

Transient source-provider failures are requeued with a persisted retry date. Provider `RetryAfter` can lengthen the delay, capped at one hour. Invalid URLs, unimplemented source providers, removed products, and other nonretryable source failures finish without retry. Unexpected processing exceptions also have bounded retries. Each claim increments `AttemptCount`. Expired processing claims are recovered in batches of up to 100 on each loop; jobs at their attempt limit finish `Failed`, or `Partial` when a source list item already exists. Previously saved products and prices are retained.

Lease renewal runs in an independent scope while extraction is active. A rejected or failed renewal cancels that processing attempt. On shutdown, cancellation reaches the provider and the worker awaits its processing/renewal tasks. An interrupted SQL claim remains recoverable when its lease expires; it is not marked as a successful import. If a restart occurs after source persistence committed, processing resumes finalisation without fetching or adding that source item again.

This stage implements source extraction only. Other active retailers receive `NotSupported` with `comparison_not_implemented`; they are never reported as `NotFound`. Successful source imports normally finish `Partial` until comparison providers are implemented. Source offer failures preserve the identified product and their specific unavailable/failure result. A complete result can be produced when all expected checks have normal outcomes. Completion rechecks the lease, account, and list; an account/list that becomes unavailable before finalisation cancels the job.

The worker processes SQL jobs created by the Stage 4B submission endpoint. Fresh-price reuse for comparisons and Woolworths comparisons remain future stages. No new migration is required. Implementation references: [PostgreSQL queue locking](https://www.postgresql.org/docs/14/sql-select.html) and [scoped services in BackgroundService](https://learn.microsoft.com/en-us/dotnet/core/extensions/scoped-service).

## Trial accounts and sessions (Stage 4A)

| Endpoint | Behaviour |
| --- | --- |
| `POST /api/auth/trial` | Creates a trial account, shopping list, and session atomically; returns `201` and a session cookie. An existing valid trial returns `200` with the same list and original expiry. |
| `GET /api/auth/me` | Returns the current account, first available list ID, and session expiry; requires a valid session. |
| `POST /api/auth/logout` | Revokes only the calling session, clears the cookie, and returns `204`. |

Trial accounts, their initial lists, and sessions expire together after three hours by default (`Auth:TrialLifetimeHours`). Reuse does not extend any expiry or replace an archived/expired list; an unavailable trial list returns `409`. A registered session cannot be replaced with a trial (`409`). An expired browser session can explicitly start a new trial. Expired records are retained until a later cleanup implementation; shared catalogue data is unaffected.

Session credentials are 256 random bits encoded as lowercase hexadecimal. The database stores their SHA-256 hashes. Browser responses never include the raw credential in JSON; it is carried only in `myshoppinglist_session`, an HttpOnly, host-only, `SameSite=Lax` cookie with path `/`. Cookies are Secure in production and on HTTPS development requests. Use HTTPS in production. Authentication checks session expiry/revocation and account activity/trial expiry on every request, without sliding expiry. Auth responses use `Cache-Control: no-store`.

The authentication handler also accepts `Authorization: Bearer <opaque-session-token>` for a client that already possesses a valid token. An explicit invalid Authorization header never falls back to a cookie. This stage does not provide a separate native-client token-issuance endpoint.

Browser state-changing API requests, including anonymous trial creation, must send `X-MyShoppingList-Request: 1`. The middleware rejects cross-site fetch metadata and any supplied Origin that differs from the API's scheme/host/port. Authenticated bearer requests do not need the browser header. Protection relies on the browser same-origin policy and the absence of credentialed CORS; future frontend deployment should use a same-origin API proxy. Do not enable cross-origin access without revisiting this protection. Local tools may omit Origin but must send the custom header.

Example same-origin browser calls:

```javascript
await fetch('/api/auth/trial', {
  method: 'POST',
  credentials: 'same-origin',
  headers: { 'X-MyShoppingList-Request': '1' }
});
const current = await fetch('/api/auth/me', { credentials: 'same-origin' }).then(r => r.json());
await fetch('/api/auth/logout', {
  method: 'POST',
  credentials: 'same-origin',
  headers: { 'X-MyShoppingList-Request': '1' }
});
```

Trial-start requests are limited to `Auth:TrialRequestsPerWindow` (default 10) per `Auth:TrialWindowSeconds` (default 3600), per remote IP per application instance. Reused trials also count. Rejection returns `429` with `Retry-After`. Limits are held in memory and reset on restart; they are not shared between instances. Forwarded address headers are not trusted automatically. Configure trusted proxies explicitly before relying on client-IP limits behind a proxy.

No database migration is required for this stage. Registered login, account conversion, and expired-data cleanup remain subsequent work.

## Import submission and polling (Stage 4B)

`POST /api/shopping-lists/{listId}/products/url` requires a valid session and the browser request header described above. Send JSON:

```json
{ "url": "https://www.coles.com.au/product/your-product", "quantity": 2 }
```

Quantity defaults to 1 when omitted and must be a positive integer. Invalid JSON, fractional quantities, invalid URLs, and unimplemented source retailers return `400`. Coles and Woolworths are implemented source providers as of Stage 5B. Submission validates the URL allow-list but does not fetch or resolve retailer DNS; the worker's safe transport validates resolved addresses and redirects when it connects.

The endpoint checks active account/trial and list access, then creates a durable queued job with pending retailer rows. No retailer lookup runs in the request. Success returns `202 Accepted`, `Cache-Control: no-store`, and a `Location` header pointing to the status URL:

```json
{
  "jobId": 123,
  "status": "Queued",
  "quantity": 2,
  "reused": false,
  "statusUrl": "/api/product-import-jobs/123"
}
```

Submissions are serialised using account then list row locks. The same normalised URL on the same list reuses an existing `Queued` or `Processing` job, including a job waiting for retry. The response has `reused: true` and retains that job's original quantity. URL fragments are removed; path and query semantics are retained. Once a job is terminal, a new submission creates a new job. Final list insertion still preserves an existing item's quantity and notes according to Stage 3B. The response's `quantity` is the job's requested quantity, not necessarily the quantity of an already-existing list item.

Missing or other users' lists return `404`; owned archived/expired lists return `409`. Unauthenticated or expired sessions return `401`. Submission is limited per account per application instance to `ProductImport:SubmissionRequestsPerWindow` (20) per `SubmissionWindowSeconds` (60); excess requests return `429` with `Retry-After`. Reused/invalid submissions count toward the limit. Polling does not consume this submission allowance.

`GET /api/product-import-jobs/{jobId}` requires the owner session and returns status/progress, requested quantity, list-item reference, product metadata when saved, and retailer result DTOs. Missing, other users', or unavailable-list jobs return `404`. Responses are not cached and exclude claim tokens, leases, raw provider error messages, and EF entities.

Each retailer retains its own status, match confidence, cache flag, check time, and error code. `prices` is an array of current shared catalogue observations with amount, currency, location ID, price scope, source, promotions, and individual check times. These observations may have been refreshed by another import; they are not an immutable quotation from this job. Different scopes/currencies/locations stay separate; no cheapest-price or local-price claim is inferred. Unavailable, failed, unsupported, and pending results do not surface old prices as successful checks. Products appear after source persistence commits, even while the job remains `Processing`.

Status projections use a PostgreSQL repeatable-read transaction so a poll observes one coherent database snapshot. See [PostgreSQL transaction isolation](https://www.postgresql.org/docs/17/transaction-iso.html). The Stage 5A frontend polls approximately every two seconds and stops on `Completed`, `Partial`, `Failed`, or `Cancelled`, session/access loss, or component disposal.

## Import discovery (Stage 5A)

`GET /api/shopping-lists/{listId}/imports?pageSize=20&beforeId=123` discovers imports after a page refresh. Both query parameters are optional: page size defaults to 20 and must be 1–50; `beforeId` must be positive when supplied. Results are newest ID first. The response contains `items` with `jobId`, `status`, and `createdDate`, plus `nextBeforeId` (null on the last page). Pass that cursor to fetch older imports; fetch individual status endpoints for product/retailer details. New submissions do not shift cursor pages.

Only the active owner can discover an active, unexpired list. Other users' or unavailable lists return `404`; invalid pagination returns `400`; invalid/expired sessions return `401`. Discovery uses a consistent database snapshot and sends `Cache-Control: no-store`. It does not consume the submission rate limit or fetch retailers. No migration is required. The frontend runs separately in `D:/pra/myshoppinglist` on port 3001 and forwards approved requests through its same-origin gateway.

No migration is required. Tests use a controlled fake retailer to verify that submission returns before extraction completes, then exercise the real hosted worker and polling endpoint without contacting retailer websites.

## Verification

```powershell
dotnet test myshoppinglist-api.slnx
dotnet ef migrations has-pending-model-changes --project myshoppinglist-api.csproj
```

Model and matching tests run without a database. Set `MYSHOPPINGLIST_TEST_CONNECTION` to a migrated PostgreSQL test database to also run relational constraints, optimistic concurrency, source persistence, price-history, and worker tests. Most database tests roll back a transaction. Separate-context concurrent-import and worker tests commit uniquely identified fixtures and delete those fixtures during cleanup. Worker tests are serialised and refuse to run with unrelated active import jobs; stop any application worker against the test database first. PostgreSQL identity sequences may advance. Tests never drop or recreate the database. Without the environment variable, database tests are explicitly reported as skipped.

Authentication integration tests use `Microsoft.AspNetCore.Mvc.Testing` 10.0.11 to exercise the real HTTP pipeline with the worker disabled and a controlled clock. They use the test connection, capture the IDs of accounts they create, and delete only those accounts (and cascading session/list records) during cleanup. They cover cookie/bearer authentication, request protection, trial reuse, expiry, revocation, and rate limiting. See [ASP.NET Core integration testing](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0).

Parser/HTTP tests are deterministic and do not contact retailers. One separate opt-in test fetches the public Coles page through the safe transport:

```powershell
$env:MYSHOPPINGLIST_LIVE_COLES = '1'
dotnet test myshoppinglist-api.slnx --filter 'FullyQualifiedName~Live_public_page'
Remove-Item Env:MYSHOPPINGLIST_LIVE_COLES
```

This live check can fail when retailer access or page structures change. It must not be used as an always-on CI dependency.

## Woolworths source imports (Stage 5B)

Woolworths product URLs (`https://www.woolworths.com.au/shop/productdetails/{stockcode}/{slug}`) now use the existing safe HTTP transport and durable import worker. Use the current link from the retailer: an outdated slug can return a not-found page even when that stockcode still exists.

The parser reads matching Product JSON-LD and `__NEXT_DATA__.props.pageProps.pdDetails.Product`. It verifies stockcode and GTIN consistency, ignores recommendations, retains missing pack information as unknown, and rejects marketplace products. Conflicting, variable-weight, or member-only prices remain unavailable instead of being presented as unconditional single-pack prices. The observed JSON-LD unit-price label contains package size, so unit prices use the explicit `CupPrice` and `CupMeasure` fields instead. Prices have unknown store scope; anonymous retailer context is not the user's selected store. An explicit AUD currency is required.

The existing matching and persistence services reuse the canonical product for matching GTINs across Coles and Woolworths, subject to contradiction checks. Accepting both source retailers does not automatically search the other retailer; Stage 6A adds search providers, while background integration remains pending. No database migration is required.

Deterministic fixtures retain only relevant public product fields and exclude reviews, tracking, and unrelated application state. The optional live extraction check is:

```powershell
$env:MYSHOPPINGLIST_LIVE_WOOLWORTHS = '1'
dotnet test myshoppinglist-api.slnx --filter 'FullyQualifiedName~WoolworthsProductProviderTests.Live_public_page'
Remove-Item Env:MYSHOPPINGLIST_LIVE_WOOLWORTHS
```

Live retailer availability and page formats can change independently of this application.

## Retailer search providers (Stage 6A)

Coles and Woolworths now implement `SearchAsync`. A deterministic query retains brand, name, variant and pack terms. Search reads one rendered results page, ranks and deduplicates product links, then verifies at most five candidate product pages using the existing safe HTTP extractors, two reads at a time. Candidates are evidence for the matching service, never assertions of an exact match. A successful empty search needs explicit empty-state evidence; Coles' “best guesses” beneath “No results for” are excluded. Shells, challenges, timeouts and unreadable candidates return typed failures instead of an empty success. Store-specific searches remain unsupported.

This stage does **not** connect searches to background import jobs or change the frontend. Imports still show other retailers as unsupported until the next integration stage. No database migration is required.

The browser service uses Microsoft.Playwright 1.62.0 with isolated, non-persistent headless Chromium sessions. Service workers, websocket connections, downloads, image/media/font requests and non-search navigations are blocked. A per-search loopback SOCKS5 proxy allows only the selected retailer's exact hosts (plus `cdn0.woolworths.media` for Woolworths scripts). Every connection validates all DNS answers and connects to a checked numeric address, retaining normal browser TLS verification. There is no direct-network fallback. Both request interception and the proxy apply because Chromium can make speculative connections before an intercepted request. Login, existing browser profiles, CAPTCHA solving and access-control bypass are not used.

Settings use the `RetailerSearch` configuration section, or environment variables such as `RetailerSearch__TimeoutSeconds`:

| Setting | Default |
|---|---|
| `Enabled` | `true` |
| `BrowserChannel` | unset: Playwright Chromium; set `chrome` to use installed Chrome |
| `TimeoutSeconds` | 45, including search and candidate retrieval |
| `MaximumConcurrentBrowsers` | 2 per application instance |
| `MaximumCandidates` | 5 (maximum 8) |
| `MaximumRequests` | 180 per browser search |
| `MaximumTransferBytes` | 40 MiB across proxy traffic in both directions |

Install the Playwright browser after building, or configure an installed Chrome channel:

```powershell
dotnet build myshoppinglist-api.slnx
pwsh -File bin/Debug/net10.0/playwright.ps1 install chromium --only-shell
# Alternatively, for an existing local Chrome installation:
$env:RetailerSearch__BrowserChannel = 'chrome'
```

The browser dependency is loaded only when search is invoked. Source-only imports do not launch it. See the official [Playwright network documentation](https://playwright.dev/dotnet/docs/network) and [browser contexts](https://playwright.dev/dotnet/docs/api/class-browsercontext).

Controlled rendering tests use Playwright Chromium, mocked documents and a rejecting fake DNS resolver; they contact no retailers. Set `MYSHOPPINGLIST_TEST_BROWSER_CHANNEL=chrome` to test an existing Chrome installation instead:

```powershell
$env:MYSHOPPINGLIST_BROWSER_TESTS = '1'
dotnet test myshoppinglist-api.slnx --filter 'FullyQualifiedName~Controlled_browser'
Remove-Item Env:MYSHOPPINGLIST_BROWSER_TESTS
```

Separate opt-in live tests require retailer access and installed Chrome:

```powershell
$env:MYSHOPPINGLIST_LIVE_SEARCH = '1'
dotnet test myshoppinglist-api.slnx --filter 'FullyQualifiedName~Live_search_renders'
Remove-Item Env:MYSHOPPINGLIST_LIVE_SEARCH
```

**Live verification limitation, 2026-09-20:** Woolworths returned HTTP access restriction in the isolated browser; Coles timed out. Both sites displayed results in an ordinary browser session, which does not establish that the server's isolated browser can access them. Live search success is therefore not verified. These failures remain visible as unavailable outcomes; do not advertise reliable automatic comparison on the strength of fixture tests. Source URL importing remains independently implemented.
