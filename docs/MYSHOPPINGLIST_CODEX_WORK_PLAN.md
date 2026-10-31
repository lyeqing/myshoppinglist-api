# MyShoppingList — Codex Implementation Work Plan

**Prepared:** 31 October 2026

**Status:** Safe local release work verified, including the complete production-build browser regression; Phase 5 remains test-only and Phase 6 design-only. Commercial billing acceptance and external launch prerequisites remain open. Operations delivery is simulated; new integration flags default OFF. See `docs/RELEASE_READINESS.md`, `docs/IMPLEMENTATION_AUDIT.md`, `docs/BILLING_TEST_GROUNDWORK.md`, `docs/COMMUNITY_DATA_DECISION.md` and section 8.

**Audience:** Codex and the project owner

**Primary goal:** Continue developing MyShoppingList safely and incrementally, combining an AI-accessible MCP interface, reliable browser-extension capture, an operational Super Admin dashboard, customer feedback, SEO and customer acquisition measurements.

## 0. Read this before changing code

### Current product direction and decisions

- Stack: Next.js App Router, TypeScript, Tailwind CSS v4; ASP.NET Core / C#, EF Core, PostgreSQL; Chrome-compatible browser extension. Confirm actual versions and source structure in the repository.
- Personal shopping lists are saved per user. The extension already supports user-initiated adding of products. **Inspect the current implementation; do not recreate working features.**
- Do not restart mass retailer scraping, daily price collection, global product catalogue harvesting or server-side browser automation. Do not assume automatic price comparisons across Coles, Woolworths and ALDI currently work.
- AI assistants **can use their own web-search capabilities**, but all access by AI to MyShoppingList data and operations must pass through **the authenticated MyShoppingList MCP server**. Do not give AI a direct backend, database or extension endpoint.
- Each customer's shopping lists and account data remain private. Sharing product observations, and especially running User A's tasks on User B's extension, are **separate optional product decisions**. Neither is authorised for implementation by this plan.
- Product/search data must have source URL (if applicable), capture timestamp, retailer, store/location, price conditions and data freshness. Never present stale/unverified information as a live price.
- Use emerald-600 as the existing intended primary Tailwind colour if compatible with current theme.
- Prefer existing architectural conventions, dependencies, migration style, test frameworks, authentication, telemetry and deployment pipeline. Avoid needless infrastructure (Kafka, continuous sockets, a new microservice) until demonstrated necessary.

### External references / prior specs

