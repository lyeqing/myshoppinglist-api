# Release handoff and acceptance gates

31 October 2026. The owner requested completing remaining work and then confirmed that no Stripe sandbox/public domain is available yet. The existing private incident-recipient address was reconfirmed; this is a destination, not an email transport/provider. No credentials, legal identity, deployment destination or spending budget were inferred. Direct chat authorisation covers safe local completion; it does not replace the original exclusions on production activation, spending, mass scraping or cross-user execution.

## Current release scope

The local product includes account-scoped OAuth/MCP, selected current lists, retry-safe list writes, an own-device approval/review extension workflow, private feedback/operations, consent-based analytics, public discovery pages and test-only billing records/hosted adapter. Existing trial/free/manual paid limits are retained. New integration switches default off. Operations delivery is simulated. Billing rejects enabled production configuration and cannot grant paid access. Community sharing and cross-user execution have no new implementation.

This is a **local/test release**, not an accepted commercial deployment. See [work plan](MYSHOPPINGLIST_CODEX_WORK_PLAN.md), [implementation audit](IMPLEMENTATION_AUDIT.md), [billing limits](BILLING_TEST_GROUNDWORK.md), [operations runbook](OPERATIONS.md), [launch draft](MARKETING_AND_LAUNCH.md) and [community gate](COMMUNITY_DATA_DECISION.md).

## Final local verification

| Check | Observed result |
| --- | --- |
| Complete API relational run, disposable fresh database | 3,041 passed, 18 failed, 5 skipped; 3,064 total, 21m34s. The runner mistakenly forced optional features off, masking the relevant fixtures. This was not a green run. |
| Corrected feature-profile rerun | 30 passed, zero failures/skips, 2m11s. Compared test names against the full-run report: **all 18 failed cases** have matching passing rerun results; none remains uncovered. Includes the final timeout fix/new case. |
| Billing timeout/cancellation group | 13 passed, 32s. Provider timeout now records a durable retry/backoff; real cancellation propagates and rolls back its attempt; recovery completes once. |
| Rebuilt available API suite | 2,682 passed, 383 explicit skips, zero failures; 3,065 total, 11s. This separate run does not count database skips as passes. |
| Controlled Chromium opt-in groups | 4 fixture cases passed, 8s, with network guard blocking external connections. These cover the two controlled-browser groups skipped in the full API run. The three real-retailer audit groups remain unenabled. |
| Complete development browser run | 466 passed, 7 failed, 3 skipped; 476 total, 25.1m. Four failures used obsolete ALDI wording; two sampled the explicit refresh before it completed; one session-restoration timeout occurred under the development server. |
| Complete production-build browser run | **473 passed, zero failures/flaky results, 3 intentional skips; 476 total, 9.7m**, desktop and mobile. Uses repaired fixtures and covers all previously failed workflows. Skips are the mobile native-extension case and two performance cases reserved for the dedicated measurement config. |
| Build/package/static checks | API Release publish and Debug package inspection passed; private local settings absent. TypeScript, changed-file lint and both whitespace checks pass; two pre-existing unused-variable warnings remain in the refresh test. |

The full API run loaded the build before the new timeout case; the corrected feature and rebuilt available runs cover the final source. The development timeout did not recur in the complete production run; the development run is not retroactively reported as passing. No product assertion was removed to obtain a green result.

API reports are in ignored `tests/myshoppinglist-api.Tests/TestResults/`: `release-final-relational.trx`, `release-feature-profile.trx`, `release-final-billing-timeout.trx`, `release-final-available.trx`, `release-controlled-browser.trx`. The frontend JSON report is ignored `test-results/release-production.json`; production failure output is separated beneath `test-results/production` to preserve development-run evidence.

After verification: zero generated database schemas remained; the owned fresh test cluster was stopped; its plaintext password and copied private settings in temporary build outputs were removed. API packages remain local/undeployed. Ports 3001/5392/55432 are free and port 3000 was untouched. The owner's original private settings remain intact.

## What can and cannot be closed now

