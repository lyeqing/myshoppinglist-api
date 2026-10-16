# MyShoppingList backend handover

Updated 7 October 2026 (Australia/Adelaide). Read this together with [frontend handover](../myshoppinglist/HANDOVER.md). This is a continuation guide, not authorization to make new changes.

## Working rules from the user

- Before editing, creating or deleting files, explain the request, list exact proposed files and changes, give reasons and ordered steps, then stop for approval. Change only approved files; ask before expanding scope.
- Explain intended actions before doing them. Ask when requirements are ambiguous rather than guessing.
- Always format changed code. Preserve unrelated work. Do not assume previously reported results establish current runtime health.
- Reserve frontend port **3000 for the user**. Codex uses **3001**, preferably via a command-line override. Never stop/restart the user's port 3000 server. If 3001 is occupied, ask before stopping it or selecting another port.
- Use Zustand for frontend global state. No agent delegation is currently requested.
- Keep credentials, session tokens, worker keys and connection strings out of documentation and output.
- No repository-root backend AGENTS.md was present when this handover was written. These rules were provided directly in the conversation. Recheck applicable instructions in a new chat.

## Current position

The five requested improvement areas have been implemented: saved retailer links, quantity-aware promotions, retailer-wide shared workload controls, permanent regression examples, and an administrator health page. The latest completed feature is `GET /api/admin/health`, consumed by `/admin/health` in the frontend.

There is no unfinished implementation explicitly approved at this checkpoint. The next practical step is local integration verification of the latest features, followed by a new approved plan for any issues discovered. The previous completion message asked the user to restart the backend for the new health endpoint; whether that restart happened is **not verified**.

Read-only checks for this handover found a clean backend Git working tree before this file was created. HEAD was `f58b1eb` (`update behavior of search`). Do not assume older conversation references to uncommitted changes remain current. Runtime processes, local database migration state and account flags were not queried for this handover.

## Architecture and local configuration

- ASP.NET Core / .NET 10, EF Core 10, Npgsql/PostgreSQL; project `myshoppinglist-api.csproj`, solution `myshoppinglist-api.slnx`.
- Database name is exactly **MyShoppingList**. Private configuration is in ignored `appsettings.Local.json` and/or environment variables. Do not copy its contents into chat.
- Current launch profile `http` in `Properties/launchSettings.json` uses **http://localhost:5392**, with development Swagger at `/swagger`. Earlier conversation references to HTTPS port 7033 are historical, not the current checked profile.
- The frontend proxies an explicit allowlist of `/api` routes. New backend endpoints usually need a corresponding approved proxy change.
- Cookie/browser request protection and bearer authentication differ. Do not disable protection to fix extension access. `UserExtension:AllowedExtensionIds` explicitly permits customer extension origins. `ColesExtension:WorkerKey` is the trusted worker credential. `Admin:AccountIds` grants administrator access.
- The owner previously approved local administrator ID **1329**, associated with **ommlouisliu@gmail.com**. This is historical configuration context, not a fresh verification of roles or paid status.
- Serilog records errors and selected information events (including background processing) under `logs`; inspect current configuration for exact sinks/retention. Avoid logging secrets or raw customer evidence unnecessarily.
- Runtime does not automatically migrate the database. Latest migration file: `20261007022451_AddRetailerWorkloadState`. It was reported applied locally during the earlier work; verify before deploying elsewhere. Health page itself adds no migration.

## Product and processing behaviour

Shared catalogue records are separate from account-owned shopping lists. Coles and Woolworths are supported; ALDI, IGA and Foodland are known retailers without equivalent implemented comparisons.

