# Account-owned AI connections

AI integration is **disabled by default**. The local implementation uses OpenIddict 7.7.2 and the official ASP.NET Core MCP SDK 2.3.0. This document describes configuration and local behaviour, not a production rollout or verified compatibility with every commercial AI application.

## Account and permissions

- Existing registered, active MyShoppingList accounts approve each connection using their first-party browser session. Trial accounts cannot connect. Suspending an account blocks subsequent MCP requests and token renewal.
- Browser sessions and extension login tokens are not MCP access tokens. MCP accepts only dedicated, resource-bound OpenIddict tokens.
- Public clients must be explicitly configured with a client ID, display name and exact callback URLs. Dynamic client registration is not enabled. The client must support authorization code with S256 PKCE and resource indicators.
- Default permissions remain `lists.read` and `products.read`. Explicitly offering `lists.write` registers scoped write tools. Explicitly offering `extension.capture` requires the personal extension bridge and registers customer-approved capture/search tools. Existing grants never gain new scopes automatically.
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

Use process environment or the ignored local settings file. Never commit passwords or private certificates. Apply the reviewed additive migration only to an explicitly selected disposable database during development. Production schema deployment requires separate approval.

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

API tests exercise the real OAuth endpoints and official MCP client, scopes, account isolation, expiry/renewal/revocation, configuration validation, source privacy and PostgreSQL-enforced read-only queries. `playwright.ai.config.ts` runs a separate real API/database/browser journey; the usual mock API suite is not used as evidence for this flow. It owns port 3001, an API on 5392 and a generated test schema; it must not run against production. See the audit/work plan for actual results and outstanding checks.

Setting `AiIntegration:Enabled=false` removes OAuth/MCP connection routes without disabling existing account/list functionality. The account current-list API and selection data remain. Do not downgrade or delete authorization tables containing real grants as a routine feature rollback. The checked-in migration is additive; it has not been approved for production deployment.