| Item | Local deliverable | Remaining external dependency |
| --- | --- | --- |
| Database changes | Additive migrations; preservation/rollback tests and fixture backup/restore | Approved target, privileges, backup/restore rehearsal and separately approved production migration |
| Application regression | Focused relational tests, real API/OAuth/extension/browser journeys; full final regression results recorded in the work plan | Deployed HTTPS smoke tests on the actual host, proxy and domain |
| Stripe | Official test adapter, signed durable webhooks, reconciliation, private views, fixture failure tests | Owner-controlled sandbox/account, securely configured test keys/prices, real hosted Checkout/portal/webhook tests |
| Commercial access | Existing three-hour trial/two free/five manual paid-or-complimentary lists | Prices, tax/accounting, grace/refund/downgrade/paid-through policy and implementation; separate live-payment approval |
| Public site | Public pages, truthful copy, metadata/sitemap/robots and local production-build checks | Domain ownership, legal operator/privacy contact, final HTTPS origins, hosting/deployment approval |
| Email/monitoring | Durable simulated alert outbox, independent uptime probe, recipient configured privately | Delivery/monitoring provider, credentials, approved budget, external host/schedule and explicit live activation |
| Extension distribution | Installed-extension fixture journey, own-account consent, bounded capture/search and cancellation | Retailer access/reuse permission assessment and Chrome listing/disclosure/review; no permission inferred from public pages |
| Community | Options, conditional contracts, consent/withdrawal prototypes, policy evidence and test requirements | Empirical value, workflow-specific permissions/review and explicit new approval; cross-user execution remains prohibited |

No generic “finish everything” instruction supplies missing credentials, creates a payment account, establishes retailer rights or approves a public legal identity. Local tests must never be labelled as remote provider acceptance or store approval. Do not tick full MSL-503/504/602 acceptance until the corresponding evidence exists.

## Next setup sequence

1. Create/select an owner-controlled Stripe sandbox when billing is needed. Store secrets in the private local configuration or an approved secret store, not chat, source control, browser code or screenshots. Configure the existing `Billing` test options and approved sandbox price IDs; no monthly/annual amount is supplied by this release.
2. Run real sandbox Checkout success/cancel, declined/required-action payment, subscription renewal/failure/recovery, duplicate/delayed webhooks, portal cancellation before period end and partial/full refund tests. Confirm current-state reconciliation during provider outages. Preserve evidence of both provider and local state. The local test adapter/fake HTTP tests do not replace this step. Use sandbox/test clocks only; do not enable live credentials.
3. Decide commercial entitlement rules separately from provider subscription status. Implement and verify them before proposing live activation. Refunds, period ends, failed payments and complimentary access must not be guessed into a paid-through promise or revenue/churn report.
4. Select the actual domain, legal operator/privacy details, deployment host, narrow trusted proxy addresses, certificate/key storage, administrator IDs, exact OAuth callbacks/client IDs and extension IDs. Rebuild public metadata for the real origin. Configure HTTPS, secure cookies, actual client-IP/rate-limit behavior, least-privilege database access and persistent signing/encryption keys using the existing runbooks. Require personal mode and explicitly disable legacy `ProductImport`/`PriceRefresh` workers in the deployment configuration; the pre-existing legacy defaults are not deployment approval. Keep preview discovery disabled where appropriate.
5. Choose a delivery/monitoring provider and explicit budget. Keep the recipient in private configuration. Verify sandbox delivery, outage/recovery dedupe, retries and provider failure before separately approving live transport. Do not send customer data, exports or credentials to the incident destination as part of tests. Place the independent uptime probe outside the application/database failure domain when deployment exists.
6. Resolve retailer/store distribution conditions. No challenge bypass, proxy rotation, unattended collection, sharing or cross-user task routing is a substitute for permission. Complete the applicable privacy/store disclosures and owner review before distribution. Community B/C remains a separate closed gate.
7. With an approved deployment target, rehearse its real backup/restore, review migrations, run HTTPS deployment smoke checks, then request the final explicit activation decision for production changes. No target currently exists, so no deployed evidence is claimed.

## Reproduce verification safely

Use a disposable PostgreSQL database: existing fixture tests can commit records and advance sequences. Set **both** `MYSHOPPINGLIST_TEST_CONNECTION` and `ConnectionStrings__MyShoppingList` to that target, never to the application database. Apply migrations only there. Match the local test hostname/port when running `tools/operations/verify_backup_restore.ps1`; that script deliberately requires loopback 55432 and a `MyShoppingList…Test` database. Remove the temporary password and stop the owned cluster afterwards.

The final API regression profile uses personal captures on globally, `ProductImport__Enabled=true` and `PriceRefresh__Enabled=true` only so explicit legacy fixtures can test their queue routes; personal mode suppresses runtime legacy collection and fixtures drive/mask workers. The final run also directs remote HTTP to a closed loopback proxy while excluding loopback fixture addresses. These are **test-process overrides**, not application/deployment configuration changes.

