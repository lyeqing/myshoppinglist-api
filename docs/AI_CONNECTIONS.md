# Account-owned AI connections

AI integration is **disabled by default**. The local implementation uses OpenIddict 7.7.2 and the official ASP.NET Core MCP SDK 2.3.0. This document describes configuration and local behaviour, not a production rollout or verified compatibility with every commercial AI application.

## Local activation — 15 November 2026

The owner authorized local activation after the migration rehearsal. Migration `20261114103211_AddPrivateProductResearch` is now applied to local `localhost:5432/MyShoppingList`. A full custom-format backup was restored into an isolated loopback PostgreSQL instance and all public table fingerprints matched using UTC. All pre-existing application table fingerprints also matched after migration; only migration history and the three new research tables changed. The backup, exact SQL, verification manifest and before/after fingerprints are under ignored `.local-backups/20261115-110833-before-mcp-enable`. This local activation supersedes earlier statements that the application database had not been migrated; production remains untouched.

Ignored `appsettings.Local.json` enables the five research/list/product scopes with development ephemeral keys. The frontend on `http://localhost:3001` uses the current HTTPS tunnel as its API origin. The exact transient public issuer/resource is in that ignored file; do not commit it. A current signed ngrok agent is isolated under `.local-dotnet/mcp-tools`; the global installation is unchanged. Request inspection is disabled. The API and tunnel must remain running; a changed tunnel hostname requires updating issuer, resource and frontend API origin. Restarting ephemeral keys invalidates existing OAuth tokens.

`AiIntegration:DevelopmentLoopbackProxy=true` enables forwarded scheme/host/client-IP processing only in Development with MCP enabled. It trusts one hop from exactly IPv4/IPv6 loopback, allows the configured issuer hostname, and runs before protocol authentication. It is not a production proxy configuration. Public PKCE clients advertise the `none` token authentication method; this does not permit unauthenticated MCP access or remove PKCE.

No ChatGPT OAuth client is registered yet: its exact callback and supported client-registration mode must come from the user's connection UI. Proposed predefined client ID: `myshoppinglist-chatgpt-local`; display name: `MyShoppingList local`. No shared client secret is needed by this public PKCE server. Configure only the exact displayed callback; do not guess one or add wildcards. If ChatGPT requires CIMD/DCR instead of accepting a predefined public client, additional authorization-server compatibility work is required. Official SDK compatibility is not commercial ChatGPT certification.

Activation checks: public OAuth/protected-resource discovery returns 200; unauthenticated MCP returns 401 with the protected-resource metadata challenge; the local research page returns 200. Focused OAuth/configuration/proxy regressions pass **23/23**, including official SDK flows, revocation, loopback-only scheme forwarding and host rejection (`mcp-local-activation-accepted.trx`). The first regression run was contaminated by active personal issuer settings; the accepted run used the original local configuration with an isolated test database, then restored the exact active settings. The initial proxy test's HTTP error-code assumption was replaced with direct assertions that untrusted peers cannot alter the scheme. Temporary restored copies were removed and the owned test cluster stopped; the backup is retained.

