# Permanent retailer regression collection

Run these checks whenever retailer extraction, product matching, search handling or promotion calculations change.

## Provenance and limits

`products.json` contains reduced, reconstructed examples based on reported failures and supported page structures. They are **not captured live pages**. Prices are fixed test values, not current retailer prices. Product names and some URLs come from reported examples; other identifiers are synthetic. No customer data, cookies, account state or credentials are included.

Each case has a stable `id`, a `reason`, `provenance`, input evidence and explicit expected results. Existing captured fixtures remain in `Fixtures/Coles` and `Fixtures/Woolworths` with their own provenance.

The companion browser collection is `D:/pra/myshoppinglist/extensions/coles-worker/tests/fixtures/regression.json`. Related product cases share IDs. Its runner exercises **both** the worker and customer extension readers, including search DOM shapes and Woolworths shadow roots. The two input formats are deliberately separate: backend tests accept HTML and product identities; browser tests accept script records and reduced DOM descriptions. When changing shared evidence, update both collections and run both suites.

## Coverage

- Product identity and single-price extraction: YoPro, McCain, Red Island, Hans, Cadbury, Pickers, Banana Boat, Four'n Twenty and Coca-Cola.
- URL punctuation: literal plus, apostrophe and decimal size.
- Layouts: JSON-LD, embedded application state and stale product data after navigation.
- Failure handling: blocked pages, loading shells and identified products without a price.
- Matching: flavours, blends, pouch versus tub, brands, unknown sizes, multipacks and equivalent units. Matching is checked in both directions.
- Promotions: verified complete bundles and leftovers; stale, member-only and ambiguous offers remain excluded.
- Browser search: multiple sizes, duplicate links, Coles suggestions, Woolworths shadow-root tiles, foreign links and confirmed empty results. Candidates are not proof of a matching product.

## Run

From `D:/pra/myshoppinglist-api`:

```powershell
dotnet test tests/myshoppinglist-api.Tests/myshoppinglist-api.Tests.csproj --filter "FullyQualifiedName~RetailerRegressionCollectionTests|FullyQualifiedName~ProductMatching|FullyQualifiedName~ProductParserTests|FullyQualifiedName~ProductSearchQueryBuilderTests|FullyQualifiedName~RetailerSearchParserTests"
```

If a running API locks the build output, add `--output "$env:TEMP/myshoppinglist-regression-tests"`.

From `D:/pra/myshoppinglist`:

```powershell
npm run test:regression
```

These tests require neither a database nor live retailer access. The backend collection also runs during ordinary `dotnet test`. The npm command runs the existing worker and customer extension suites as well as the saved collection. Live retailer smoke tests remain separately opt-in; they do not replace these deterministic checks.

## Add a reported failure

1. Choose a descriptive, unique ID. State the observed failure and whether evidence was captured, reduced or reconstructed.
2. Remove account details, cookies, tokens, tracking parameters and unrelated page state. Keep only evidence needed to reproduce the behaviour.
3. Add explicit expectations: product code, price or error; matching decision; promotion total; or search candidates. Include a nearby wrong flavour/size where relevant.
4. Add equivalent browser evidence when the failure involves extension extraction. Preserve shared IDs for related cases.
5. Run both suites. Investigate any failure against the requirement; do not change an expected result merely to match current implementation.
6. Obtain approval for any production fix outside the approved file list. Retain the regression case when fixing the bug.