Do not force `Operations__Enabled`, `AiIntegration__Enabled`, `ExtensionBridge__Enabled` or `Billing__Enabled` off in the full test process: those environment overrides mask the feature fixtures' startup settings. Checked-in defaults already keep the real features off. Remove such overrides from the disposable test process and let each fixture select its own configuration. An initial full-run command made that mistake; corrected feature results are recorded separately, not presented as if that failed run was green.

Local API publish inspection also verified `appsettings.Local.json` is excluded by project metadata. It remains available for local development but must never accompany a release package. A publish artifact is not deployed by generating or inspecting it.

From the API repository, after configuring the isolated test environment:

```powershell
dotnet test tests/myshoppinglist-api.Tests/myshoppinglist-api.Tests.csproj --no-restore --logger 'trx;LogFileName=release-final-relational.trx' -v quiet
python -m unittest discover -s tools/operations -p 'test_*.py'
```

From the frontend repository, use installed CLI entry points (the machine's global `npx` shim was broken). Reserve port 3000 for the owner; these configs own port 3001 and refuse reusing another server:

```powershell
node node_modules/@playwright/test/cli.js test --reporter=line
```

Real integration journeys have separate `playwright.*.config.ts` configurations and disposable database requirements. Production-build public checks use `playwright.public-production.config.ts`. Build and development tests share the ignored `.next-playwright` directory: stop the owned server before building or clearing its verified dev cache. Do not build while a dev server is using it. The work plan distinguishes full runs, targeted reruns, skips, fixture repairs and external checks that have not occurred.

For the complete regression against built release assets, build `.next-playwright` with the same test API/public-origin settings, stop any owned development server, then run:

```powershell
node node_modules/@playwright/test/cli.js test --config playwright.regression-production.config.ts
```

This reuses the ordinary customer/admin cases with a production server on 3001 and writes the JSON report above. It does not deploy the website or use an external API/provider.

## Local schema troubleshooting

Ordinary `/api/auth/me` and trial/session restoration query `UserCurrentLists`; disabling MCP does not make the latest API compatible with the old schema. `42P01: relation "UserCurrentLists" does not exist` can indicate pending migrations or a wrong connection/search path. Inspect the actual target and migration history first. Preserve a private backup, rehearse restoration/upgrade and verify existing data before applying the prepared migrations to an approved application target. Do not bypass ownership logic, catch the error as an empty list, reset the database or manually create just one missing table. The latest model has other dependencies too.

On 31 October 2026 the configured **local development** database `localhost:5432/MyShoppingList`, schema `public`, was confirmed to have only the 18 migrations through `20261026073219_AddLegacyProductMigrationMaps`. The API code was ahead of that local schema. The owner’s error report triggered the repair under the existing authorisation to complete local work; this was not a production migration.

A full custom-format database backup was written to an ignored, owner-access-restricted directory. The application role could migrate its own schema but could not create a second database, so its permissions were left unchanged and the restore rehearsal used a separate Codex-owned PostgreSQL instance on 55432. The restored copy upgraded successfully: all **29 pre-existing tables** retained identical row counts/data fingerprints, with **48 application tables** after upgrade.

The same six pending migrations were then applied to the verified local application database with bounded lock/statement timeouts. All **29 existing table fingerprints and row counts remained identical** there as well. Read-only checks confirmed `UserCurrentLists`, `UserCurrentListRevisions`, 24 applied migrations and latest `20261031061702_AddTestBillingLedger`; a session/current-list join resolved successfully. No new integration/payment/sending flag or workspace credential was changed.

Backup and before/after fingerprint manifests remain privately in `.local-backups/schema-repair-1f008b2cbb244589aabb71a51f7a71e1/`. Keep them restricted and apply the owner's backup retention policy; they contain actual local application data, not disposable fixtures. The rehearsal server was stopped and its temporary password removed. The original PostgreSQL service, application process and frontend port 3000 were not stopped or restarted. The running API did not answer a three-second readiness probe, so authenticated HTTP recovery was not claimed; if paused at the original debugger exception, resume/continue and retry the request.

Subsequent recovery confirmation: the owner reported that the page works and the cancellation/timeout errors stopped. Agent checks then observed `/health/ready` HTTP 200; a synthetic invalid session exercised the authentication lookup and returned the correct HTTP 401 in 258ms. Read-only diagnostics found no blocked database backends, the expected token index, and a synthetic authentication lookup executing in 0.037ms. All five cancellation-handling tests passed. No real session token/account data was read for the probe. No timeout increase, automatic retry, authentication bypass or exception-suppression change was made. The transient exceptions were consistent with interrupted/debug-paused requests; their original timing cause was not independently reproduced. Current local recovery is confirmed, not a guarantee that all future cancellations/timeouts are harmless.