In ChatGPT, use Plugins → Add custom MCP server (or the legacy Apps creation screen if that is the account's available surface), select OAuth and use the current HTTPS `/mcp` URL. Account/workspace policies may restrict creation. Send back the exact callback URI and whether the UI accepts a predefined client ID without a secret; never send account passwords, ngrok tokens or database credentials. After registration, reconnect, approve the scopes, select the current list in the local website and test a read before research writes. AI evidence still does not become a verified price. See [OpenAI's connection instructions](https://help.openai.com/en/articles/12584461) and [OAuth requirements](https://developers.openai.com/plugins/build/auth).

## Account and permissions

- Creating a registered MyShoppingList account is free; signing in is required for all shopping and AI features. Temporary trial creation is retired (`POST /api/auth/trial` returns 410), and legacy trial credentials cannot authenticate.
- Active registered accounts approve each connection using their first-party browser session. The consent page offers free registration and sign-in while retaining the pending request. Account creation never approves permissions automatically. Suspending an account blocks subsequent MCP requests and token renewal.
- Browser sessions and extension login tokens are not MCP access tokens. MCP accepts only dedicated, resource-bound OpenIddict tokens.
- Public clients must be explicitly configured with a client ID, display name and exact callback URLs. Dynamic client registration is not enabled. The client must support authorization code with S256 PKCE and resource indicators.
- Default permissions remain `lists.read` and `products.read`. Explicitly offering `lists.write` registers scoped write tools. Explicitly offering `extension.capture` requires the personal extension bridge and registers customer-approved capture/search tools. Existing grants never gain new scopes automatically.
- Research adds explicit `research.read` and `research.write` grants. Offering `research.write` registers submission/issue tools; it does not grant list edits. Include `lists.write` separately for candidate selection. Existing clients/grants require a new consent decision to gain either research permission.
- Access tokens expire after five minutes by default. Optional refresh tokens expire after 30 days. Reference tokens, authorization records, issuer, audience and active-account status are validated on each MCP request. Revoking a connection invalidates its authorization and tokens. Already completed or in-flight responses cannot be withdrawn from a client.
- The Connected AI Apps page shows individual grants, granted permissions and last activity; it can revoke a grant. Repeated explicit approvals may create separate connections, which are shown separately.

## Explicit current list

Customers select a current list in **Settings → Connected AI apps and current list**. The selection belongs to the account and uses ownership foreign keys and optimistic version checks. Website/session and extension login/session reads prefer this selection when accessible. Existing website fallback to its first accessible list remains available when no current list exists; that fallback does **not** select an AI current list. Viewing another list does not change the account selection.

The `get_current_list` tool returns `selection_required` until the customer chooses one. Archived or expired selected lists return `selection_unavailable`; the AI must ask the customer to choose again. No read tool creates a list or silently changes selection.

## Tools and price limitations

`get_my_lists`, `get_current_list` and `get_list_items` require `lists.read`. `search_my_products` requires `products.read`. IDs are strings in MCP contracts. Pagination is bounded to 50 results. Unknown/malformed arguments, missing permissions and inaccessible IDs return safe machine-readable errors. Unknown and other-account IDs use the same not-found outcome.

Read tools reuse account-scoped list/product services and never initiate retailer collection. Saved prices include source type, observation time, exact-match status, currency and observed store/location. They are not live checks. Empty `savedPrices` means no known observation, not a zero price. Non-AUD/invalid monetary values are null rather than converted. Location-specific observations are labelled as such; a preferred branch is never substituted for an observed branch. Unknown unit-price basis is explicitly `unknown` and must not be used to infer comparable unit prices. Saved promotion conditions remain untrusted captured text.

Only established Coles/Woolworths public product-route source URLs are currently returned; tracking queries/fragments and unknown/private routes are removed. Other sources can retain their source type while their URL is null. List names, notes, product names and captured conditions are untrusted data, never instructions to the AI.

Read-only shopping services do not write. The transport separately updates connection last-used metadata at most once per minute. Fixed-name tool counters/log events include tool, outcome and existing request correlation; they exclude arguments, results, account IDs, tokens and private URLs. Counters are in-process instruments; durable reporting/export belongs to the observability phase.

## Writes and personal extension requests

`create_list`, `set_current_list`, `add_product_to_list`, `update_list_item` and `remove_list_item` require `lists.write`. UUID request IDs are account-bound, atomic with their mutation, and must be reused unchanged after uncertain responses. Changed inputs under an existing ID are rejected. Item edits require the current update timestamp; removal also requires explicit confirmation. Generic items have no invented retailer identity or price. Selecting a list uses a monotonic account revision, including after the selected list is deleted.

For extension tools, explicitly enable `ExtensionBridge:Enabled` and `UserExtension:PersonalCapturesEnabled`, allow the installed extension ID, and offer/approve `extension.capture`. The bridge defaults to OFF. It supports only the customer's own linked browser, with a dedicated device credential bound to its account, extension ID and active sign-in session. The secret is returned once, hashed on the server, and retained only in Chrome session storage. It cannot authenticate ordinary website APIs or MCP. Logout/account switching clears local credentials; revoked/expired source sessions and browser unlinking block further access.

Open **AI requests** from the extension, sign in and explicitly link the browser. That native top-level extension page polls while visible; there are no alarms, sockets or background wakeup promises. Recent presence means an approval prompt may be received, not execution success. `get_extension_status` distinguishes missing, offline, permission, recent/interactive and awaiting-approval conditions. MCP requests are private to the originating account and AI connection. Another linked browser can receive an unassigned own-account task, but only the first customer-approved claim can execute it.

The customer clicks **Open page and review** before any supported URL opens. The extension uses the existing bundled reader, checks the active page identity before/after reading, and requires a separate **Save to list** or **Share reviewed links** action. Capture saves the requested quantity (replacing it when the product is already on the list). Cancellation writes no product. Search reads a single Coles/Woolworths results page, at most five candidate links, without paging or list additions. Missing candidate names remain null; prices/availability are unverified. Retailer restrictions stop the task without bypass attempts. No arbitrary AI-provided JavaScript executes.

Defaults: tasks expire after 10 minutes, recent presence after 45 seconds, at most five pending tasks and ten active browser links. Device endpoints are separately rate limited. Expired/revoked/list-unavailable tasks cannot persist captures. Completed evidence is immutable and retryable using the same claim and body; capture persistence and completion commit together. Worker restart preserves the pending body in session storage; without a draft, explicit resume obtains a new claim and reads again. Lost connectivity never becomes a successful outcome automatically.

The Connected AI Apps page lists own browser links, last contact, version/session state and confirmed unlinking. Stored task results contain private product facts, not unrelated browsing history, cookies or whole page HTML. Task/device retention, account export/deletion and operational reporting are addressed in the operations phase. Disable the bridge to stop new routes/tools; retain tables and history. Migration rollback refuses any nonempty device/task table. Migration deployment remains separately approved.

Chrome implementation references: [service worker lifecycle](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle), [bundled static module imports](https://developer.chrome.com/docs/extensions/develop/migrate/improve-security), and [scripting permissions](https://developer.chrome.com/docs/extensions/reference/api/scripting). Browser suspension is expected; it must not be hidden by indefinite keepalive loops.

## Local configuration

Use process environment or the ignored local settings file. Never commit passwords or private certificates. Rehearse migrations on a disposable database first; applying them to the explicitly selected local application database requires a verified backup and owner authorization. Production schema deployment requires separate approval.

```json
{
  "AiIntegration": {
    "Enabled": true,
    "Issuer": "http://localhost:5392/",
    "Resource": "http://localhost:5392/mcp",
    "FrontendOrigin": "http://localhost:3001",
    "DevelopmentEphemeralKeys": true,
    "AllowedScopes": ["lists.read", "products.read"],
    "RequestsPerMinute": 120,
    "Clients": [{
      "ClientId": "your-preregistered-client",
      "DisplayName": "Your trusted AI app",
      "RedirectUris": ["http://localhost:3001/your-client-callback"]
    }]
  }
}
```

HTTP and ephemeral keys are development-loopback only. Ephemeral keys invalidate existing tokens when the process restarts. Production requires HTTPS issuer/frontend/callbacks and persistent signing/encryption PKCS#12 certificates via `SigningCertificatePath`, `EncryptionCertificatePath` and optional passwords. Certificate rotation, protected key storage, database/token pruning, proxy host forwarding and backup operations require deployment configuration and validation before enabling production. No certificates or paid services are provisioned automatically.

`/.well-known/oauth-authorization-server` advertises the authorization server; `/.well-known/oauth-protected-resource/mcp` advertises the MCP resource. `/connect/authorize`, `/connect/token` and `/connect/revoke` are OpenIddict protocol endpoints. Consent uses short-lived OpenIddict cached authorization requests with explicit client IDs. The website gateway accepts fixed consent fields, validates browser request protection and converts only the validated decision redirect into a client navigation result. It never forwards arbitrary bearer credentials.

MCP checks the configured issuer host and supplied browser Origin. Extra browser-client origins require explicit `AllowedBrowserOrigins`. No credentialed cross-origin browser access is enabled implicitly. Per-IP protocol limits run before authentication; authenticated MCP requests also have a per-account limit. Commercial entitlement-specific quotas remain a product decision.

## Verification and rollback

### Customer-initiated research (14 November 2026)

Apply additive migration `20261114103211_AddPrivateProductResearch` to the explicitly reviewed target before using the research workspace. It was initially rehearsed on disposable data and is now applied to the authorized local application database as recorded above; production is unchanged. The website at `/research` prepares a request to copy into a connected AI app, reviews private submissions, adds a chosen candidate at the requested package quantity and records unresolved outcomes. It also supports a manual item without a price. Opening ChatGPT/Claude does not send or execute the prepared request. A structured-response import is available for clients without write support; no personal API key or paid backend AI dependency is used.

| MCP contract | Required scope / behaviour |
| --- | --- |
| `get_research_instructions` | `lists.read`; centrally versioned rules, bounds and structured fallback example. Call before research. |
| `get_shopping_context` | `lists.read`; lists/current selection, retailer and branch preferences and pack/brand toggles. No account email, credentials or admin data. |
| `get_research_review(jobId?)` | `research.read`; omit ID for latest 50 job summaries, supply it for up to 20 candidates plus all job issues. Jobs remain accessible by known ID beyond the summary window. |
| `submit_product_research(submission)` | `research.write`; immutable batch of at most 20 candidates and 20 retailer issues, at most 60 KB total/10 KB per candidate. UUID request ID, query and exact workflow version required. Returns `researchJobId`; use review for detailed outcomes. Identical retries replay, changed inputs conflict. |
| `add_product_to_list(..., candidateId)` | `lists.write`; exactly one candidate/product/generic selector. Candidate must be owned and accepted for review. Candidate quantity is 1–10000 packages. Existing item quantity/notes stay unchanged; use normal versioned edits to change them. Research selection never creates an eligible price observation. |
| `get_price_refresh_tasks(listId, pageSize=20)` | `research.read` + `lists.read`; at most 50 private mapping/scope/location targets for an owned active list, excluding hidden/purchased items. A database-only projection, with no claims, jobs, network requests or automatic execution. |
| `resolve_research_issue(issueId, update)` | `research.write`; UUID, expected revision, `Open`, `Resolved` or `CannotVerify`, and a reason. Acknowledgement changes workflow status only, never price verification. |

`research_groceries` is the reusable MCP prompt; initialization instructions and `get_research_instructions` remain available when a client does not load prompts. `search_my_products` and the existing list tools keep their existing contracts. Write discovery remains configuration-dependent; every invocation enforces its own scopes.

Submission fields are defined in `Contracts/ResearchContracts.cs`: source key/retailer/product URL, `ProductIdentity`, bounded factual evidence, UTC observation time, optional AUD price/promotion/location, proposed match classification, optional owned related product and AI confidence. Tissue evidence optionally includes rolls, sheets per roll, ply and dimensions. The server validates retailer URLs/codes using existing parsers, GTIN checks, dates, dimensions and ownership. Exact/package/alternative claims against a related product must pass existing deterministic matchers. All sources/text remain untrusted data.

Accepted means **structurally valid, unverified AI report**, not verified retailer truth. Rejected candidates remain visible and unselectable. Duplicate URLs within a batch are rejected; existing retailer mappings/products are resolved using existing identity services at selection. Partial batches retain successful and failed candidates separately. Reports/corrections append new immutable batches; submission never adds a list item. Product selection preserves existing product metadata, mappings, current prices and check dates. New research-only mappings are `UserConfirmed`, with no checked price date. Reports and unit arithmetic are shown only in research review, excluded from existing basket totals/savings. There is intentionally no AI-to-verified-price promotion path.

Freshness settings: `Research:ActivePriceHours` defaults to 24 (1–168), and `Research:RetryHours` to 6 (1–168). Refresh projection considers missing prices, observation age, promotion boundaries and recent open inaccessible-retailer outcomes. Existing legacy weekly comparison eligibility is unchanged. The projection requires an existing private retailer mapping; generic items without one need ordinary product research. Scope/location and previous evidence are returned, never silently replaced with a preferred branch. Each submitted refresh is a new history batch; failed searches never update current/verified price dates. No inactive-product scheduler or personal AI automation is installed.

Website API routes share the same services under `/api/research`: instructions, latest jobs, `/{jobId}`, `/refresh/{listId}`, POST submission, POST `/select`, PUT `/issues/{issueId}`, and POST `/generic/{listId}`. They require an authenticated session; registered-account eligibility is checked for private reads/writes. Session mutations retain browser request protection and account rate limits. Submission accepts 64 KiB at transport while its domain envelope is bounded to 60 KB; other writes retain the ordinary limits. The Next.js gateway explicitly allowlists these paths, methods, pagination and body limits. The workspace sends `X-MyShoppingList-Account` as an optional consistency fence: a mismatch with the authenticated account returns 409, never changes authorization. Account changes cancel requests and clear drafts. New candidate product/location IDs serialize as decimal strings to preserve 64-bit values in JavaScript.

Research tables carry account IDs; candidate/issue FKs include job and account. A PostgreSQL trigger prevents evidence mutation and cross-account selected-product links. Jobs are account-owned rather than attached to a list, so deleting a list does not erase unresolved research. Candidate selection validates the chosen list afresh. Account support export discovers these owned tables automatically. Evidence is retained with the account and is not shared; there is no new automatic purge or cross-user cache. Issue records retain their latest revision/reason; mutation receipts preserve retry identity. A full issue-event timeline is not implemented. Populated research rollback refuses to discard evidence; disable new entry points and use a reviewed roll-forward/recovery rather than dropping data.

Test command for the separate browser journey: start an owned API at `127.0.0.1:5499` against an explicitly disposable migrated PostgreSQL database, set `MYSHOPPINGLIST_RESEARCH_BROWSER=1`, then run `node node_modules/@playwright/test/cli.js test --config playwright.research.config.ts` in the frontend. Its frontend owns port 3001 and refuses to reuse a running server. Test prices are synthetic. Keep normal production/application targets out of this harness. See the migration plan for recorded test results and external ChatGPT/Claude prerequisites.

API tests exercise the real OAuth endpoints and official MCP client, scopes, account isolation, expiry/renewal/revocation, configuration validation, source privacy and PostgreSQL-enforced read-only queries. `playwright.ai.config.ts` runs a separate real API/database/browser journey; the usual mock API suite is not used as evidence for this flow. It owns port 3001, an API on 5392 and a generated test schema; it must not run against production. See the audit/work plan for actual results and outstanding checks.

Setting `AiIntegration:Enabled=false` removes OAuth/MCP connection routes without disabling existing account/list functionality. The account current-list API and selection data remain. Do not downgrade or delete authorization tables containing real grants as a routine feature rollback. The checked-in migration is additive; it has not been approved for production deployment.
