# MyShoppingList Technical Guide

Developer and maintainer reference for the API, website and personal-product Chrome extension. Prepared on 7 November 2026 from the checked-out source and project runbooks.

Commands below are instructions for the operator; they were not executed while preparing this guide. No database state, installed tools, running services, live retailer access or external provider configuration was verified for this document. Older README stage descriptions can describe superseded behaviour; use the current source and linked specialist runbooks when they differ.

## 1. Repository and technology map

| Component | Location | Current source-defined stack |
| --- | --- | --- |
| API | `D:\pra\myshoppinglist-api` | ASP.NET Core / .NET 10, EF Core 10.0.11, Npgsql EF provider 10.0.3, PostgreSQL |
| Website | `D:\pra\myshoppinglist` | Next.js 16.4.3, React 19.2.8, TypeScript, Tailwind CSS 4, Zustand 5.0.15 |
| Personal extension | `D:\pra\myshoppinglist\extensions\shopping-list` | Chrome Manifest V3, JavaScript modules, extension version 0.8.0 |
| API tests | `tests/myshoppinglist-api.Tests` in the API repository | xUnit, ASP.NET test host, PostgreSQL fixtures |
| Website tests | `tests` and `playwright.*.config.ts` in the website repository | Playwright, desktop/mobile fixtures and separate real integration journeys |

These versions describe this checkout, not a recommendation to upgrade dependencies.

## 2. Architecture and request flow

```text
Website browser
  -> Next.js /api/* gateway
    -> ASP.NET Core endpoint
      -> account-scoped service
        -> EF Core / PostgreSQL

Chrome personal extension
  -> user-requested product read on a supported retailer page
  -> API preview
  -> customer review
  -> API capture save
    -> private product / retailer mapping / observation / list item

Website Refresh items
  -> local Chrome extension bridge
    -> known product pages in temporary tabs
      -> API private price-refresh save
```

The website calls same-origin APIs. Its gateway forwards only explicitly allowed routes and methods to the configured backend. The extension calls the local API directly using its own authentication and extension-ID checks.

The current personal workflow saves account-owned products and dated observations. Confirmed Coles captures start bounded, account-owned Woolworths and ALDI comparisons in the user extension. Opening a panel or previewing a product does not start comparison work; unattended shared collection remains disabled. Optional AI requests use a separate own-device approval/review workflow.

Legacy import, shared catalogue and worker code remains in the repository. `UserExtension:PersonalCapturesEnabled` suppresses legacy import/refresh workers and guards collection routes. Do not enable old workers to repair a personal-capture failure. Historical shared data and private-copy conversion receipts must be preserved according to the migration runbook.

## 3. Prerequisites and ports

- .NET SDK 10.
- EF CLI 10.0.11, matching the project README.
- Node.js 22 LTS, as documented by the frontend repository, and npm.
- PostgreSQL with an explicitly selected database and suitable application/migration privileges.
- Chrome 142 or newer for the current extension.
- PostgreSQL client tools for backup/restore and the real extension integration tests.

| Port | Purpose |
| --- | --- |
| 3000 | Reserved for the user's frontend server; `npm run dev` defaults here |
| 3001 | Frontend started by Codex; current Playwright website configurations also own this port |
| 5392 | Local API; some integration journeys start their own API here |
| 5499 | Mock API used by ordinary frontend browser tests |
| 55432 | Disposable loopback PostgreSQL used by the restricted backup/restore verification procedure |

Never stop or restart the user's port-3000 server. Check port 3001 before starting a server or browser tests. If occupied, ask before stopping its process or selecting another port. Tests configured with `reuseExistingServer: false` require an available port.

## 4. API configuration and startup

From PowerShell:

```powershell
Set-Location D:\pra\myshoppinglist-api
dotnet --version
dotnet ef --version
dotnet restore myshoppinglist-api.slnx
dotnet build myshoppinglist-api.csproj
```

If `dotnet ef` is missing, install the documented version in the intended tool scope. For an explicitly chosen global installation:

```powershell
dotnet tool install --global dotnet-ef --version 10.0.11
```

Do not replace an existing installation without checking its use by other projects.

### Configuration sources

