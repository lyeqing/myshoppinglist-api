# Security testing

Review date: 1 November 2026. Tests run locally are distinct from deployed-host verification. Final results are recorded below after the validation run.

## Coverage

Existing API suites cover anonymous rejection, account/list/product isolation, expired/revoked sessions, cookie and native transport, CSRF, malformed inputs, login throttling, retailer SSRF and DNS rebinding, OAuth PKCE/consent/audience/scopes/revocation, MCP write ownership/idempotency/version conflicts, extension ownership/claim/approval/replay, operations privacy and incident deduplication.

New `RequestSecurityTests` exercises declared and unknown-length oversize bodies, exact boundaries, explicit capture limits, response security/privacy headers, anonymous throttling without trusting forwarded headers, independent account budgets, pre-authentication ingress rejection and health-probe exclusion. Operations tests cover the configurable security-rejection threshold, deduplication, conservative recovery, ingress visibility and bounded persistence during floods. Correlation tests verify safe 500 responses with reference IDs. Website `public-pages.spec.ts` adds fresh nonce/caller-header replacement, production security headers, successful hydrated analytics consent and a network-HTML injected script that the actual browser must block. DevTools evaluation is privileged and is not used as evidence that an XSS payload is blocked. Node API-client tests cover safe reference display, hostile reference text and malformed upstream problem documents.

## Commands

API:

```powershell
dotnet test --no-restore --nologo --logger 'trx;LogFileName=security-relational.trx'
dotnet list package --include-transitive --vulnerable --format json
python -m unittest discover -s tools/operations -p 'test_*.py'
```

Use an explicitly disposable PostgreSQL target for both `MYSHOPPINGLIST_TEST_CONNECTION` and `ConnectionStrings__MyShoppingList`, apply migrations only there and retain the profile documented in [release readiness](RELEASE_READINESS.md). Relational fixtures can commit rows. Missing PostgreSQL means skipped coverage, not passing ownership tests. The new hardening code introduces no schema changes.

Website:

```powershell
npm audit --omit=dev --audit-level=high
npm run test:regression
npm run lint
node node_modules/typescript/bin/tsc --noEmit
# Match the existing production-browser configuration's output directory and origin:
$env:MYSHOPPINGLIST_NEXT_DIST_DIR='.next-playwright'
$env:MYSHOPPINGLIST_PUBLIC_ORIGIN='http://127.0.0.1:3001'
npm run build
node node_modules/@playwright/test/cli.js test --config playwright.public-production.config.ts
```

Port 3000 belongs to the user. Browser configs own port 3001 and refuse to reuse an occupied server. The machine's global npm shim was broken; local validation uses the installed npm CLI entry point where needed. The .NET CLI uses an ignored workspace-local home to avoid writing outside the workspace. Compiler/browser/database process creation required sandbox escalation; these are local test processes, not deployment.

## CI

Neither repository had an existing workflow in the inspected checkout. Added read-only-permission GitHub Actions workflows under `.github/workflows/security.yml`: API restore/advisories/build/migrations/tests against a disposable PostgreSQL service plus Python operations tests; frontend lockfile install, production advisory gate, regressions, type check, lint, production build and Chromium desktop/mobile public-page/CSP tests. Credentials in the ephemeral CI database are fixture values, never deployment secrets. The workflows have not been executed on a remote GitHub runner. Live-retailer and opt-in browser groups remain separately gated.

## Initial hardening evidence

- Baseline: API 2682 passed, 383 skipped without relational/browser conditions; website/extension Node regressions 154 passed.
- Final website production build: passed on Next.js 16.4.3. TypeScript passed. Lint: zero errors, four pre-existing unused-variable warnings in unrelated tests. Final Node regression: 157 passed.
- Full npm audit after compatible patches: zero critical/high, 16 moderate findings in development lint tooling. Production-only audit: zero findings at any severity. The remaining minimatch advisory requires upstream-compatible resolution; forced historical ESLint downgrades were not applied.
- NuGet direct/transitive advisory query: no vulnerable package entries returned for either project.
- Python operations tests: 5 passed.
- Final production public-page/CSP browser checks: 12 passed across desktop and mobile. An additional 132 existing desktop/mobile account/session/gateway/shopping regressions passed after the framework/CSP update. Initial CSP test failures came from using privileged DevTools evaluation for the attempted injection; the corrected test injects actual network HTML and verifies enforcement.
- Existing backup/restore rehearsal: passed on the separate local PostgreSQL instance, with every table fingerprint equal after restore and account-export isolation/redaction checks passing. The application database was not used.
- Full relational API regression after the main hardening changes: 3065 passed, 5 skipped, zero failures (3070 total; 17.8 minutes). The remaining skipped groups are opt-in retailer/browser conditions, not reported as passes. A final focused run covers the subsequently added ingress/sampled-telemetry/error-reference/null-candidate changes and is recorded separately below.
- Final focused API integration regression on the final source: 67 passed, zero skipped/failed (2.2 minutes), including request security, correlation, operations, browser/native authentication, OAuth SDK flows, MCP read/write scopes and extension isolation/search validation. Reports: `tests/myshoppinglist-api.Tests/TestResults/security-relational.trx` and `security-final-targeted.trx` (ignored local artifacts).

