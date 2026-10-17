# MyShoppingList backend handover

Updated 17 October 2026 (Australia/Adelaide). Read this together with [frontend handover](../myshoppinglist/HANDOVER.md). This is a continuation guide, not authorization to make new changes.

## Working rules from the user

- Before editing, creating or deleting files, explain the request, list exact proposed files and changes, give reasons and ordered steps, then stop for approval. Change only approved files; ask before expanding scope.
- Explain intended actions before doing them. Ask when requirements are ambiguous rather than guessing.
- Always format changed code. Preserve unrelated work. Do not assume previously reported results establish current runtime health.
- Reserve frontend port **3000 for the user**. Codex uses **3001**, preferably via a command-line override. Never stop/restart the user's port 3000 server. If 3001 is occupied, ask before stopping it or selecting another port.
- Use Zustand for frontend global state. No agent delegation is currently requested.
- Keep credentials, session tokens, worker keys and connection strings out of documentation and output.
- Read the current root `AGENTS.md`; it includes the approval workflow and permanent product-quality rules. Recheck current instructions before continuing.

## Current position

The supermarket-comparison work now includes persisted My Stores and comparison preferences, saved verified branches, same-product package-size alternatives, reviewed different-brand alternatives, comparison provenance and clearer product/list refresh outcomes. Earlier saved-link, promotion, workload-control, extension and administrator-health features remain implemented.

Stage 10 adds combined authenticated endpoint tests for registered and trial customers and a companion frontend journey. Application behavior and database schema are unchanged in this stage. The next customer task is the combined local smoke test in the frontend handover; observed defects need a new exact approved file plan.

On 17 October, the existing local API process was verified as the Stage 9 build in `bin/CodexRefreshOutcomes`, listening at `http://localhost:5392`. Root returned 200; anonymous planning/in-store requests returned 401. This verifies availability and authentication enforcement, not the owner's signed-in session or live retailer collection. No restart was needed for Stage 10. Recheck runtime and Git state next time rather than relying on a historical process ID or clean-tree claim.

## Architecture and local configuration

- ASP.NET Core / .NET 10, EF Core 10, Npgsql/PostgreSQL; project `myshoppinglist-api.csproj`, solution `myshoppinglist-api.slnx`.
- Database name is exactly **MyShoppingList**. Private configuration is in ignored `appsettings.Local.json` and/or environment variables. Do not copy its contents into chat.
- Current launch profile `http` in `Properties/launchSettings.json` uses **http://localhost:5392**, with development Swagger at `/swagger`. Earlier conversation references to HTTPS port 7033 are historical, not the current checked profile.
- The frontend proxies an explicit allowlist of `/api` routes. New backend endpoints usually need a corresponding approved proxy change.
- Cookie/browser request protection and bearer authentication differ. Do not disable protection to fix extension access. `UserExtension:AllowedExtensionIds` explicitly permits customer extension origins. `ColesExtension:WorkerKey` is the trusted worker credential. `Admin:AccountIds` grants administrator access.
- Administrator grants belong in private configuration. Verify current account permissions rather than relying on historical account IDs.
- Serilog records errors and selected information events (including background processing) under `logs`; inspect current configuration for exact sinks/retention. Avoid logging secrets or raw customer evidence unnecessarily.
- Runtime does not automatically migrate the database. Latest migration: `20261017044704_AddPreferredShopLocations`. A read-only local migration listing on 17 October showed all 12 migrations without pending markers. Stages 8–10 add no migration; verify migration state independently in every deployment environment.

## Product and processing behaviour

Shared catalogue records are separate from account-owned shopping lists. Coles and Woolworths have implemented integrations, subject to live access restrictions. ALDI is explicitly `Restricted`; IGA and Foodland remain known but unimplemented. Saving a retailer preference does not create collection capability.

1. Identify and validate the retailer URL and product identity.
2. Reuse fresh saved data where available. Freshness follows Wednesday 00:00 **Australia/Adelaide**, including daylight saving; expired promotions are not fresh.
3. Reuse verified exact cross-retailer mappings and read their saved product URLs directly before repeating search. Reject contradictory identity evidence rather than accepting a nearby flavour or pack size.
4. Persist source information and expose partial progress while comparisons finish. Database-backed jobs use leases, claim tokens and guarded writes to prevent stale submissions overwriting newer work.
5. Coles reads/searches rely on extension work, not direct Coles API calls. Woolworths has backend extraction/search with extension fallback when access is restricted. Customer Add can collect both retailers through the customer's extension.