`Program.cs` adds optional `appsettings.Local.json`, followed by environment variables, to the normal ASP.NET configuration. Environment overrides therefore take precedence over that local file. Environment variable names use double underscores for nested keys, for example `ConnectionStrings__MyShoppingList`.

Merge required values into existing private configuration; do not overwrite it with an example. The connection-string shape is:

```json
{
  "ConnectionStrings": {
    "MyShoppingList": "Host=localhost;Port=5432;Database=MyShoppingList;Username=YOUR_USER;Password=YOUR_PASSWORD"
  }
}
```

The documented database name is `MyShoppingList`. Replace placeholders privately. Credentials must not be committed or pasted into logs. The project explicitly excludes `appsettings.Local.json` from publish output.

### Database preparation

The runtime does not create or migrate the database automatically. Before any application-database migration, confirm the server, database and search path; take a protected backup and rehearse restoration and upgrade separately. Existing databases must retain their data and migration receipts.

Inspect available migrations:

```powershell
dotnet ef migrations list --project myshoppinglist-api.csproj
```

After the specific target and migration operation are approved:

```powershell
dotnet ef database update --project myshoppinglist-api.csproj
```

EF may create an absent database if the role has permission. Select the target deliberately. Do not use database reset, `EnsureCreated`, manual one-table repairs or destructive rollback to repair schema drift.

Start the API after configuration and schema preparation:

```powershell
dotnet run --project myshoppinglist-api.csproj --launch-profile http
```

The HTTP profile selects Development and `http://localhost:5392`. Development API documentation is available at `http://localhost:5392/swagger`, backed by `/openapi/v1.json`.

Health checks:

```powershell
Invoke-RestMethod http://localhost:5392/health/live
Invoke-RestMethod http://localhost:5392/health/ready
```

Liveness reports the host is running. Readiness checks database connectivity and pending migrations; it does not establish retailer or external-provider availability.

## 5. Website configuration and startup

```powershell
Set-Location D:\pra\myshoppinglist
npm ci
```

The repository's `.env.example` documents:

| Variable | Purpose |
| --- | --- |
| `MYSHOPPINGLIST_API_URL` | Backend origin; local value `http://localhost:5392` |
| `MYSHOPPINGLIST_PUBLIC_ORIGIN` | Exact browser-facing origin; match the hostname and port used |
| `NEXT_PUBLIC_RELEASE` | Public release label; example value `local` |
| `MYSHOPPINGLIST_NEXT_DIST_DIR` | Optional isolated build directory; defaults to `.next` |

Use `.env.local` or process environment variables. For a Codex-owned development server, first confirm port 3001 is available, then use a command-line override:

```powershell
$env:MYSHOPPINGLIST_API_URL = 'http://localhost:5392'
$env:MYSHOPPINGLIST_PUBLIC_ORIGIN = 'http://localhost:3001'
node node_modules/next/dist/bin/next dev --webpack --port 3001
```

Open `http://localhost:3001`. The user's `npm run dev` uses port 3000; do not change that default for agent work. `localhost` and `127.0.0.1` are different browser origins. Use `localhost` for the current extension bridge, whose allowed website origins are localhost ports 3000 and 3001.

For a local production build, stop any owned server using the same build directory first:

```powershell
npm run build
npm run start
```

`npm run start` currently uses port 3001. A local production build is not deployment. Build and runtime public-origin values must match the intended deployment; public metadata requires rebuilding for the real canonical origin.

## 6. Personal Chrome extension integration

1. Start the prepared API and website.
2. Open `chrome://extensions`, enable Developer mode and choose **Load unpacked**.
3. Select `D:\pra\myshoppinglist\extensions\shopping-list`.
4. Copy its 32-character extension ID.
5. Have the API owner merge the ID into `UserExtension:AllowedExtensionIds` in private configuration.
6. Confirm the private-data rollout and schema requirements before enabling `UserExtension:PersonalCapturesEnabled`. Explicitly disable legacy `ProductImport:Enabled` and `PriceRefresh:Enabled` in the deployment configuration.
7. Restart the API after approved configuration changes. Reload the unpacked extension after extension updates.
8. Sign in using the same Chrome profile, open a supported product page, and use **Read product → review → Save to list**.