Initial local API attempts encountered sandbox denial of the Windows Event Log and ASP.NET Data Protection key directory. The isolated test host now uses console logging; the relational run uses the required local process/key access. No application security control was disabled to obtain passing tests. Interrupted runs are not counted as passes.

After verification, the owned disposable PostgreSQL cluster and its plaintext temporary password were removed. Backup rehearsal artifacts remain in ignored `.local-backups`; test reports/logs remain ignored. Ports 3001 and 55432 were verified free; the user's port 3000 was untouched. No application database/schema or production infrastructure was changed by this work.

Production email/recovery delivery, external AI-client connectivity, production TLS/proxy/client-IP limits, actual database grants/firewall, deployed backups/restore and Cloudflare WAF are not verified here. See the audit and architecture documents for the exact remaining configuration. Do not describe the project as production-secure or fully complete until those are resolved.

## Account recovery and delivery follow-up

Implemented locally after the initial hardening commits: current-password change, asynchronous recovery, atomic session/OAuth/device revocation, encrypted leased SMTP outbox, truthful incident/recovery delivery ordering, production key protection, and independent SQLite/SMTP outage delivery. Default external sending/recovery remain off. The additive migration and exact settings are documented in [account security](ACCOUNT_SECURITY.md).

- Full API regression with the two new tables: **3088 passed, 5 skipped**, 3093 total, zero failures, 19.4 minutes. Report: `account-security-full.trx` in the ignored test result directory. The explicit schema allowlist was updated to include only the two reviewed entities; ownership/shadow-property assertions remain enabled.
- Final focused API regression after key-protection and notification-ordering changes: **54 passed**, zero skipped/failed, 2.3 minutes. Includes recovery, concurrent/expired/tampered proofs, whole-account revocation, typed validation, SMTP refusal to downgrade, blind recipients, retries/claims/expiry, production key persistence/certificate encryption, OAuth consent revocation fence, migration preservation and rollback refusal, operations, and model checks. Report: `account-security-final.trx`.
- Real production-build browser journey: **2 passed**, desktop and mobile, using a real API, isolated PostgreSQL schemas and local SMTP receiver. Verified the complete email-link reset/password-change journey, old-password rejection, old-session revocation and list preservation. The journey caught and fixed same-tab fragment navigation. Reset proof never appears in HTTP URLs or browser persistent storage.
- Focused production UI checks: **8 passed**, desktop/mobile disabled service, availability retry, invalid-link feedback, password confirmation, account-switch draft clearing and stale-account refresh. Earlier broader account/session/shopping regression: **138 passed**.
- Final public/private page, CSP and metadata checks: **12 passed**, including noindex on both new account routes. Node extension/API-client regressions: **157 passed**. Production builds and TypeScript pass; lint has zero errors and four existing unused-variable warnings in unrelated tests.
- Python operations checks: **11 passed**, including an actual loopback SMTP notification for database readiness failure without PostgreSQL delivery dependency, durable retries, suppression and explicit simulation/live-mode separation. No external email was sent.
- Repeated backup/restore rehearsal with the new tables: **passed**, all table fingerprints equal, export ownership/redaction preserved. MailKit 4.18.2 added; the refreshed NuGet advisory query returns no vulnerable package entries. Final full npm advisory query has zero critical/high and 16 moderate development lint-tool findings; production dependencies are unchanged from the clean production audit.

The migration generator could not traverse an unrelated private backup directory. Generation used a temporary copy containing only source and public configuration; private settings/backups were excluded and the copy was removed. No backup permissions were changed. Tooling interruptions and initial fixture/selector failures are not counted as passes. No security control was disabled to obtain a green result.

The application database was not migrated and no production activation occurred. Deployment still needs the reviewed migration, runtime grants, SMTP/sender configuration, protected persistent keys, actual origins/proxies and independently placed scheduling. Registration email verification remains unimplemented and is not represented as verified account ownership.