1. Identify and validate the retailer URL and product identity.
2. Reuse fresh saved data where available. Freshness follows Wednesday 00:00 **Australia/Adelaide**, including daylight saving; expired promotions are not fresh.
3. Reuse verified exact cross-retailer mappings and read their saved product URLs directly before repeating search. Reject contradictory identity evidence rather than accepting a nearby flavour or pack size.
4. Persist source information and expose partial progress while comparisons finish. Database-backed jobs use leases, claim tokens and guarded writes to prevent stale submissions overwriting newer work.
5. Coles reads/searches rely on extension work, not direct Coles API calls. Woolworths has backend extraction/search with extension fallback when access is restricted. Customer Add can collect both retailers through the customer's extension.

Important services:

| Area | Starting files |
| --- | --- |
| Import orchestration | `Services/ProductImportSubmissionService.cs`, `ProductImportProcessor.cs`, `ProductImportJobService.cs`, `Workers/ProductImportWorker.cs` |
| Persistence/cache | `SourceProductPersistenceService.cs`, `SourceProductCacheService.cs`, `ShopProductService.cs`, `PriceService.cs` under `Services` |
| Identity/comparison | `Services/ProductMatchingService.cs`, `ProductNormalisationService.cs`, `RetailerComparisonService.cs`, `RetailerComparisonPersistenceService.cs`; retailer parsers under `Providers` |
| Shared extension queue | `Services/ColesExtensionTaskService.cs` (historical name; handles both retailers) |
| Personal extension import | `Services/UserExtensionImportService.cs` |
| Customer contribution/access | `Services/ContributionService.cs`, `AdminAccountService.cs`, `Security`, `Endpoints` |
| List planning/history | `Services/ShoppingListService.cs`, `InStoreShoppingService.cs`, `ShoppingListNameService.cs` |

The trusted Retailer Reader and customer Add extension are separate. Customer shared collection defaults on, can be opted out, and claims one task while idle; personal Add takes priority. The server records authenticated contributor provenance and validation outcomes. Administrators can independently set paid status, block contributions, and suspend/restore accounts with audited reasons. A parsing error alone does not establish abuse. Contribution blocking can preserve shopping access; account suspension revokes sessions. Paid status is manually managed, not an implemented billing integration.

## Lists, prices and refresh

- Registered free accounts: 2 active lists. Paid accounts: 5 active lists. Paid users can manually archive and view `/history`; free users delete and cannot access history.
- Nonempty lists with all items purchased or hidden automatically archive, including temporary trials. Deleted lists are not available through user history. Empty lists are not automatically archived merely for being empty.
- Automatically created names use the user's date and daily sequence, e.g. `Shopping_List_07_10_2026_01`.
- Customer extension repeated Add uses the new quantity rather than incrementing or silently retaining the old quantity. Check the precise import entry point when modifying duplicate handling; older README text describing quantity preservation is not universally current.
- `PriceRefreshWorker` / `PriceRefreshService` refresh stale known mappings for items **still to buy**: not purchased/hidden, in eligible active lists/accounts. Defaults: scan every 15 minutes, failed-refresh cooldown 6 hours. Queue entries deduplicate shared products and preserve higher-priority work. A recent unavailable observation is still a completed check, avoiding loops.
- Prices are observed retailer-page prices, with store/location uncertainty retained. Unknown price is not proof a retailer does not sell a product. Do not fabricate zero prices.
- `PromotionCalculationService` separates single-item prices from verified multibuy totals, computes complete bundles plus leftover singles, and compares quantity totals. Example: verified 2 for $30 with $30 single price gives quantity 1 = $30, 2 = $30, 3 = $60. Membership eligibility and mixed-product baskets are not inferred. Stale or unverified conditional prices do not become trustworthy cheapest totals.

## Latest five improvements