The API defaults to no allowed extension IDs and personal capture disabled. Installing the extension does not activate server support. This guide does not inspect or disclose the current private configuration.

The local extension fixes its API URL at `http://localhost:5392` and its website link at `http://localhost:3000/`. Session/refresh bridge support includes localhost ports 3000 and 3001. A remote deployment requires reviewed origin, cookie, manifest and published-ID changes.

### Capture and refresh contracts

| Method and API path | Responsibility |
| --- | --- |
| `POST /api/user-extension/login` | Extension login; returns session information and a token |
| `POST /api/user-extension/captures/preview` | Parse and validate evidence for review |
| `POST /api/user-extension/captures` | Save the reviewed capture |
| `GET /api/user-extension/price-refresh/{listId}` | Read eligible existing private mappings |
| `POST /api/user-extension/price-refresh/` | Save a refreshed observation |

See `Endpoints/UserExtensionEndpoints.cs` and the request types in `Contracts` for exact payloads. The extension sends `X-MyShoppingList-Extension`; the API validates its configured ID and supplied extension origin. The ID is an allowlist identifier, not a replacement for authentication.

Previewing alone creates no product, observation or list. Saving uses a stable request ID for safe retries. The extension locks a pending save to its original evidence, destination and quantity. Pending state survives worker suspension while Chrome remains open, but not a full browser restart or extension reload; check the list before recapturing after an uncertain result.

**Refresh items** works sequentially through known eligible product mappings and skips purchased/hidden items. Cancellation retains already saved results. It does not discover products, change quantities/notes or bypass retailer verification. Previous observations survive unsuccessful updates.

## 7. Source-code map

Paths in this section are relative to their respective repository roots.

| Area | Main locations |
| --- | --- |
| API composition, middleware and service registration | `Program.cs` |
| HTTP routes and request/response shapes | `Endpoints/`, `Contracts/` |
| Business rules and account-scoped operations | `Services/` |
| Persistence model and schema | `Models/`, `Data/MyShoppingListDbContext.cs`, `Data/Configurations/`, `Data/Migrations/` |
| Authentication, request protection and collection guard | `Security/` |
| Typed configuration | `Configuration/` |
| Retailer parsing and retained transports | `Providers/` |
| Background workers, including retained legacy workflows | `Workers/` |
| OAuth/MCP tools and connection validation | `Mcp/` |
| Website pages | `src/app/` |
| Website gateway | `src/app/api/[...path]/route.ts` |
| Client API wrapper and shared types | `src/lib/api-client.ts`, `src/lib/api-types.ts` |
| Website presentation | `src/components/` |
| Website state | `src/stores/` and component providers |
| Extension service worker, UI and website bridge | `extensions/shopping-list/background.js`, `popup.js`, `website-session.js` |

Keep business rules in services and ownership enforcement on the server. Changes to a backend endpoint may also require gateway allowlist, API types, client state and browser-test updates.

## 8. Authentication and API conventions

- Website sessions use the `myshoppinglist_session` HttpOnly cookie. Sessions persist token hashes server-side; raw tokens must not be logged.
- Browser mutations require the established `X-MyShoppingList-Request: 1` header and origin/request protection. Use the existing client wrapper rather than introducing a second request path.
- The Next.js gateway forwards the application cookie, applies route/method/body limits and uses a 15-second upstream timeout. The client wrapper uses a 20-second timeout and returns safe error/reference information.
- Native mobile endpoints issue bearer tokens rather than website cookies. OAuth/MCP tokens are separate credentials; ordinary website or extension tokens are not MCP access tokens.
- Writes can require an expected account ID, item timestamp or selection revision. Obtain the current contract from source; do not reuse stale values after switching accounts.
- For item updates, preserve `updatedDate` exactly as `expectedUpdatedDate`. A 409 requires reloading and reconciling changes rather than silently overwriting.
- Backend account ownership and list availability checks apply even when IDs are supplied by a trusted-looking client.
- Private responses use no-store handling. Authentication/rate-limit failures must not be treated as empty successful results.

Representative route families:

| API path | Purpose |
| --- | --- |
| `/api/auth/*` | Trial, registration, login, session and logout |
| `/api/shopping-lists/manage` | Active-list management information |
| `/api/shopping-lists/{listId}/items` | Paginated items |
| `/api/shopping-lists/{listId}/items/{itemId}` | Item update or deletion, according to HTTP method |
| `/api/shopping-lists/{listId}/plan` | Planning comparison |
| `/api/shopping-lists/{listId}/in-store` | In-store view |
| `/api/account/products` | Product history, Favorites and Essentials |
| `/api/account/current-list` | Explicit account-owned current-list selection |

This is a navigation map, not a complete API specification. Inspect endpoint files and Development OpenAPI for methods and payloads. The expired-session reset route is gateway-specific; do not assume every website `/api` operation maps to an identical backend endpoint.

## 9. Data and price integrity

Core entities cover accounts/sessions, lists/items, products, retailer mappings, current prices/history and product-library preferences/activity. Additional migrations support current-list selection, extension devices/tasks, OAuth, mutation receipts, operations, billing and recovery.

- Personal products and observations must remain scoped to their owner. Never infer ownership from an old shared contribution alone.
- List edits use optimistic concurrency. Database constraints and transactional services protect ownership and related writes.
- Prices retain currency, scope, observation time and conditions. Unknown prices remain unknown; they are not zero.
- Store preference is separate from observed store identity. Unknown location must not be relabelled as a preferred branch.
- Basket estimates include quantities and eligible explicitly chosen alternatives. An incomplete basket is a subtotal; a lowest estimate can require more than one shop.
- Retained legacy private-copy migration maps are durable receipts. Do not delete receipts, re-run conversions blindly or reset history to force a downgrade.

Some schema dependencies apply even with integrations disabled: session restoration uses current-list tables. Disabling MCP does not make an older schema compatible with the current API. Password/recovery/email workflows additionally require `20261101001539_AddAccountRecoveryAndEmailOutbox`; see the account-security runbook.

## 10. Optional integrations

| Feature | Configuration / runbook | Important boundary |
| --- | --- | --- |
| AI OAuth/MCP | `AiIntegration`, [AI connections](docs/AI_CONNECTIONS.md), [MCP security](docs/MCP_SECURITY.md) | Disabled by default; exact registered callbacks, PKCE, dedicated scoped tokens and account consent |
| Own-device AI capture/search | `ExtensionBridge`, [AI connections](docs/AI_CONNECTIONS.md) | Disabled by default; linked own browser, explicit approval and review; cannot start Chrome |
| Password recovery and email | `AccountRecovery`, `EmailDelivery`, [account security](docs/ACCOUNT_SECURITY.md) | Disabled by default; requires schema, protected keys and verified delivery configuration |
| Feedback, diagnostics and notifications | `Operations`, [operations](docs/OPERATIONS.md) | Operations disabled by default and notification delivery simulated by default |
| Billing | `Billing`, [test billing](docs/BILLING_TEST_GROUNDWORK.md) | Test-only, disabled by default; cannot grant paid entitlement or activate production payments |
| Administrator access | `Admin:AccountIds` | Explicit server-side account allowlist, empty by default |

Password recovery and password changes are implemented; older README text saying they are unimplemented is historical. Email ownership verification at registration and MFA are not introduced by that implementation. Credential changes revoke sessions, AI authorizations and linked extension access as described in the account-security runbook.

Do not activate optional integrations merely to make their pages visible. Follow each runbook's schema, key, provider and deployment requirements.

## 11. Validation commands

### API build and tests

From the API repository:

```powershell
dotnet build myshoppinglist-api.csproj
dotnet test myshoppinglist-api.slnx
dotnet ef migrations has-pending-model-changes --project myshoppinglist-api.csproj
```

Without `MYSHOPPINGLIST_TEST_CONNECTION`, database-dependent tests report explicit skips. A passing non-database run is not relational verification.

For relational verification, use a disposable migrated PostgreSQL test database. Configure **both** `MYSHOPPINGLIST_TEST_CONNECTION` and `ConnectionStrings__MyShoppingList` privately to the test target. Some fixtures create isolated schemas; others commit fixture records or advance sequences. Never use the application database as a full-test target. Let feature fixtures choose their own options; blanket environment overrides can mask fixture configuration and cause misleading failures.

### Website and extension

From the frontend repository:

```powershell
npm run lint
npm run build
npm run test:regression
node node_modules/@playwright/test/cli.js test --reporter=line
```

Ensure the configured browser is installed. To use installed Chrome for ordinary browser tests, set `PLAYWRIGHT_CHANNEL=chrome` in the test process. The ordinary suite uses its own frontend on port 3001 and mock upstream on 5499; it does not verify live retailers.

The regression npm script includes personal extension, AI-bridge, retained worker extraction, SEO and API-client security tests. For the personal extension alone:

```powershell
node --test extensions/shopping-list/tests/extension.test.mjs
```

### Real integration journeys

The separate `playwright.personal.config.ts` uses the real API on 5392, the website on 3001, an isolated PostgreSQL schema and a temporary Chromium profile. Build the API first. Supply `MYSHOPPINGLIST_TEST_CONNECTION` and matching `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD` and `PGDATABASE` privately; optional `PSQL_PATH` and `MYSHOPPINGLIST_API_PROJECT` select tools/project. Confirm the required ports are free; do not stop another owner's processes.

```powershell
node node_modules/@playwright/test/cli.js test --config playwright.personal.config.ts
```

AI, operations and account-security journeys have separate `playwright.*.config.ts` files. Read each configuration and fixture before running; their dependencies differ.

For the production-build customer/admin regression, first stop any owned process using `.next-playwright`, then build the matching test assets in the same frontend PowerShell process:

```powershell
$env:MYSHOPPINGLIST_API_URL = 'http://127.0.0.1:5499'
$env:MYSHOPPINGLIST_PUBLIC_ORIGIN = 'http://127.0.0.1:3001'
$env:MYSHOPPINGLIST_NEXT_DIST_DIR = '.next-playwright'
npm run build
node node_modules/@playwright/test/cli.js test --config playwright.regression-production.config.ts
```

These are test-process values; restore the intended development values before the next development session. Reports go to ignored `test-results`, with production output under `test-results/production`. Fixture-based extension/browser success does not establish live retailer extraction, current installed-profile behaviour or external-provider delivery.

## 12. Troubleshooting

| Symptom | Checks and next action |
| --- | --- |
| Website cannot connect / gateway failure | Check API process and port 5392, `MYSHOPPINGLIST_API_URL`, readiness and API logs. Keep the error reference for correlation. |
| `42P01` or missing `UserCurrentLists` | Verify actual database/schema and migration history. Follow backup/restore and approved migration procedures; do not manually add one table or swallow the error. |
| Readiness unavailable | Check PostgreSQL connectivity, credentials, selected search path and pending migrations. Liveness alone is insufficient. |
| Extension not enabled on server | Check the loaded extension ID against `UserExtension:AllowedExtensionIds`, personal-capture configuration and whether the API restarted after changes. |
| Refresh cannot reach extension | Use Chrome in the same profile on localhost:3000 or localhost:3001. Check enabled extension, reload after updates and use the UI's Try again / Reload this page guidance. |
| Retailer page blocked, missing data or identity conflict | Open and check the product manually, complete verification, then read again. Do not bypass challenges or silently replace old observations. |
| 401 | Session expired or credential invalid. Sign in again. An expired trial cannot be recovered by resetting its cookie. |
| 403 on browser write | Inspect origin, custom request header and gateway route/method checks. Do not remove request protection. |
| 409 on edit | Load current saved data and reconcile the draft. Preserve timestamp precision and confirm the active account. |
| 429 | Respect `Retry-After`. Authentication limits are in-memory per peer; multiple users behind the website gateway can share a peer address. |
| Browser tests refuse to start | Confirm configured ports are free. Current configs own port 3001 and refuse server reuse; do not stop the user's port 3000. |
| Optional page unavailable | Check the relevant feature runbook and configuration. An unavailable feature is not permission to enable a provider. |
| Next build conflicts or stale assets | Confirm build/dev servers are not sharing an active output directory. Use the intended `MYSHOPPINGLIST_NEXT_DIST_DIR`; stop only owned processes. |

API logs use console output and rolling `logs/myshoppinglist-*.log` files. Configuration limits file size and retained files and selects specific operational sources; not every event is logged. Preserve safe trace/request references and avoid including credentials, cookies, reset links or customer data in diagnostics.