Important services:

| Area                          | Starting files                                                                                                                                                                        |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Import orchestration          | `Services/ProductImportSubmissionService.cs`, `ProductImportProcessor.cs`, `ProductImportJobService.cs`, `Workers/ProductImportWorker.cs`                                             |
| Persistence/cache             | `SourceProductPersistenceService.cs`, `SourceProductCacheService.cs`, `ShopProductService.cs`, `PriceService.cs` under `Services`                                                     |
| Identity/comparison           | `Services/ProductMatchingService.cs`, `ProductNormalisationService.cs`, `RetailerComparisonService.cs`, `RetailerComparisonPersistenceService.cs`; retailer parsers under `Providers` |
| Shared extension queue        | `Services/ColesExtensionTaskService.cs` (historical name; handles both retailers)                                                                                                     |
| Personal extension import     | `Services/UserExtensionImportService.cs`                                                                                                                                              |
| Customer contribution/access  | `Services/ContributionService.cs`, `AdminAccountService.cs`, `Security`, `Endpoints`                                                                                                  |
| List planning/history         | `Services/ShoppingListService.cs`, `InStoreShoppingService.cs`, `ShoppingListNameService.cs`                                                                                          |
| Preferences and branches      | `Services/ShoppingPreferencesService.cs`, `ShopLocationService.cs`; `Contracts/ShoppingPreferencesContracts.cs`                                                                       |
| Other sizes / reviewed brands | `Services/PackAlternativeService.cs`, `PackDiscoveryService.cs`, `BrandAlternativeService.cs`                                                                                         |
| Provenance / refresh outcomes | `Services/PriceObservationContextService.cs`, `PriceRefreshService.cs`; `Contracts/PriceObservationContext.cs`                                                                        |

The trusted Retailer Reader and customer Add extension are separate. Customer shared collection defaults on, can be opted out, and claims one task while idle; personal Add takes priority. The server records authenticated contributor provenance and validation outcomes. Administrators can independently set paid status, block contributions, and suspend/restore accounts with audited reasons. A parsing error alone does not establish abuse. Contribution blocking can preserve shopping access; account suspension revokes sessions. Paid status is manually managed, not an implemented billing integration.

## Lists, prices and refresh

- Registered free accounts: 2 active lists. Paid accounts: 5 active lists. Paid users can manually archive and view `/history`; free users delete and cannot access history.
- Nonempty lists with all items purchased or hidden automatically archive, including temporary trials. Deleted lists are not available through user history. Empty lists are not automatically archived merely for being empty.
- Automatically created names use the user's date and daily sequence, e.g. `Shopping_List_07_10_2026_01`.
- Customer extension repeated Add uses the new quantity rather than incrementing or silently retaining the old quantity. Check the precise import entry point when modifying duplicate handling; older README text describing quantity preservation is not universally current.
- `PriceRefreshWorker` / `PriceRefreshService` refresh stale known mappings for items **still to buy**: not purchased/hidden, in eligible active lists/accounts. Defaults: scan every 15 minutes, failed-refresh cooldown 6 hours. Queue entries deduplicate shared products and preserve higher-priority work. A recent unavailable observation is still a completed check, avoiding loops.
- Prices are observed retailer-page prices, with store/location uncertainty retained. Unknown price is not proof a retailer does not sell a product. Do not fabricate zero prices.
- `PromotionCalculationService` separates single-item prices from verified multibuy totals, computes complete bundles plus leftover singles, and compares quantity totals. Example: verified 2 for $30 with $30 single price gives quantity 1 = $30, 2 = $30, 3 = $60. Membership eligibility and mixed-product baskets are not inferred. Stale or unverified conditional prices do not become trustworthy cheapest totals.

## Comparison rules and current API