## Customer-initiated AI research follow-up — 14 November 2026

Research adds private immutable evidence, separate read/write scopes, bounded partial submissions, explicit selection and revisioned issue outcomes using the existing OAuth host, account services and atomic mutation receipts. AI reports cannot overwrite verified prices/check dates, enrich existing product metadata, or participate in trusted basket totals. The additive migration enforces account/job/product boundaries and refuses populated rollback. The legacy converter explicitly validates/fingerprints these references; unknown reference tables still fail closed.

Full API acceptance: **3,136 passed, 5 skipped, zero failures**, 3,141 total, 22m42s (`research-acceptance.trx`). The full run compiled before the last serialization-only change. Final research/OAuth follow-up covers that change: **28 passed**, zero failures/skips, including official MCP SDK submission/review/selection and 64-bit identifier round trips. Focused migration/legacy/research checks: **44 passed**. Real gateway/API/PostgreSQL research browser journeys: **4 passed**, desktop/mobile, including account-context fences, logout clearing and normal-list quantity preservation. Existing frontend regressions: **94 passed**. Node/extension: **173 passed, 1 skipped**, no failures. Python operations: **11 passed**. Final production build, TypeScript and lint pass with zero lint warnings/errors. Synthetic desktop/mobile screenshots were inspected.

Only an owned disposable loopback PostgreSQL cluster was migrated. No production/application data, credentials or server on port 3000 was changed. External ChatGPT/Claude connectivity and live retailer access remain untested. Evidence remains unverified even after customer selection or issue acknowledgement. Full phase results, failed-run corrections and remaining deployment/retention limits are recorded in [the migration report](ai-mcp-migration-plan.md#implementation-verification).

## Social sign-in follow-up — 15 November 2026

Google, Facebook and Microsoft use the official ASP.NET Core handlers and existing account/session services. Tests cover stable provider subjects, missing/changed provider email, explicit linking without email-based merging, fresh identity proof, last-method protection, trial conversion and explicit existing-account switching, session/account changes, cancellation, replay/expiry, protected email/password enrollment and delegated-access revocation. Passwordless accounts reject password login, including the dummy timing-hash fallback. Migration checks cover preservation, session ownership, immutable identities, concurrent credential deletion and populated rollback refusal.

- API evidence across the broad run and fresh follow-ups: **3,163 distinct passing test cases, 5 intentional skips**, with successful evidence for all **660 discovered test methods**. This is combined coverage, not a clean single full-suite run. The broad process stalled after reporting 3,129 passes, one historical migration-fixture failure and five skips; the cause of the test-host stall was not established. The fixture was corrected to insert only columns present at its historical migration stage. A fresh completion run passed **53 cases**, with one opt-in browser group skipped there and subsequently exercised separately. No failed attempt is counted as a pass.
- Final focused protocol/migration checks: **25 passed**. Final social/password regression: **27 passed**, including the passwordless dummy-hash rejection. Reports are `social-final-protocol.trx` and `social-password-final.trx` in the ignored test-result directory. The combined coverage reconciliation is in ignored `.local-dotnet/social-coverage-evidence.json`.
- Final production-build browser journeys: **6 passed**, Google/Facebook/Microsoft on desktop and mobile. These cross the actual website, gateway, API, cookies and disposable PostgreSQL database. Desktop registers new users; mobile converts active trials while preserving internal account IDs. External provider navigation and token/profile responses are controlled fixtures, so these results do not certify real provider consent or sign-in.
- Final social/account-security UI checks: **26 passed**. The broader account/session/shopping/UI run initially passed 105 of 108 cases; selector corrections and an increased test-only journey timeout were followed by **8 passing focused reruns** covering every failure on desktop and mobile. Cancellation, explicit expired-session reset, account switching and same-tab proof-fragment handling have dedicated coverage.
- Node/extension regressions: **173 passed, 1 skipped**. Final backend and frontend production builds pass; TypeScript and lint pass with zero errors/warnings. NuGet direct/transitive advisory scanning reports no vulnerable packages. Desktop/mobile screenshots were inspected.

Only the disposable loopback PostgreSQL instance was migrated. The application database was not migrated, providers remain disabled, and no real provider credentials or external email were used. Existing servers on ports 3000/3001 were preserved; browser tests used explicitly approved port 3002. Actual provider accounts, publication/branding approval and deployed SMTP remain owner configuration and live-validation work. Apply the social migration before starting the updated API, even with providers disabled. See [social authentication setup](SOCIAL_AUTHENTICATION.md) for exact credentials, callbacks and activation steps.