## 13. Maintenance, backup and release

For changes in this project, obtain approval for the exact files and ordered plan before editing. Only implement approved scope. Keep port 3000 reserved and follow the repository's product-quality rules.

Before a release:

1. Review [release readiness](docs/RELEASE_READINESS.md) and the specialist runbooks for the intended features.
2. Confirm the deployment target, exact HTTPS origins, database/schema, privileges, persistent key storage, extension IDs and OAuth callbacks.
3. Verify a protected backup by restoring to a separate environment. Apply approved migrations there and compare retained data before proposing application migration/cutover.
4. Run checks appropriate to the change and distinguish passes, failures, skips and fixture evidence from live acceptance.
5. Publish the API only when packaging is intended:

   ```powershell
   dotnet publish myshoppinglist-api.csproj --configuration Release
   ```

6. Inspect artifacts to confirm private local settings are absent. Supply deployment secrets through approved private configuration.
7. Build the website with the actual canonical origin. Review HTTPS cookies, proxy trust/client-IP handling and rate limits on the actual host.
8. Verify health, sign-in, list editing, private capture and applicable revocation/recovery flows on the approved deployment before declaring acceptance.

The restricted `tools/operations/verify_backup_restore.ps1` rehearses backup/restore on loopback 55432 and an explicitly named test database. It is not a general live-database restore command. See [operations](docs/OPERATIONS.md) for its prerequisites and export/privacy handling.

Current code and local verification do not establish a commercial deployment, live payments, real delivered email, Chrome Web Store approval or retailer access/reuse permissions. Keep those acceptance decisions tied to actual deployment/provider evidence.

## 14. Further references

- [User guide](USER_GUIDE.md)
- [API README](README.md)
- [Release readiness and schema troubleshooting](docs/RELEASE_READINESS.md)
- [Security architecture](docs/SECURITY_ARCHITECTURE.md)
- [Security testing](docs/SECURITY_TESTING.md)
- [Account security and email](docs/ACCOUNT_SECURITY.md)
- [AI connections](docs/AI_CONNECTIONS.md)
- [MCP security](docs/MCP_SECURITY.md)
- [Operations and backup/restore](docs/OPERATIONS.md)
- [Test billing boundaries](docs/BILLING_TEST_GROUNDWORK.md)
- Frontend README: `D:\pra\myshoppinglist\README.md`
- Personal extension README: `D:\pra\myshoppinglist\extensions\shopping-list\README.md`


## Private retailer comparison rollout

Apply the prepared AddPersonalRetailerComparisons migration only after explicit owner approval. It adds one private comparison-state table, a unique capture-receipt index and account/job foreign keys; it does not rewrite existing products or prices. Do not deploy the new shopping-list plan query before applying it. Restart the API and reload the extension after deployment. Do not enable legacy shared workers or change PersonalCapturesEnabled to restore comparisons.

The /api/user-extension/comparisons endpoints require account authentication and the configured extension allowlist. The source capture commits independently before comparison creation. Each run belongs to a committed Coles capture and its account-owned active list item. Steps are fenced by tokens; the last receipt replays safely, obsolete results after cancellation fail, and source quantity/notes/state are never modified by comparison writes. Exact matches use existing validated private mapping and price persistence. Candidate alternatives are stored in private comparison state and excluded from planning totals. Existing private products are not silently merged.

The extension processes at most two queries and five candidate reads per retailer in one owned tab. Verification and uncertain delivery pause safely. Server-side unfinished runs can be recovered as paused after browser/session loss. Migration/API checks use an explicitly selected isolated test schema; do not run an application-database update as part of testing.

The installed-extension comparison journey passed against a temporary schema and a dedicated API port with a temporary extension copy, covering a Woolworths exact match, a separate ALDI alternative, desktop/mobile list editing, two-account isolation and unchanged application-table fingerprints. Its preflight authenticates a unique temporary-schema probe session through both the API and website proxy before browser mutations; it also checks that the comparison API exists. The standard API configuration defaults to Debug, with MYSHOPPINGLIST_API_CONFIGURATION available for isolated builds. MYSHOPPINGLIST_TEST_API_URL must match the test runner website's upstream. Never reuse another process on the test API port.