- `GET/PUT /api/account/shopping-preferences` persists chain preferences, preferred-only filtering, independent package-size/brand toggles and ordered branch selections for the authenticated account, including trials. Saves check account identity and the current settings timestamp. Omitted/null branch selections preserve existing selections; an explicit empty array clears them.
- Preferred retailer filtering runs in backend queries and before new retailer work. Package-size comparisons default on; different-brand comparisons default off. Exact matches remain independent of these optional settings.
- `GET /api/shop-locations` exposes the verified branch directory. Administrator review endpoints live under `/api/admin/shop-locations`; directory data and private review evidence are separate. Saved branches identify locations but do not enable branch-specific price collection or establish local stock.
- `GET /api/shopping-lists/{id}/plan` and `/api/shopping-lists/{id}/in-store` return store context, exact prices and qualifying saved alternatives. Pack alternatives require structured same-product evidence; brand alternatives require a current administrator-reviewed relationship. Changed product evidence invalidates old approval. Alternatives never silently replace the customer's item or change its quantity/totals.
- Discovery is explicit, scoped to remaining products and permitted supported retailers. No scheduled whole-catalogue harvesting is implemented. ALDI is not an automatic-collection source; do not infer permission from a saved preference or user consent.
- Each comparison price has optional `observation` metadata: `sourceType`, `priceScope`, `sourceUrl`. It comes from the selected observation, including retained historical prices and alternative baselines. Public source links are limited to established Coles/Woolworths product routes; credentials, private routes, query parameters and fragments are not exposed. Contributor IDs and review evidence are excluded.
- General comparison eligibility accepts `Online`, `National` and legacy `Unknown` coverage. Unknown remains an estimate, not a national or saved-branch quote. Branch-linked observations, `InStore`, `Catalogue`, `StoreSpecific` and undefined scopes do not contribute to general totals or savings. Currency, freshness, stock and promotion rules still apply.
- Manual `POST /api/shopping-lists/{id}/refresh-prices` and `/items/{itemId}/refresh-prices` use the client's local day (`X-Client-Timezone`, UTC fallback). Automatic scans retain the Wednesday catalogue freshness policy. Refresh checks known verified mappings for remaining items, not a whole retailer catalogue.
- Refresh responses contain `queued`, `alreadyRunning`, `current`, `deferred` and `checkedUnavailable`. Only usable current comparison prices count as `current`. A recent completed check with no usable price counts as `checkedUnavailable`, without repeated collection. Existing shared-task deduplication, priorities and cooldowns remain. A queued check does not itself change the observed price or its date.

## Earlier improvements retained

1. **Saved links:** fresh cache first, then direct verified retailer URL refresh; fallback searching and identity checks retained.
2. **Promotions:** shared backend quantity calculation used in planning and in-store totals; deal wording remains visible so shoppers understand conditions.
3. **Shared workload controls:** default maximum 2 concurrent tasks per retailer; 3 blocked reports within 10 minutes pause that retailer for 15 minutes; recovery allows one trial. Persisted `RetailerWorkloadState` coordinates workers. This does not bypass retailer challenges or guarantee live access.
4. **Regression collection:** backend `tests/myshoppinglist-api.Tests/Fixtures/Regression/products.json` and frontend companion browser fixtures. Read the adjacent `README.md` for provenance and commands. Includes troublesome punctuation, navigation, flavours, sizes, multipacks, suggestions, blocked pages and promotions. Reconstructed examples are labelled; they are not all live captures.
5. **Administrator health:** `Contracts/AdminHealthContracts.cs`, `Services/AdminHealthService.cs`, `Endpoints/AdminHealthEndpoints.cs`, registered in `Program.cs`, tested in `AdminHealthTests.cs`. Administrator-only, read-only, no-store, consistent database snapshot. Shows waiting/processing/failed counts, expired leases, oldest outstanding creation time, latest retained 24-hour outcomes, retained blocking reports, pause/trial state and top current errors. Reading it never claims or recovers work.

Health statistics deliberately have limits: task age is from creation (including reused tasks), outcomes are each retained task's latest result rather than complete attempt history, completed searches may have no match, and personal Add comparison tasks are outside the shared queue view. It is not a complete historical analytics system.

## Validation evidence and commands

Stage 10 verification:

- The new registered/trial endpoint journeys passed (2 tests), using real ASP.NET endpoints and isolated PostgreSQL schemas. They cover authenticated preferences, verified branch selection, retailer filtering, all toggle combinations, pack/brand baselines, source privacy, mixed refresh results, deduplication, restricted-retailer selection, cross-account denial and suspension.
- The final focused backend run passed **88 tests, 0 skipped**, including both new workflow cases and existing preferences, saved-branch, pack/brand, provenance and refresh regressions. The companion final browser run passed **22 desktop/mobile cases**, including all 4 combined registered/trial journeys. Browser tests use controlled API fixtures, not the running database API or live retailer pages. TypeScript, targeted ESLint and whitespace checks passed; the final combined screenshots were inspected.
- No test customer data was seeded into the application's normal schema. The combined factory disables background worker execution and drives queue transitions explicitly. Routine tests make no live retailer requests.
- The API and read-only migration smoke checks above were performed during this stage. A complete backend/extension suite and live retailer collection were not rerun.

Earlier results remain scoped to their execution dates: Stage 9 reported 33 backend and 58 desktop/mobile refresh/provenance checks passing, plus TypeScript, lint and a clean API build. Older milestones reported 2 health backend tests, 12 health/admin browser tests, 2,315 backend tests and 139 extension tests; those are historical results, not a current full-suite claim.

Useful commands (from this backend directory):

```powershell
dotnet build myshoppinglist-api.csproj --no-restore
dotnet test tests/myshoppinglist-api.Tests/myshoppinglist-api.Tests.csproj --no-restore --filter "FullyQualifiedName~ShoppingComparisonWorkflowTests"
dotnet run --project myshoppinglist-api.csproj --launch-profile http
```

The 88-test Stage 10 regression filter was:

```powershell
$comparisonTests = 'ShoppingComparisonWorkflowTests', 'ShoppingPreferencesTests', 'PreferredShopLocationsTests', 'PackComparisonTests', 'BrandAlternativeTests', 'PriceObservationContextTests', 'PriceRefreshServiceTests', 'PriceRefreshEndpointTests'
$comparisonFilter = ($comparisonTests | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
dotnet test tests/myshoppinglist-api.Tests/myshoppinglist-api.Tests.csproj --no-restore --filter $comparisonFilter -p:UseAppHost=false
```

Database tests require `MYSHOPPINGLIST_TEST_CONNECTION` configured securely. They may skip without it. Use the existing `IsolatedDatabaseScope` and nonparallel `Import worker database` collection for isolated database tests; other older fixtures have their own setup. Never aim destructive test setup at customer data. A temporary build output avoids locking binaries of a running API. Do not start a duplicate API or stop the user's debugger without coordinating.

## Remaining checks / future work

- Follow the frontend combined-testing checklist with a customer-owned test list and available verified catalogue records. Open administrator health separately to observe actual queue completion, pause and recovery; Stage 10 did not authenticate as the owner or drive real extension work.
- Run a small live Coles/Woolworths smoke test across website Add, customer extension Add, cached mappings, stale refresh and promotion quantities. Retailer layouts/access can change despite fixture coverage.
- Check saved mappings/refresh speed under real load; workload limits intentionally trade throughput for reliability.
- Branch-specific collection, automatic ALDI comparisons and comprehensive retailer coverage are not implemented. Brand alternatives require reviewed eligible catalogue pairs; an empty alternatives section can be correct.
- Production deployment and Chrome Web Store publishing are not done: HTTPS origins, extension IDs, permissions, privacy disclosures, packaging, secrets, migrations and production authentication need a reviewed plan.
- Payment processing, paid analytics, full historical health metrics, email verification/password recovery and additional retailers are not established as completed here. Inspect code before planning them.
- Existing root README is a historical stage log and contains superseded statements (missing list management, old duplicate semantics, older transport descriptions). Use current code and this handover, not those statements alone.

## New-chat starter

> Continue MyShoppingList. Read D:/pra/myshoppinglist-api/HANDOVER.md and D:/pra/myshoppinglist/HANDOVER.md, plus applicable instructions. First verify current Git/runtime state and summarize what remains. Do not edit files until I approve an exact file-by-file plan. Port 3000 is mine; use 3001 only if free. The latest stage adds combined comparison workflow verification and handover, following My Stores, saved branches, alternatives, provenance and clearer refresh outcomes. Review the recorded test boundaries and the customer smoke-test results before discussing further changes.