- [MyShoppingList project home](https://app.notion.com/p/3e7498f929a181058515dbfa78672b36)
- [AI Integration & MCP API](https://app.notion.com/p/409498f929a181cda454d0a4aad7becf)
- [Super Admin, Monitoring, SEO & Marketing](https://app.notion.com/p/409498f929a181d69e08ebb802e7f687)
- [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [Chrome Manifest V3 service worker lifecycle](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle)
- [Chrome Web Store developer policy](https://developer.chrome.com/docs/webstore/program-policies/policies)

**Important:** Some older project roadmap notes still discuss a canonical shared product catalogue and background retailer lookups. They predate the later personal-extension-only direction. The later decision takes precedence until the product owner explicitly changes it.

### Repo-level instructions for Codex

1. Read `AGENTS.md`, README, current architecture documents, environment templates, existing migrations, tests and CI. Locate frontend, backend, extension and deployment projects. Check `git status` and preserve user changes.
2. Produce `docs/IMPLEMENTATION_AUDIT.md` with what exists, file paths, gaps, potential conflicts with this plan, and commands/results from the current test suite.
3. Update this document's task checkboxes (and a short change log) as verified work is completed. **Never mark a task done based only on code being written.**
4. Work in small coherent changes/PRs. Preserve API compatibility, add migrations safely, add automated tests, run linters/build/tests, report results and known limitations.
5. Implement Phase 0 first, then Phase 1. Do not begin payment-provider activation, live email sending, production secrets, migration destructive operations or cross-user execution without approved configuration/decision.
6. Do not guess a folder path, API shape, ORM model or authentication mechanism; inspect the repository and adapt the designs below.
7. If an external service or missing credentials blocks progress, build behind interfaces with safe local mocks and document exactly what production setup is needed. Avoid fake success states.

---

## 1. System architecture

```text
ChatGPT / Claude / compatible AI clients
      | MCP tools + user-scoped OAuth
      v
ASP.NET Core MCP HTTP endpoint (authorised, auditable)
      | calls existing application services ONLY
      v
ASP.NET Core application/backend ---- PostgreSQL
      |                   ^               ^
      |                   |               |
      +-- authorised extension task queue |
      |                   |               |
      v                   |               |
User-owned Chrome extension --------------+
   - linked device, current status
   - explicitly approved capture on permitted retailer page
   - bounded, structured product result

Next.js frontend / public website ------> normal authenticated API
Next.js Super Admin ---------------------> admin-only API
Telemetry + analytics + alerting <------- frontend / API / extension
```

- The MCP API is an **integration adapter** over existing application services; it is not a second business-logic implementation.
- MCP tools do not receive arbitrary user IDs. User identity is resolved from the validated access token and authorised server-side.
- Frontend and extension continue using the normal application API; AI never bypasses MCP.
- Extension tasks must be expiring, acknowledged, permission-checked and idempotent. Never assume that a Chrome service worker stays running.
- Use a dedicated hosted/managed monitoring service for detailed logs/traces when practical; PostgreSQL stores business records, audit records and compact aggregates, not high-volume raw analytics logs.

---

## 2. Release sequence and tasks

### Phase 0 — Audit, secure foundation and acceptance baseline (P0)

**Deliverables:** `docs/IMPLEMENTATION_AUDIT.md`, feature inventory, diagrams, updated backlog, verified baseline test report.

- [x] **MSL-000** Inventory current list features, extension product capture, API endpoints, identity/session system, migrations, hosting, error logging, analytics, pricing page and trial entitlements.
- [x] **MSL-001** Record the actual directory structure and shared services to reuse; identify legacy shared-price/scraping code and whether it is live. Do not remove it without evidence/approval.
- [x] **MSL-002** Verify server-side authorisation across list endpoints; prove User A cannot read/write User B's lists by changing IDs. Add tests for this if absent.
- [x] **MSL-003** Ensure application-level request IDs, safe structured errors and existing builds/tests run in local environment; record problems and environment variables (names only, no values).
- [x] **MSL-004** Establish a minimal, reliable smoke test for: landing-page response, register/login, create list, add a product (or generic item), retrieve list, extension capture if testable.

**Acceptance:** Audit includes source paths, concrete evidence and pass/fail test commands. Existing UX remains intact. Any ambiguity is documented rather than guessed.

### Phase 1 — AI/MCP read-only MVP (P0/P1; implement after Phase 0)

**Goal:** A customer can connect a supported AI client and read *only their own* shopping-list data through an MCP endpoint.

- [x] **MSL-100** Integrate official `ModelContextProtocol.AspNetCore` in an appropriate existing ASP.NET Core host. Verify SDK/package and transport compatibility against the version actually installed.
- [x] **MSL-101** Implement standards-compliant remote MCP authentication/authorisation (OAuth where required), with correct resource/audience validation, scoped tokens and per-client/user revocation. Prefer a trusted existing authorisation provider/library over hand-rolled OAuth. **Do not publish unauthenticated MCP tools.**
- [x] **MSL-102** Define scopes: `lists.read`, `lists.write`, `products.read`, `extension.capture`. Start read-only; require explicit separate grants for later tools.
- [x] **MSL-103** Add MCP tools over existing services: `get_my_lists`, `get_current_list`, `get_list_items`, `search_my_products`. Clearly define how `current/default list` is chosen; do not silently guess.
- [x] **MSL-104** Standardise tool result contracts: stable IDs, version, unit/quantity, nullable product price, source, checkedAt, store/location, machine-readable not-found/permission errors; do not disclose account-internal fields unnecessarily.
- [x] **MSL-105** Implement tool-level audit counters and safe request logging; redact tokens, personal list contents and private URLs.
- [x] **MSL-106** Integration tests: User A cannot read User B; expired/revoked token rejected; missing scope rejected; multiple lists unambiguous; missing retailer price explicitly unknown; test with a real compatible MCP client when available.

**Acceptance:** Read-only tools work with a compatible MCP client using the user's individual account. Unauthenticated and cross-user access return permission errors. No writes from read-only tools.

### Phase 2 — MCP write operations + controlled extension bridge (P1)

**Goal:** AI modifies the user's own shopping list, and can ask the backend to request an approved capture using that user's own available extension.

- [x] **MSL-200** Add `create_list`, `set_current_list`, `add_product_to_list` (including generic products), `update_list_item`, `remove_list_item` with `lists.write` scope, input validation and ownership enforcement.
- [x] **MSL-201** Add optimistic concurrency/version checks and idempotency keys for retried writes. Return conflict details rather than overwriting concurrent edits. Require safeguards for destructive/bulk actions.
- [x] **MSL-202** Implement **Connected AI Apps** page: connected clients, scopes, last-used time, per-client revoke, explanatory privacy text; revocation must stop access immediately.
- [x] **MSL-210** Add per-user extension-device registration (least privilege, device identifier, linkedAt, lastSeenAt, version, supported retailers, revokedAt). Protect device credentials; no other user's device IDs exposed.
- [x] **MSL-211** Implement `get_extension_status` with honest states: `not_linked`, `recently_seen_unconfirmed`, `ready_for_request`, `needs_permission`, `awaiting_user_approval`, `offline`, `unsupported_site`. A recent heartbeat alone cannot guarantee execution.
- [x] **MSL-212** Add `ExtensionTask` entity and task API: `taskId`, ownerUserId, deviceId, `type` (capture/search within approved scope), supported URL/domain, status, created/expiry, acknowledgement, attempts, errorCode, result ID, idempotency key. States: `queued`, `awaiting_user`, `acknowledged`, `running`, `completed`, `failed`, `expired`, `cancelled`.
- [x] **MSL-213** Add MCP `request_extension_capture`, limited `request_extension_search` and `get_extension_task_status`. Return actionable instructions if not linked/offline/permission missing. Never claim server can switch on an extension.
- [x] **MSL-214** Extension pulls/acknowledges authorised tasks only while active; show review UI; use Chrome-granted permissions and approved domains; capture only needed public product facts; return a bounded validated result. Support dropped connectivity, worker suspension, task expiry and retries.
- [x] **MSL-215** Enforce SSRF protection, HTTPS and exact allowed hosts for external URLs; reject local/internal IPs, arbitrary javascript/remote-code instructions, pages requiring CAPTCHA bypass, or unexpected origins. Do not execute arbitrary AI-provided JavaScript.
- [x] **MSL-216** Tests for offline extension, expired task, denied permission, user cancellation, duplicate job pickup, worker restart, product-price freshness, wrong-user device access, and list state consistency in web + AI.

**Acceptance:** AI can request changes through MCP; an authorised user sees changes in MyShoppingList. An available extension can complete a user-approved supported-page capture; offline or blocked conditions yield truthful errors and suggestions rather than false success.

### Phase 3 — Super Admin, support and observability (P1)

**Goal:** Know whether MyShoppingList is functioning, whose customer journey failed, what feedback users submitted and what needs action.

- [x] **MSL-300** Secure `/superadmin` frontend and backend routes with explicit administrator authorization and audit trail; no user-facing MCP Super Admin tools.
- [x] **MSL-301** Overview metrics: unique-visitor estimates vs sessions vs page views, registered/active users, trial/free/paid counts, first successful list/extension product save, MCP calls/connection errors, API health.
- [x] **MSL-302** Instrument privacy-limited events across website, backend, extension, MCP: release, timestamp, component, error fingerprint, request/trace ID, affected user where appropriate, status, latency. Define sampling, retention and consent rules.
- [x] **MSL-303** User management: search/filter, account details, state, last activity, subscription/entitlement, audit history, suspend/reactivate with confirmation and proper authorization.
- [x] **MSL-304** Customer feedback form (web + extension): bug/suggestion/other, message, optional rating and screenshot with permission, coarse technical context. Admin inbox: new, triaged, planned, resolved, closed, respond where supported, issue linking.
- [x] **MSL-305** Customer journey view: landing visit -> signup -> extension linked -> first successful capture -> first list use; handle privacy/consent and avoid invasive tracking.
- [x] **MSL-306** Technical error view: frontend, API, MCP, DB/jobs, extension, payment; grouping, filtering, error trends and links to monitoring provider.
- [x] **MSL-307** Critical incident alerts: P0 immediate; P1 threshold confirmed; P2 dashboard/digest. Deduplicate incidents, rate-limit sends, send recovery notice, store delivery outcome. Ensure notifications go to configured recipients through durable queue/provider.
- [x] **MSL-308** External uptime check independent from the application; alert even if backend/database are down. Provide test/simulation modes instead of emailing production recipients during tests.
- [x] **MSL-309** Admin analytics for extension devices, task queue depth, task success rate, MCP operations by client, failures, latency and per-user correlation.
- [x] **MSL-310** Automated journey tests: landing page, signup, list lifecycle, feedback, MCP access, extension job, payment test if implemented. Verify backup restoration plan and data deletion/export paths.

**Acceptance:** Problems are visible with actionable context and permission-safe drilldowns. P0 alert tests prove email send and recovery path; notification suppression avoids alert storms. No sensitive contents in logs.

### Phase 4 — Landing page, SEO, AI-search visibility and marketing analytics (P1/P2)

- [x] **MSL-400** Audit real landing page: mobile usability, headline/value proposition, actual feature claims, screenshots, CTA path, installation flow, broken links, page speed, pricing clarity and privacy.
- [x] **MSL-401** Render public marketing content in initial Next.js HTML; add unique metadata, canonical URLs, Open Graph, XML sitemap, robots, internal links and relevant SoftwareApplication structured data; keep private/account/admin URLs `noindex` and securely authenticated.
- [x] **MSL-402** Add feature/extension/FAQ/how-it-works/pricing/support pages describing only functioning capabilities. Support Google/Bing search crawler access and chosen AI search crawlers subject to desired policy/Cloudflare settings.
- [x] **MSL-403** Track CTA clicks, signup starts/completions, extension connect, first product save, first list creation, 7/30-day activity and payment conversion; attribute via privacy-conscious UTM/referrer tracking.
- [x] **MSL-404** Marketing dashboard for acquisition -> activation -> retention -> revenue. Add search-console operational checklist; no fabricated SEO claims or rank promises.
- [x] **MSL-405** Produce Chrome Web Store copy/screenshots recommendations and a lightweight launch-experiment checklist; do not launch paid ads without approval or proof of retention.

**Acceptance:** Public pages have meaningful server HTML, valid metadata/canonical/sitemap and truthful copy, private pages cannot be accessed without authorization, and the key funnel is measurable end to end.

### Phase 5 — Subscription/payments (P2, requires decisions and credentials)

Owner-approved scope amendment: **keep existing limits; build test-only billing groundwork**. No commercial prices/credentials supplied. Test ledger/SDK/UI and local failure matrix are verified; actual sandbox-provider acceptance and commercial entitlement/accounting policy remain deferred, not silently invented. Full MSL-503/504 checkboxes remain open for those prerequisites.

- [x] **MSL-500** Confirm trial/free/paid entitlements and limits; keep account status, subscription status and entitlement separate.
- [x] **MSL-501** Design Stripe integration (or approved equivalent) with hosted Checkout, Billing and customer portal; do not store raw payment card data.
- [x] **MSL-502** Persist customer/subscription/event IDs, verify webhook signatures, dedupe events, handle out-of-order updates and reconcile payment records; prevent multiple entitlements from retries. (Approved local test-only scope; actual sandbox acceptance remains external.)
- [ ] **MSL-503** Build billing UI/admin reporting: MRR, paid-through, renewals, cancellations, failures, refunds, receipts and churn. Use test mode until owner approves live activation.
- [ ] **MSL-504** Test failed payments, duplicate webhooks, partial outages, cancellation before paid-through date, refunds, trial expiry, downgrade and admin complimentary access.

**Acceptance:** No paid entitlement is granted solely from a checkout success redirect. Payment state survives duplicate, delayed and reordered webhooks. Production activation is separately approved.

### Phase 6 — Optional community-contributed data: DESIGN GATE ONLY (P3)

**Do NOT build this phase automatically.** First deliver a design/feasibility report, consent UX prototypes, policy review and empirical evidence that this is valuable.

- [x] **MSL-600** Compare: (A) personal only, (B) opt-in sharing of previously captured public product observations, (C) cross-user remotely requested lookups. Consider benefits, legality, Chrome policies, retailer access conditions, user trust and operating costs.
- [x] **MSL-601** If B is later authorised, specify `ProductObservation` and `SharingConsent`: source, store/channel/location, capturedAt, public price type, price conditions, expiry, contributor user ID stored privately; only explicitly contributed public product facts become shareable. No customer identity, account session, personal offers, shopping lists, browser history or cookies. (Conditional design only, no entities implemented.)
- [ ] **MSL-602** Require explicit opt-in that defaults OFF, clear privacy-policy update, ability to revoke/delete applicable contributions, permission and audit tests, provenance and freshness checks; validate compatibility with Chrome Web Store policies and retailer terms. (Requirements/prototype and scoped policy evidence documented; compatibility/permissions and empirical value are unresolved. No feature implemented.)
- [x] **MSL-603** **Cross-user execution (option C) is not approved.** If revisited, require a separate security/abuse impact assessment and user-consent design. Never silently assign stranger-requested search jobs to another customer's browser; users must be able to opt out without service degradation.

**Acceptance for Phase 6 planning:** A written decision record with options, risk assessment, constraints, rollout conditions and explicit owner approval. No background feature flag should secretly enable cross-user data collection or extension execution.

Design deliverable: `docs/COMMUNITY_DATA_DECISION.md`. Optional rollout approval has **not** been granted; the gate remains closed. Do not mark community-policy compatibility or implementation accepted from completion of this report.

---

## 3. Suggested entities and API boundaries (not mandated migrations)

**Reuse existing schema names first.** Add only what isn't present:

- `ShoppingList`, `ShoppingListItem`, `UserSavedProduct` (or current equivalents): owned by user; list version for concurrent writes.
- `AiConnection`: user, OAuth client, granted scopes, connected/revoked timestamps, last-used; no raw tokens in admin UI.
- `ExtensionDevice`: user, pseudonymous device ID, version, supported retailer capabilities, linked/last seen/revoked; not publicly discoverable.
- `ExtensionTask`: owner user ID, assigned own device ID, task type, allowed URL/host, state, created/expires/acknowledged, idempotency key, failure category, output reference.
- `ProductCapture`: user, retailer, URL (safe), product ID/description, verified source, store/channel/price qualifier, timestamp; no cross-user read by default.
- `AnalyticsEvent` or external event sink; compact `DailyAnalyticsSummary` for admin.
- `FeedbackTicket`, `AdminAuditLog`, `Incident`, `AlertRule`, `AlertDelivery`.
- `BillingCustomer`, `Subscription`, `PaymentWebhookEvent`, `Entitlement` only after Phase 5 approval.
- `ProductObservation`, `SharingConsent` **only** if the optional Phase 6 is approved.

Security invariants:

1. Every user-owned entity is always filtered by authenticated identity at the service/repository boundary, including MCP, REST, background jobs and admin views.
2. Every write and extension task is auditable and scoped; all external contents treated as untrusted data (prompt injection cannot grant capabilities).
3. An AI client cannot directly control Chrome or send arbitrary commands. It requests a typed task through MCP; backend verifies and extension requires appropriate user permission.
4. `available` means extension has acknowledged the task (or a fresh, accurate connection signal), not merely that it was once installed.
5. Site restrictions must be respected. Never bypass CAPTCHA, logins or anti-bot protections, and do not use another person's browser as a covert proxy.

---

## 4. Operational definitions and metrics

| Metric | Definition | Why |
|---|---|---|
| Unique visitor estimate | Deduplicated privacy-respecting visitor identifier per window | Do not confuse with sessions or pageviews |
| Activation | First successful shopping-list item save after signup | Measures actual core value |
| MCP active user | Distinct authenticated account making successful MCP calls per window | Adoption |
| Extension availability | Known linked device + recently acknowledged request; separate offline/unknown states | Truthful AI instructions |
| Extension capture success | Successful approved captures / attempted supported captures | Reliability, segment by retailer/version |
| Task wait time | Queue created -> acknowledged; completed separately | UX performance |
| Error rate | Failed relevant requests / valid relevant requests in window, with minimum volume | Alerts |
| Subscriber MRR | Monthly paid amounts + annual paid amounts/12, appropriate currency/refund handling | Business health |
| Source freshness | Captured timestamp and explicit retailer/store context | Prevent false live price claims |

**Initial configurable alerts:** P0 external uptime unavailable/DB down/widespread checkout errors -> immediate; P1 API 5xx >5% with sensible sample floor for 5m, extension capture failure >20% across multiple users for 15m, persistent webhook backlog; P2 isolated errors -> dashboard/digest. Use cooldown, dedupe, owner-configured email address(es), and recovery notices.

---

## 5. Working agreements and verification gate

After **each** phase, Codex must report:

- What changed (files, migrations, feature flags, endpoints, screens).
- Which tasks are completed and exact acceptance-test evidence.
- Local commands executed and results; skipped tests with reasons.
- New dependencies and configuration keys with example placeholder values only.
- Security/privacy review, data access boundaries and rollback notes.
- Open decisions requiring product-owner approval.
- The smallest safe next phase/PR to continue.

### Minimum cross-component test cases

- **Two-user isolation:** A and B have lists; A's MCP cannot read/update B's list even by guessing IDs.
- **Token/scopes:** missing auth, wrong audience, read-only grant performing write, revoked session all rejected.
- **AI list edits:** add/update/remove show consistently on frontend and extension; retries do not duplicate.
- **Extension offline:** not linked/closed/idle cannot be mistaken for success; AI gets clear instruction.
- **Capture denied:** user declines or retailer blocks; status safely failed with no unrelated data capture.
- **Price validity:** no historical or wrong-store price advertised as current verified price.
- **Failure notification:** simulated P0 triggers exactly one actionable email and a recovery event without sending from local tests to real people.
- **Website privacy:** superadmin and personal pages blocked for unauthorised users; public pages indexable with correct SEO metadata.
- **Feedback:** ticket can be submitted, viewed by admin, triaged, and user-visible private data is protected.
- **Community OFF:** no contribution or cross-user execution occurs absent explicit approved opt-in and policy work.

---

## 6. Start-here instructions for Codex

Copy the text below into Codex (or use the separate `CODEX_START_HERE.md` file):

> Read `docs/MYSHOPPINGLIST_CODEX_WORK_PLAN.md` fully. Start with **Phase 0**. Inspect repository instructions (`AGENTS.md`), existing frontend/backend/extension code, migrations, current tests and configuration before changing any code. Create `docs/IMPLEMENTATION_AUDIT.md` recording what exists, test evidence, key security gaps and what can be reused. Then implement the smallest safe unblocked slice of **Phase 1**: secure, user-scoped read-only MCP access using existing services and the official ASP.NET Core MCP SDK. Do not create unauthenticated MCP endpoints, global product scraping, cross-user extension execution, paid provider activation or destructive migrations. Add tests and run them. Summarise diffs, acceptance criteria met, limitations, and the next implementation task. Continue through subsequent approved phases only when each phase passes verification.

## 7. Owner decisions still required before later phases

- Final free / trial / paid limits and payment provider/tax configuration.
- Which customers/plans get MCP, and quotas/rate limits.
- **Decided:** one explicitly selected current list per account; viewing other lists does not silently change it.
- **Decided:** the customer-owned extension may open supported product URLs after customer approval; cross-user execution remains prohibited.
- Email provider and incident-recipient addresses; analytics/monitoring provider and budgets.
- Consent and retention policy for visitor analytics and extension diagnostics.
- Community observation-sharing / cross-user execution decision, including legal review. **No consent inferred.**

## 8. Verified progress and change log

### 31 October 2026 — Phase 0 investigation; acceptance gate still open

- Read the supplied start/work-plan documents, repository instructions, API/frontend architecture, auth, list/private-product services, extension source, migrations, test fixtures and local run configuration. Both repositories were initially clean.
- Added `docs/IMPLEMENTATION_AUDIT.md` with source paths, architecture, reuse decisions, legacy collection guards, security gaps, commands/results and outstanding decisions. Copied this supplied work plan into the previously missing repository path.
- Added `tests/myshoppinglist-api.Tests/ReadOnlyListBaselineTests.cs`: two users, archived/expired exclusions, repeat reads and empty-account behaviour using disposable isolated schemas, database-enforced read-only transactions and before/after fingerprints. These tests compile but are skipped until a dedicated test connection is configured.
- API baseline before changes: 2,637 passed, 2 failed, 340 skipped (2,979 total). After adding tests: 2,637 passed, 2 failed, 342 skipped (2,981 total). Existing failures are the stale entity inventory and legacy worker test inheriting personal mode (expects 400; receives 410). They were investigated and remain unchanged because repairs to those files were not in the approved plan.
- Extension fixture regressions: 144 passed. Frontend lint passed with four existing warnings. Frontend webpack production build passed outside the sandbox after permission failures in sandboxed build attempts. Six focused desktop browser smoke tests passed using a mock API. The frontend has no tracked changes.
- `MYSHOPPINGLIST_TEST_CONNECTION` is absent. Real PostgreSQL isolation/no-write, registration/list persistence and installed-extension journey verification remain blocked. Mock browser tests do not satisfy those criteria. Application request-ID/error-contract coverage also remains open.
- MSL-000/MSL-001 inventories are documented, with production hosting/deployed mode explicitly unverified. MSL-002/MSL-003/MSL-004 have remaining verification gaps. No Phase 0 task or phase acceptance is marked complete without its required evidence.
- Per the approved conditional plan, **Phase 1 was not started**. No MCP dependency, tool, route, OAuth provider, migration, runtime configuration or production activation was added.

### 31 October 2026 — Approved baseline test repairs verified

- Owner approved the two narrowly scoped repairs. `tests/myshoppinglist-api.Tests/DatabaseModelTests.cs` now includes the existing `LegacyProductMigrationMap` and `LegacyShopProductMigrationMap` entities in its exact inventory.
- `tests/myshoppinglist-api.Tests/ColesExtensionEndpointTests.cs` explicitly selects legacy mode in its test factory, keeping import workers disabled and retaining all authentication assertions. Production guards/configuration and separate personal-mode cutover tests are unchanged.
- Reran `dotnet test tests/myshoppinglist-api.Tests/myshoppinglist-api.Tests.csproj --no-restore --logger 'console;verbosity=quiet'` with the audit's process-only .NET environment setup: **2,639 passed, zero failed, 342 skipped, 2,981 total**, duration 8 seconds. Both original failures now pass.
- Dedicated test connection remains absent. Database isolation/no-write and real API/extension journey acceptance are still unverified. Phase 0 remains open; Phase 1 has not started. No runtime/dependency/migration/frontend changes were made.

### 31 October 2026 — Disposable database and installed-extension verification completed

- Owner approved an isolated PostgreSQL setup and verification. Reused installed PostgreSQL 18.6 and EF CLI 10.0.11, generated temporary SCRAM credentials and bound only `127.0.0.1:55432`. All 18 existing migrations applied to `MyShoppingListPhase0Test`. A separate scratch database within that same temporary instance isolated the browser journey and diagnostic runs.
- Full API suite with database tests enabled: **2,961 passed, 15 failed, 5 skipped; 2,981 total**, duration 15 minutes 19 seconds. Skips were explicit retailer/browser opt-ins. No live-retailer test flags were enabled.
- Both new no-write tests passed independently and in the full run. All 10 private-product isolation tests, 12 authentication endpoint tests, 3 personal-mode cutover tests and 15 list-service tests passed. Four list endpoint tests passed; the planning test has a test-side string-enum deserialization failure.
- Real website/API/installed-extension journey: **1 passed**, 1.3 minutes. Verified registration, reviewed/cancelled/saved captures, both retailer observations, desktop totals and saved-product pages, mobile quantity/note persistence, sign-out/account switching and 404 for the other account's list. Zero legacy collection tasks and unchanged public-table fingerprints were asserted. Retailer pages were controlled fixtures, not live-site access.
- Diagnostic reruns with explicit legacy test mode: 32 passed / 4 failed in the main selected group, plus 1/1 passed for the brand endpoint. All eleven configuration-sensitive cases passed. Remaining failures: string-enum deserialization in `ShoppingListEndpointTests.cs`; two ALDI `Restricted` vs `ExtensionOnly` assertions in `ShoppingComparisonWorkflowTests.cs`; and retained original-price provenance vs expected null in `PriceObservationContextTests.cs`. The latter requires contract review before changing the assertion. Full details are in the audit.
- Real temporary API smoke returned root 200 and generic unauthenticated 401 Problem Details with a framework `traceId`. No custom request-ID middleware was added. Operational log correlation remains unverified.
- Marked inventory/source map (MSL-000/MSL-001), demonstrated list ownership isolation (MSL-002), and the established real smoke baseline (MSL-004) complete based on these results. Production deployment details remain explicitly unverified. **MSL-003 and Phase 0 remain open because the full baseline has failures. Phase 1 has not started.**
- Teardown confirmed zero remaining generated test schemas in both databases, stopped the temporary cluster, released 55432, removed its plaintext password file and confirmed test ports 3001/5392 free. Stopped data/logs/TRX evidence remain under `%TEMP%/myshoppinglist-phase0-postgres/`. No runtime source, application configuration, dependencies, production schema or frontend source changed during this verification pass.

### 31 October 2026 — Approved test repairs and baseline verification completed

- Owner approved the seven-file test repair plan and fresh disposable database rerun, then separately approved explicit legacy-mode overrides in the list and workflow factories. No application services, endpoints, wire contracts, runtime configuration files, dependencies or frontend source changed.
- Import setup now fails on non-success responses. The import, refresh, pack and brand endpoint factories explicitly select legacy mode. Planning-response JSON decoding now matches string-enum serialization. ALDI's assertion matches the registry's `ExtensionOnly` contract while comparison remains unsupported.
- Reviewed and corrected the observation assertion: brand alternatives retain original descriptive provenance even when ineligible for savings; pack alternatives omit that baseline. Tests still require excluded totals and now explicitly require both unit-price and package-price savings to be absent when ineligible. The latest ineligible observation must remain the described source, rather than an older eligible price being substituted.
- API and test builds passed with zero warnings/errors. First affected group: 53 passed with personal mode enabled at process level. Five list endpoint tests passed. Both workflow tests passed with the intended legacy queue settings. All 60 tests in the seven affected classes therefore passed across targeted runs.
- Two intermediate workflow reruns failed because the verification command disabled `PriceRefresh:Enabled`, which also disables manual queuing. Corrected process-only profile: personal capture false, import enabled, refresh enabled. The workflow fixture removes every collection hosted service and drives transitions manually. These intermediate results are retained in the audit.
- Fresh cluster/evidence directory: `%TEMP%/myshoppinglist-phase0-postgres/rerun-20261031-tests/`; database `MyShoppingListPhase0RepairTest`, loopback port 55432, new SCRAM credentials, all 18 migrations applied. Earlier stopped evidence was preserved.
- Full API baseline: **2,976 passed, zero failed, 5 skipped; 2,981 total**, duration 14 minutes 46 seconds. The five skips remain explicit retailer/browser opt-ins. Full-run groups include private isolation 10/10, auth endpoints 12/12, personal-mode cutover 3/3, read-only baseline 2/2, list endpoints 5/5, workflows 2/2 and price observation context 23/23 passing.
- The additional two factory overrides were approved and edited while the full run's existing assembly was active. After it completed, rebuilt the final assembly and tested both fixture groups with personal mode enabled globally and import/refresh queue flags enabled: **7 passed, zero failed/skipped**, duration 38 seconds. This verifies the final source changes independently of the full run's explicit legacy process profile.
- Confirmed the remaining MSL-003 logging gap with a controlled malformed login request: response 400 includes a framework `traceId`, and the HTTP error is logged, but the response trace/trace component is absent from rendered console/file output. Existing templates omit trace/request identifiers. No new ID-generation middleware is necessary, and no logging configuration/source change has been made without approval.
- Teardown verified zero remaining generated schemas, stopped only this rerun's PostgreSQL cluster, removed its plaintext password file and confirmed ports 55432, 5392 and 3001 free. Stopped data/logs/TRX reports remain in the approved rerun directory. API/frontend/extension behaviour and production state were unchanged by these test-only repairs.

### 31 October 2026 — Logging correlation verified; Phase 0 complete

- Owner approved the logging templates, correlation/privacy tests and available-suite verification. Bootstrap console/file templates in `Program.cs` and configured console/file templates in `appsettings.json` now render existing `TraceId` and `RequestId` values. No new ID-generation middleware, dependency or HTTP contract was introduced.
- Added `RequestCorrelationTests.cs`: two real-pipeline malformed requests correlate to separate response traces and nonempty request IDs across all four templates and actual file output. Credential, cookie, query-token and private-note markers are absent from rendered output. A database connection interceptor verifies zero access attempts; workers are removed/disabled.
- Focused checks: **6 passed**, zero failed/skipped (correlation plus five cancellation cases). First run exposed a Windows file lock during test cleanup; the test now closes/flushed its owned logger before reading/deleting its log files.
- Available API suite: **2,640 passed, zero failed, 342 skipped; 2,982 total**, duration 9 seconds. Database/browser opt-ins were not enabled for this log-format-only verification. The earlier full relational baseline (2,976 passed, 5 opt-in skips) and real installed-extension journey remain the data/UX evidence; they were not relabelled as rerun.
- Independent real HTTP smoke on test-owned port 5392 returned a structured 400, matched its trace to actual console/file logs, verified nonempty request IDs and absence of all private markers. Collection was disabled and an unusable database connection configured. Test-owned API stopped afterward. Evidence is under `%TEMP%/myshoppinglist-correlation-verification/`.
- Marked MSL-003 and **Phase 0 complete** based on the combined audit, build/test, database isolation, real journey and logging evidence. Production hosting/configuration remains explicitly unverified. Existing development-mode exception-summary status classification was not changed by this template repair.

### 31 October 2026 — First approved Phase 1 slice verified (unexposed groundwork)

- Continued the original conditional implementation approval after Phase 0 passed. Pinned official `ModelContextProtocol.AspNetCore` **2.3.0** in the existing API project; restored successfully and verified the installed SDK's `net10.0` target and tool metadata APIs. No extra host or microservice was created.
- Added `Mcp/ShoppingListReadTools.cs` with only `get_my_lists`, reusing `ShoppingListService.ManageAsync`. Identity comes from the current HTTP principal through an injected accessor, never tool arguments. One authenticated identity, one valid positive account claim and exact case-sensitive `scope=lists.read` are required before database access. Ambiguous/missing identity and missing scope fail closed.
- Added `Contracts/McpReadContracts.cs`: version 1, string list IDs, names and original UTC creation/update dates. Internal account/entitlement fields are omitted. No default/current-list selection, writes, prices or product search were added. Existing registered-account eligibility is preserved, including rejection of trial/inactive accounts.
- SDK metadata declares read-only, non-destructive, idempotent, closed-world structured output and no client identity parameters. Stable safe error messages are `authentication_required`, `invalid_identity`, `insufficient_scope`, `account_unavailable`, `account_not_supported`.
- Added `McpShoppingListReadTests.cs`: SDK metadata, exact scopes, malformed/ambiguous identities, unauthenticated-extra-identity grant rejection, actual application `/mcp` GET/POST returning 404, two users, multiple active lists, archived/expired exclusion, empty-list non-creation, eligibility, read-only database transactions and minimal output fields.
- Database-focused group: **29 passed**, zero failures/skips (21 MCP cases plus existing read-only/correlation/cancellation checks), duration 21 seconds. Strengthened the two-user case with a second active list and reran that final case: **1 passed**, 5 seconds. Reused existing isolated fixtures; generated schemas stay separate from `public`.
- Final available API suite: **2,658 passed, zero failed, 345 skipped; 3,003 total**, duration 9 seconds. The three new database cases passed in the focused run and were explicitly skipped in this separate no-database suite. Earlier complete relational baseline and real extension journey remain separate evidence, not claimed as rerun after the SDK addition.
- Fresh disposable test instance under `%TEMP%/myshoppinglist-phase0-postgres/phase1-readonly-20261031/`, loopback 55432, database `MyShoppingListPhase1ReadTest`; tests generated their own isolated current-model schemas. No application migration or production database access. Teardown verified zero generated schemas, stopped the cluster, removed its plaintext password and confirmed 55432/5392/3001 free.
- **No MCP service registration, tool discovery or HTTP endpoint was added to `Program.cs`.** No AI client can connect yet. Token validation, audience/resource binding, OAuth discovery/grants, revocation, quotas and a real authenticated MCP client/transport test remain to be designed/implemented. Existing browser session tokens are not claimed to be delegated OAuth grants.
- MSL-100/102/103/104/106 have partial groundwork only; their whole-task checkboxes remain open. MSL-101 and MSL-105 are not implemented. **Phase 1 is not complete.**

### 31 October 2026 — Owner decisions and next Phase 1 increment in progress

- Owner authorised continuing planned tasks without repeated task approvals. Production activation, spending, mass scraping and cross-user extension execution remain excluded.
- Owner selected self-hosted OpenIddict with existing accounts; one explicitly selected current list per account; supported product URLs may be opened by the customer's own extension after customer approval.
- Added disabled-by-default OpenIddict configuration and authorization-code/PKCE groundwork, reference-token storage, per-request token/authorization validation, consent handlers and account-owned connection management. These are under active protocol testing and are **not yet a verified remote MCP release**.
- Added account current-list persistence, a composite ownership foreign key and version checks. Three focused relational/model tests passed: explicit selection/shared reads, stale-version and wrong-account protection, unavailable archived selection, and database rejection of cross-account rows.
- Generated `AddAccountCurrentListAndAiAuthorization` locally. No application database migration or production configuration change has been performed.
- OpenIddict protocol testing exposed required resource registration/permissions and explicit client ID on cached consent requests; implementing and verifying against the installed library rather than bypassing its validation.

**Next task:** Complete real OAuth/SDK transport tests, consent and connection-management UI, remaining read-only tool projections and security coverage. Phase 1 remains open. Later commercial limits, monitoring/email destinations and privacy-retention decisions are still required before their dependent work.

### 31 October 2026 — Remote read-only increment and real browser journey verified

- Implemented the four account-scoped read tools with versioned string IDs, bounded paging, explicit current-list states, nullable/unknown saved-price behaviour, observation time/source/store/location and removal of private source-URL parameters. Reused existing list/product services; no retailer requests or list/product writes occur from reads.
- Added disabled-by-default OAuth/MCP hosting, issuer/resource validation, preconfigured exact client callbacks, authorization code with S256-only PKCE, explicit first-party consent, optional refresh grants, immediate token/authorization validation and account/client revocation. Protocol/IP and authenticated/account request limits are separate. A first-party session is never accepted as an MCP token.
- Added consent and Connected AI Apps website pages, permission selection, account-switch protection, revocation, activity display, and explicit shared current-list selection. Existing website and extension session/login reads prefer the selected accessible list. Viewing another list does not select it for AI.
- Added fixed-name tool outcome counters and correlated, content-free audit events. Expected tool errors become stable structured error codes. Transport activity metadata is tracked separately from read-only shopping services.
- Focused final-source API verification using a separate test output directory: **40 passed, zero failed/skipped**, 46 seconds. Includes actual official SDK automatic OAuth discovery/PKCE/state/token exchange, manual security cases, token expiry/renewal/revocation, missing scopes, denied resources/callbacks, cross-account access, configuration validation, source privacy and database-enforced read-only queries. S256-only and idempotent revocation are covered.
- Real browser/API/disposable-schema journey: **1 passed**, 55.8 seconds. Verified cancellation, shared selection/session preference, approval after account switching rejected, approved token exchange, real MCP initialization, mobile layout and immediate revocation. Earlier attempts exposed only test locator ambiguity (wrapped label/options and Next route announcer); corrected role-scoped selectors. No product assertions were weakened.
- Frontend webpack production build and TypeScript passed. New/changed frontend files pass ESLint. Additive migration successfully applied to the disposable database and migration-upgrade test passed. Production configuration and databases remain untouched. Full relational regression verification is still in progress; no Phase 1 task checkbox is closed yet.
- Added `docs/AI_CONNECTIONS.md` covering configuration, supported client requirements, key/deployment requirements, price limitations, activity metadata, current-list semantics and safe feature rollback. Exact commercial quotas and later monitoring/billing/privacy decisions remain pending.

**Next task:** Finish the full regression gate and final browser check of the MCP-address display. Then proceed to Phase 2 scoped writes, concurrency/idempotency and the customer-approved personal extension bridge. No cross-user execution or production activation is authorised.

### 31 October 2026 — Phase 1 acceptance completed locally

- Full relational run: **3,015 passed, 1 failed, 5 opt-in skips; 3,021 total**, 18m43s. The sole failure was a legacy migration rollback test assuming its migration was still latest, causing its schema-wide fingerprint to include the legitimate rollback of newer empty tables. Scoped that test to its own migration; its integrity assertions remain unchanged.
- Added a guarded rollback for the new additive migration. It locks the six new tables and refuses rollback if any contains current-selection or authorization data. Final migration regression group: **8 passed**, zero failures/skips, 55 seconds, including the repaired legacy case, empty rollback, grant-data protection and current-selection protection. The complete 18-minute run was not repeated; this targeted run resolves its sole failure.
- Final focused API/auth/read/correlation group after the final session-contract changes: **55 passed**, zero failures/skips, 1m08s. Final available suite without a test database: **2,672 passed, zero failed, 353 explicitly skipped; 3,025 total**, 10 seconds. Database skips are separate from the passing relational evidence, not claimed as database passes.
- Final real OAuth/browser journey: **1 passed**, 52.5 seconds, including the MCP-address display and explicit current-list session field. Installed-extension journey rerun: **1 passed**, 1.3 minutes, now also verifying account current-list changes reach the actual extension and preserve the existing capture, desktop/mobile and two-account isolation workflow. Retailer pages remained fixtures.
- Extension regression suite: **145 passed**. Extension follows changes to the account selection while allowing local browsing of another owned list; browsing does not mutate the account selection. Existing extension/session behaviour is preserved when no explicit selection exists.
- MSL-100 through MSL-106 are complete for this verified local read-only release, and the Connected AI Apps requirement MSL-202 is already satisfied. No commercial AI application is claimed verified beyond the official SDK client. Public client registrations, production keys/hosting/proxy settings, operational pruning/export and production schema deployment still need deployment configuration/approval.
- Test evidence remains under `%TEMP%/myshoppinglist-phase0-postgres/phase1-oauth-20261031/`. Production activation, paid services, live retailer collection and cross-user execution remain excluded.

**Next task:** Phase 2 MSL-200/MSL-201: scoped write tools over existing services with atomic idempotency receipts, optimistic concurrency, explicit removal confirmation and cross-user/retry tests; then personal device/task registration and customer-approved capture. Keep all new scopes opt-in rather than adding them to existing grants.

### 31 October 2026 — Phase 2 scoped writes verified; extension bridge next

- Added `create_list`, `set_current_list`, `add_product_to_list` (saved or generic product), `update_list_item` and `remove_list_item`. They require `lists.write` and are registered only when that scope is explicitly offered by configuration. Existing read-only grants remain read-only. Partial edits preserve omitted fields; removal requires explicit confirmation and a current item version.
- Reused existing list/product/current-list services through an optional outer write transaction. Account-bound UUID receipts and mutations commit atomically; repeated requests replay the original result, while changed inputs under the same request ID are rejected. The coordinator preserves existing import/account/list lock ordering. It detaches only unchanged validation entities, retaining the existing clean-context safeguard discovered by the real OAuth integration test.
- Added independent account selection revisions to prevent version reuse after deleting the current list. Migration backfills existing revisions; nonempty revision/receipt tables block rollback. Generic items remain private, unpriced and without invented retailer/package metadata.
- Fixed scope-array configuration binding so explicit scopes replace initialized defaults, including explicitly empty configuration. Regression tests cover both cases.
- Direct write/configuration group: **20 passed** after correcting the injected-failure test to expect EF's `DbUpdateException` wrapper. Actual OAuth/SDK plus direct write group: **8 passed**. Final write/current-list/OAuth group including deletion/version reuse: **11 passed**, 51 seconds. Reused services/schema/security regression group: **103 passed**, zero failures/skips, 3m25s.
- Real browser/API/MCP journey extended: **1 passed**, 1.1 minutes. MCP creates a list, retries without duplication, selects it, adds an unpriced item, updates its quantity and removes it; the actual website shows each change. Existing consent, account-switch protection and mobile revocation remain verified.
- Added guarded `AddMcpMutationReceipts` and `PreserveAccountCurrentListRevisions` migrations. Applied only to the disposable test database. Migration tests target their own migration versions so future additive migrations do not invalidate rollback fingerprints. Production selection writes must remain stopped across the revision migration/code rollout to avoid mixed-version writers.
- Owner selected existing limits and test-only billing groundwork; optional visitor-analytics consent, 30-day raw-event and 12-month aggregate retention. Incident recipient was supplied and will be kept in local configuration; sending remains simulated until a provider is configured.

MSL-200/MSL-201 are complete locally. Phase 2 remains open for MSL-210–216: per-account device credentials, truthful presence/task states, customer approval before supported URLs open, bounded capture/search and failure/restart/isolation tests. No cross-user execution, production activation or provider spending is enabled.

### 31 October 2026 — Phase 2 personal extension bridge verified locally

- Added disabled-by-default `ExtensionBridge` hosting, account/session/extension-bound device credentials (server hashes only), own-browser management, truthful recent/offline/permission/approval states and expiring typed task lifecycle. Task/result access is restricted to the originating account and AI grant; another customer's browser cannot receive, claim or complete it. First approval claims one own browser; other devices cannot duplicate execution.
- Added four MCP tools for presence, capture/search requests and status. Requests validate supported HTTPS product/search routes and strip private URL parameters. No backend retailer fetch, arbitrary JavaScript, CAPTCHA bypass, paging or cross-user execution is introduced.
- Added the extension's native top-level AI requests page. It links explicitly, polls while visible, opens one supported URL only after customer approval, reuses the existing bundled reader, and separately requires Save/Share after review. Search is bounded to five unverified links, with null names when unknown. Capture completion and private persistence commit together. Exact retries survive worker restart/uncertain responses; cancellation, account changes, navigation changes, permissions and retailer restrictions do not save products.
- Website Connected AI Apps now lists own extension links and supports confirmed unlinking. The fixed gateway allows only those account-management routes. Task/device credentials and raw evidence are never returned in view snapshots.
- Migration `20261031035902_AddPersonalExtensionDevicesAndTasks` is additive and refuses rollback with any link/task history. Disposable-schema tests verify upgrade, empty rollback, data preservation and the nonempty guard. No production migration was applied.
- Final API relational group: **46 passed, zero failed/skipped**, 2m15s (`phase2-bridge-final.trx`): bridge security/state/search/capture, duplicate-device claim rejection, stale evidence, own-grant revocation, guarded migration, existing personal capture, write/current-list and configuration regressions. Earlier bridge/migration/personal-capture group: **23 passed**, 2m11s. Existing reused-service/security group for Phase 2 writes: **103 passed**, 3m25s.
- Installed-extension/real OAuth/MCP/API/website journey: **1 passed**, 1.4m. Verified no page before approval, no product before Save, matching saved website quantity, bounded reviewed search without extra products, and website browser unlinking. Chromium's extension-created first-navigation race initially bypassed a fixture; the final harness attaches on about:blank and blocks retailer DNS before fixture navigation. No live retailer reliability is claimed. Static bundled imports corrected a real MV3 loading failure; approved pending navigation is allowed only during load, then revalidated before/after reading.
- Extension regression group: **151 passed** before the final blocked-site case; the final seven bridge tests pass, adding restricted-retailer termination without retries/saves. Frontend production webpack build, TypeScript, changed-file ESLint and whitespace checks passed. Final full regression count is recorded with subsequent verification if rerun; no whole relational suite rerun is implied.

MSL-210–216 and Phase 2 are complete for the safe local release. AI/bridge feature flags remain OFF by default. Known operational follow-up: provider-independent diagnostics/retention, export/deletion, production keys/client registration and deployment validation. The alert recipient has been supplied privately; live email, commercial prices/provider spending, production payments, mass scraping and cross-user execution remain excluded.

**Next task:** Phase 3 — reuse the existing administrator policy, account review/audits and health services; add server-protected Super Admin, private feedback, privacy-limited operational metrics and simulated durable incident/recovery delivery. Keep external monitoring/email provider activation as documented configuration work.

### 31 October 2026 — Phase 3 local operations/support acceptance completed

- Added default-OFF `Operations`, six additive operational/support tables and guarded migration `20261031043409_AddOperationsAndPrivateFeedback`. Existing administrator policy, account review/audits and health code are reused. `/superadmin` and `/admin` now require a server-verified administrator session; backend policy independently protects data. Client account caches are cleared/fenced on session changes. Account updates have explicit confirmation, reason, concurrency, search/filter and contact/save context.
- Added private website/native-extension feedback, retry-safe submission, optional rating, explicitly approved and processed screenshots, a versioned status inbox, internal issue references and in-ticket customer responses. Screenshot downloads and administrator operations are audited; internal notes/references are not customer-visible. No screenshots, raw exception text, tokens, URLs or list/message contents enter diagnostics.
- Added limited API/MCP/frontend diagnostics, safe fingerprints/correlation, release/status/latency, account activity/activation proxies, consented visitor/session/view counts, task/version/outcome/latency summaries and a limited account journey. Registration correlation uses the trusted completed auth result rather than guessing an anonymous account. Missing/expired historical events remain unknown. Cancellations are 499/cancelled, not false server failures.
- Optional visitor consent is OFF until chosen; withdrawal forgets known raw visitor events. Hourly pruning uses the exact 30-day cutoff while preserving completed daily totals across partial-day expiry; anonymous aggregates expire after 12 months. Inputs accept only fixed page/action categories and limited source slugs, never raw browsing URLs. Essential diagnostics use fixed categories and bounded reporting rather than private error text.
- Added durable incident/open/recovery delivery records, dedupe, cooldown and sample/window/multi-account P1 rules. Delivery is explicitly `simulated`, `suppressed` or `not_configured`; **no email provider or live sending exists**. Independent bounded uptime tooling uses its own SQLite state/outbox, can record API/database outages without the application database, and has simulation/tests. Scheduling on a separate production host remains deployment configuration. Readiness checks connectivity and applied migration history.
- Owner selected **support-reviewed account export/deletion requests**, not automatic deletion. Support provides request controls and ownership/review instructions. Read-only export preparation validates ownership/schema, redacts auth/internal fields, refuses overwrites and requires human coverage/delivery review. No real account was deleted or exported.
- Real operations API/browser journey: **1 passed**, 54.2s, including consent/withdrawal, approved screenshot, private feedback, administrator response without internal reference leakage, prepared/sent deletion request without account removal, one simulated open/recovery, and confirmed suspension. Earlier journey passed in 51.4s before the privacy request increment; a corrupt PNG fixture and label-selector issue were corrected without weakening product assertions.
- Existing administrator desktop/mobile browser regressions: **62 passed**, 2.3m. Fixtures now provide explicit server-side admin sessions; production guards are not bypassed. Real journeys have separate configs and are excluded from the ordinary mock suite. Installed-extension/real OAuth/MCP journey with native feedback/operations enabled: **1 passed**, 1.2m; capture/search and browser unlinking remain verified. Final extension regressions: **152 passed**.
- API evidence: migration/model/old-admin/operations/correlation group **31 passed**, 1m17s; final safety/retention/bridge group **12 passed**, 1m02s; cancellation **1 passed**; auth plus registration-correlation **19 passed**, 42s; final operations acceptance **8 passed**, 37s, including migration-readiness, partial-day retention and multi-account alert thresholds. Available suite without DB: **2,674 passed, 377 explicit skips, zero failures; 3,051 total**, 11s. Database skips are not claimed as relational passes. The complete relational suite was not rerun.
- Python probe/export tests: **5 passed**. Actual disposable two-account export/redaction plus dump/restore rehearsal passed, with every restored table fingerprint matching. Only generated schemas/databases were removed; zero generated schemas remained. Fixture dumps/manifests remain in ignored `.local-backups`. Intermediate rehearsal failures (duplicate fixture IDs and a psql formatter terminator) were corrected; no application/public data was altered.
- Final frontend webpack production build, TypeScript, changed-file ESLint and whitespace checks passed. No new paid/runtime dependency or provider account was added. `docs/OPERATIONS.md` records configuration, privacy, limits, backup/export/deletion review, outage handling, safe rollback and production prerequisites.

MSL-300–310 are complete for the approved **local/simulation scope**. Actual provider delivery, external-host scheduling, production backup/security configuration and deployments remain unactivated prerequisites. **Next task:** Phase 4 truthful public pages/metadata/sitemap, privacy-safe funnel views and launch documentation. Owner selected allowing all crawlers on public pages; private pages/APIs remain blocked. Commercial prices and production payment activation remain excluded.

### 31 October 2026 — Phase 4 public pages and local discovery checks completed

- Reused the existing home/account/trial workflow and its server-rendered headline. Added server-rendered feature, how-it-works, FAQ, plans and privacy pages; updated extension/support copy and shared public navigation. Extension access is truthfully described as a local test build without a public store listing; developer machine paths/ports were removed from customer onboarding. Saved observations are not advertised as automatic live checks. Existing default trial/list limits remain explicit; no invented commercial prices or live checkout are offered.
- Added configured-origin canonical/Open Graph/Twitter metadata, a public-only sitemap, owner-approved wildcard crawler policy, private-prefix robot rules and private-route noindex/referrer metadata. Missing/unsafe public-origin configuration fails closed for discovery. Home SoftwareApplication JSON-LD contains no fake offers/reviews/ratings. Generated and visually inspected the real PNG share card; text fits without private data or external image generation/provider calls.
- Added a protected marketing dashboard over existing operations data: consented source/view/CTA/signup reports, saved-item activation proxy and observed 7/30-day activity, with explicit population/window limitations. It does not fabricate matched-cohort conversion, D7/D30 retention or revenue. Source slugs persist only within a consenting browser session, and withdrawal clears them. Consent/privacy text discloses signed-in account association. Campaign/API data is protected by the existing administrator policy.
- Public-content/canonical/sitemap/robots/consent/image tests plus existing shopping-list/preferences browser group: **84 passed, 2 failed**, 7.6m. The two failures were stale tests requiring the old developer-path installation copy. Updated them to require truthful current access/support instructions and absence of machine paths; targeted desktop/mobile rerun: **2 passed**, 16.8s. No remaining known failure in that group; the full 86-test group was not repeated. An earlier multi-route development check needed a 90s first-compilation allowance; its assertions were retained.
- Final **production-build** public-page checks: **10 passed**, zero failures/skips, 10.6s, desktop/mobile. Verified meaningful initial HTML, unique metadata, public-only discovery, private noindex, layout bounds, consent/withdrawal/source retention and actual PNG output. Local feature-page measurements: desktop TTFB 2ms/DOM ready 21ms/147,962 transferred resource bytes; emulated mobile 2ms/22ms/150,438 bytes. No network/CPU throttling, deployed field performance or Core Web Vitals claim is made.
- Origin/private-inventory security tests: **2 passed**. Acquisition API regression **1 passed** (12s); earlier operations/marketing API group **2 passed**. Final frontend webpack build/TypeScript passed; changed source lint has zero errors, with two pre-existing unused-variable warnings in the existing shopping-list test. Extended Node regression command includes bridge and SEO tests. Whitespace checks passed.
- Added `docs/MARKETING_AND_LAUNCH.md`: inspected implementation, measurement definitions/limitations, domain/build configuration, Google/Bing operational checklist, owner crawler policy, Chrome listing-copy/screenshot recommendations and a small invited-cohort launch plan. Sources are official documentation. No search account connection, public publication, paid ad, store submission, commercial price or provider spending occurred.

MSL-400–405 are complete for the local release. Actual canonical domain/operator/privacy details, store distribution/permissions, deployed monitoring/performance and publication remain deployment prerequisites. **Next task:** Phase 5 — preserve current limits and build test-only billing records, signed/idempotent webhook/reconciliation groundwork, honest billing/admin UI and failure/retry tests. Live payment activation remains prohibited without separate approval.

### 31 October 2026 — Phase 5 approved test-only groundwork verified

- Added the official stable Stripe.net 53.1.0 SDK, disabled `Billing` configuration, Development-only/test-key validation, hosted Checkout/portal adapter and private account-owned billing APIs. No credentials, commercial amounts, live provider activation or entitlement changes were invented. Trial/free/manual paid limits remain three hours/two/five, verified through existing services and tests.
- Added account/customer mapping, projected subscription/invoice/captured-payment/refund snapshots, durable webhook receipts and checkout retry fences. Signature verification uses bounded raw bodies, SDK verification and past/future tolerance. Event dedupe and customer locks protect concurrent processing; delayed events fetch current provider state. Outages preserve the last good snapshot and retry; unknown customers require review. A return URL grants no access. Refunds and cancellation do not silently change manual/complimentary access.
- Added private billing UI and protected admin test estimates, scheduled cancellation/failure/canceled counts, refunds/receipts and pending/unmatched reconciliation. Period ends are labelled billing periods, not proof of paid-through access. No recognised revenue, actual paid-through entitlement or cohort churn rate is claimed. These commercial aspects keep full MSL-503 acceptance open. Checkout refuses unconfigured prices and conservatively blocks a different checkout for 24 hours after one starts.
- Additive migration `20261031061702_AddTestBillingLedger` passed real upgrade/empty rollback and nonempty-history rollback-refusal tests. Read-only support export includes billing/webhook ownership. Actual two-account export/redaction and dump/restore passed with **all table fingerprints matching**; no application/public data changed. Python operations/export tests: **5 passed**.
- Billing/model/trial/admin relational group: **34 passed**, 26s. Final billing-only group after partial-capture, size-boundary and admin-report assertions: **12 passed**, 21s. SDK requests were exercised through fixture HTTP, including hosted fixed return URLs, configured server price and provider idempotency; no real Stripe charge/request. Final available suite: **2,682 passed, 382 explicit database skips, zero failures; 3,064 total**. Full relational suite was not rerun.
- Final desktop/mobile billing fixtures: **6 passed**, 23.9s. Verified unavailable configuration, retry body/account ID, session-change clearing, noindex, reporting and layout. Mobile screenshot inspected. Earlier two failures were selectors colliding with Next's route announcer; scoped to the billing main element without weakening assertions. Real API/browser operations journey including billing-return safety: **1 passed**, 55.6s. Final webpack production build/TypeScript and changed-source ESLint passed. Final production public-page checks: **10 passed**, 10.8s.
- `docs/BILLING_TEST_GROUNDWORK.md` records architecture, configuration, conservative retry limits, snapshot/reporting definitions, rollback, retention/export and future gates. A global `npx` shim was broken; direct installed Node CLI entry points worked. No system npm configuration was changed.

The approved **local test-only deliverable** is complete. Full MSL-503/504 acceptance remains open for actual sandbox-provider verification and commercial paid-through/refund/grace/downgrade/accounting decisions. **Next task:** Phase 6 design record only; do not implement community sharing or cross-user execution.

### 31 October 2026 — Phase 6 design deliverable delivered; implementation gate remains closed

- Added `docs/COMMUNITY_DATA_DECISION.md`: options A/B/C, benefits, privacy/abuse/cost risks, conditional observation/consent fields, explicit per-observation consent and withdrawal text prototypes, freshness/provenance rules, required isolation/revocation/cache tests and rollout conditions. Existing authorised personal scope remains the only implemented direction. No sharing entity, cross-user endpoint, job-routing change or dormant execution flag was added.
- Inspected current primary Chrome Web Store and Coles/Woolworths/ALDI terms. Chrome disclosure/limited-use and minimum-permission constraints require workflow-specific review; user consent is not blanket permission. ALDI restricts automated access with a search-engine exception not established to cover this extension; Woolworths restricts retrieval mechanisms; Coles restricts personal/reuse purposes. No retailer permission, legal determination or store approval was obtained. These are material launch constraints, not resolved by fixture capture success.
- No empirical evidence of community value exists yet; the record proposes an authorised synthetic/licensed-data study and measurable criteria, without inventing customers, outcomes or a spending budget. No recruitment, live collection, provider spending or community experiment occurred.

MSL-600/601/603 design work is complete. MSL-602's compatibility/permission and empirical-value requirements remain unresolved, and explicit optional rollout approval has not been given. **Phase 6 gate is closed**, not accepted for implementation. All authorised safe local implementation/design work has been delivered. **Next task:** operator release readiness—supply/configure sandbox credentials/test prices, confirm the canonical domain/operator/privacy details and monitoring provider/budget, resolve retailer/store permissions and review deployment/backup/security—before proposing separately approved activation. Live payments, sending, production migrations, publication, mass scraping, sharing and cross-user execution remain unactivated.

Final verification/teardown: available API rerun **2,682 passed, 382 explicitly skipped, zero failures**, 9s; both repositories' whitespace checks passed. Verified zero generated schemas/restore databases, stopped only the verified Codex-owned disposable PostgreSQL cluster and removed its plaintext test password. Ports 3001/5392/55432 are free; the user's port 3000 was untouched. Fixture dumps/test reports remain private local artifacts. Full MSL-503/504 and MSL-602 remain honestly open for the external acceptance/decision gates above; no approved independent local task remains unfinished.

### 31 October 2026 — Final release follow-up and complete regression verification

- Owner requested completing all remaining work and confirmed no Stripe sandbox/public domain exists yet; reconfirmed the private application email destination. No payment account, legal operator identity, delivery provider, budget, credentials or retailer permission was invented. These external prerequisites remain open rather than being represented as completed tests.
- Review found and fixed provider-timeout handling in `TestBillingService`: timeouts expressed as cancellation exceptions now persist retry/backoff when the caller is not cancelled; genuine caller cancellation still propagates/rolls back. Added an acceptance case covering both paths and recovery. Billing group **13 passed**, 32s.
- Review confirmed default SDK publish metadata would copy private `appsettings.Local.json`. Added `CopyToPublishDirectory=Never`, retaining local development use. Inspected actual Debug and Release publish artifacts: API assembly present, private local settings absent; new integration flags remain off. Artifacts are local and undeployed. No source-control secrets or production activation was added.
- Complete API relational run on a fresh migrated disposable database: **3,041 passed, 18 failed, 5 skipped; 3,064 total**, 21m34s. My verification command forced the optional feature flags off globally and overrode fixture startup configuration. Corrected feature-profile rerun: **30 passed**, zero failures/skips, 2m11s. Compared report test names and verified **all 18 failed cases** have matching passing results; no uncovered failure remains. The complete run is not falsely reported as green. Its loaded build predates the added timeout case; final feature/available checks cover that final source.
- Rebuilt available API suite: **2,682 passed, 383 explicit skips, zero failures; 3,065 total**, 11s. Separately enabled the two controlled-browser fixture groups with external sockets blocked: **4 cases passed**, 8s. The three real-retailer audit groups remain unenabled under the existing no-scraping/permission boundary. No database skip is counted as a pass.
- Complete development frontend run: **466 passed, 7 failed, 3 skipped**, 25.1m. Repaired four stale workflow assertions requiring old automatic-comparison wording, preserving all price/ownership/no-collection checks. Repaired two idle-polling fixtures to wait for the explicit extension probe and saved-data reload to finish before measuring idle requests. One development-server session-restoration timeout did not recur in the production suite; no product behavior was changed or assertion removed for it.
- Added `playwright.regression-production.config.ts`, reusing all ordinary customer/admin cases with the built release server on port 3001 and a JSON report. Complete production-build run: **473 passed, zero failures/flaky results, 3 intentional skips; 476 total**, 9.7m, desktop/mobile. Covers all failed development workflows with the final fixtures. Skips are mobile native Chrome-extension support and two dedicated performance-measurement cases (already verified separately using the production measurement config).
- API Release publish, TypeScript, changed-file lint and both whitespace checks passed. Two existing unused-variable warnings remain in the refresh fixture. Latest API TRX reports and frontend JSON/traces remain ignored local artifacts. `docs/RELEASE_READINESS.md` and README now provide one release handoff with commands, exact evidence, configuration precedence, private packaging and remaining setup sequence.
- Final teardown verified zero generated schemas, stopped only the fresh owned PostgreSQL cluster, removed its plaintext password and private local-setting copies from its temporary build outputs, and confirmed ports 3001/5392/55432 free. The user's port 3000 and original private settings were untouched.

**Current outcome:** all executable safe local release work is complete and verified. Full MSL-503/504 and MSL-602 remain open for actual sandbox/commercial and policy/permission acceptance. **Next external setup:** create/select the owner's sandbox and domain/operator details, configure approved providers/budget, resolve retailer/store permissions, then perform the real provider/deployed checks before separately approved activation. No live payment, sending, production migration/deployment, mass scraping, sharing or cross-user execution occurred.

### 31 October 2026 — Local application schema mismatch repaired

- Owner reported `42P01` in `TrialSessionService.CurrentAsync`: ordinary session restoration queries `UserCurrentLists`, but the configured local development database still had only 18 migrations through `AddLegacyProductMigrationMaps`. Earlier verification had upgraded disposable databases, leaving this application target behind the updated code. Optional MCP flags do not remove this core schema requirement.
- Verified local target `localhost:5432/MyShoppingList`, `public`; captured a full custom-format backup into an ignored owner-access-restricted directory. Application role could create schema objects/owned its session table but could not create another database; no privilege change was made. Restored the backup into a separate owned PostgreSQL instance for rehearsal.
- Applied all six prepared migrations on the restored copy and compared every pre-existing table: **29 identical row counts/data fingerprints**, 48 tables after upgrade. Then applied the same migrations to the confirmed local application database with bounded lock/statement waits: **all 29 existing tables again remained identical**. No data reset, manual table workaround, ownership bypass or live integration activation.
- Final read-only checks: both current-list tables exist, **24 migrations applied**, latest `20261031061702_AddTestBillingLedger`; session/current-list join resolves. The authenticated live endpoint was not claimed verified: the existing API did not respond to a three-second readiness probe and may be paused at the debugger exception. Resume/continue and retry; no application restart was forced.
- Private recovery artifacts: `.local-backups/schema-repair-1f008b2cbb244589aabb71a51f7a71e1/` contains actual local data and fingerprint manifests; it is access restricted and ignored. Stopped only the rehearsal cluster and removed its temporary password. Original PostgreSQL on 5432, application process, credentials and user port 3000 were untouched apart from the approved local schema upgrade. README/release handoff now explain the always-required schema and missing-table recovery sequence.

**Next task:** retry the interrupted local session request after continuing the debugger. External production/payment/provider/community gates remain unchanged.

### 31 October 2026 — Local cancellation/timeout follow-up resolved

- Owner supplied cancellation and transient Npgsql timeout stacks in session authentication, then confirmed: **the page works now and the errors stopped**.
- Live checks: readiness HTTP 200; synthetic invalid-session request exercised authentication and returned the expected HTTP 401 in 258ms. Read-only database checks found zero blocked backends, an existing token index and a synthetic lookup execution time of 0.037ms. No real session tokens or private account data were accessed for these probes.
- Existing cancellation middleware correctly distinguishes client aborts from genuine failures; **5 cancellation tests passed**, zero failures/skips. No runtime change, timeout increase, retry policy, swallowed database failure or authentication fallback was introduced. Interrupted/debug-paused requests are a plausible explanation, not a reproduced root-cause proof.

**Current local incident:** resolved by schema upgrade plus request recovery, confirmed by the owner and current diagnostics. No further local fix is indicated. External release gates remain unchanged.
