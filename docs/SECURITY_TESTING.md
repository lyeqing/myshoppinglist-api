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

## Results and remaining checks

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