1. **Saved links:** fresh cache first, then direct verified retailer URL refresh; fallback searching and identity checks retained.
2. **Promotions:** shared backend quantity calculation used in planning and in-store totals; deal wording remains visible so shoppers understand conditions.
3. **Shared workload controls:** default maximum 2 concurrent tasks per retailer; 3 blocked reports within 10 minutes pause that retailer for 15 minutes; recovery allows one trial. Persisted `RetailerWorkloadState` coordinates workers. This does not bypass retailer challenges or guarantee live access.
4. **Regression collection:** backend `tests/myshoppinglist-api.Tests/Fixtures/Regression/products.json` and frontend companion browser fixtures. Read the adjacent `README.md` for provenance and commands. Includes troublesome punctuation, navigation, flavours, sizes, multipacks, suggestions, blocked pages and promotions. Reconstructed examples are labelled; they are not all live captures.
5. **Administrator health:** `Contracts/AdminHealthContracts.cs`, `Services/AdminHealthService.cs`, `Endpoints/AdminHealthEndpoints.cs`, registered in `Program.cs`, tested in `AdminHealthTests.cs`. Administrator-only, read-only, no-store, consistent database snapshot. Shows waiting/processing/failed counts, expired leases, oldest outstanding creation time, latest retained 24-hour outcomes, retained blocking reports, pause/trial state and top current errors. Reading it never claims or recovers work.

Health statistics deliberately have limits: task age is from creation (including reused tasks), outcomes are each retained task's latest result rather than complete attempt history, completed searches may have no match, and personal Add comparison tasks are outside the shared queue view. It is not a complete historical analytics system.

## Validation evidence and commands

These are previous execution results, **not rerun during documentation work**:

- Latest health work: 2 PostgreSQL/backend health tests and 12 desktop/mobile administrator browser tests passed; TypeScript, ESLint and diff checks passed; changed code formatted.
- Earlier regression milestone: 2,315 backend tests and 139 extension tests reported passing. This is a historical run, not a claim that every suite was rerun after health changes.
- Saved-link, promotion and workload features had targeted checks in their respective stages. Real retailer smoke tests remain distinct from fixtures/mocks.

Useful commands (from this backend directory):

```powershell
dotnet build myshoppinglist-api.slnx
dotnet test tests/myshoppinglist-api.Tests/myshoppinglist-api.Tests.csproj --output "$env:TEMP/myshoppinglist-tests"
dotnet test tests/myshoppinglist-api.Tests/myshoppinglist-api.Tests.csproj --output "$env:TEMP/myshoppinglist-health-tests" --filter "FullyQualifiedName~AdminHealthTests"
dotnet run --project myshoppinglist-api.csproj --launch-profile http
```

Database tests require `MYSHOPPINGLIST_TEST_CONNECTION` configured securely. They may skip without it. Use the existing `IsolatedDatabaseScope` and nonparallel `Import worker database` collection for isolated database tests; other older fixtures have their own setup. Never aim destructive test setup at customer data. A temporary build output avoids locking binaries of a running API. Do not start a duplicate API or stop the user's debugger without coordinating.

## Remaining checks / future work

- Verify latest API is running, open health as administrator, and observe real queue completion, pause and recovery. Do not assume a reported restart occurred.
- Run a small live Coles/Woolworths smoke test across website Add, customer extension Add, cached mappings, stale refresh and promotion quantities. Retailer layouts/access can change despite fixture coverage.
- Check saved mappings/refresh speed under real load; workload limits intentionally trade throughput for reliability.
- Production deployment and Chrome Web Store publishing are not done: HTTPS origins, extension IDs, permissions, privacy disclosures, packaging, secrets, migrations and production authentication need a reviewed plan.
- Payment processing, paid analytics, full historical health metrics, email verification/password recovery and additional retailers are not established as completed here. Inspect code before planning them.
- Existing root README is a historical stage log and contains superseded statements (missing list management, old duplicate semantics, older transport descriptions). Use current code and this handover, not those statements alone.

## New-chat starter

> Continue MyShoppingList. Read D:/pra/myshoppinglist-api/HANDOVER.md and D:/pra/myshoppinglist/HANDOVER.md, plus applicable instructions. First verify current Git/runtime state and summarize what remains. Do not edit files until I approve an exact file-by-file plan. Port 3000 is mine; use 3001 only if free. The latest completed feature was administrator retailer health; start by checking its integration and discussing the next priority with me.
