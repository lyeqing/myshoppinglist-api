# MyShoppingList API

Standalone ASP.NET Core 10 / EF Core / PostgreSQL backend following HandyTool conventions. Stage 1 provides the database foundation only; authentication endpoints, retailer providers, job processing, and the frontend will follow separately.

## Local setup

Requires .NET SDK 10, EF CLI 10.0.11, and PostgreSQL. The database name is exactly `MyShoppingList` (case-sensitive). Supply `ConnectionStrings__MyShoppingList` through the environment or an ignored `appsettings.Local.json`:

```json
{
  "ConnectionStrings": {
    "MyShoppingList": "Host=localhost;Port=5432;Database=MyShoppingList;Username=YOUR_USER;Password=YOUR_PASSWORD"
  }
}
```

Do not commit credentials. The local configuration is loaded before environment overrides. The runtime does not create or migrate databases automatically.

```powershell
dotnet restore myshoppinglist-api.slnx
dotnet ef database update --project myshoppinglist-api.csproj
dotnet run --project myshoppinglist-api.csproj --launch-profile http
```

EF creates the named database if absent when the configured PostgreSQL role has permission. The API listens on `http://localhost:5392`; development Swagger is at `/swagger`. This stage exposes only a liveness response, not shopping-list or import endpoints.

## Database design

- Shared `Products`, `Shops`, `ShopLocations`, and `ShopProducts` contain catalogue data, independent of account ownership. All five initial shops are seeded; a shop record does not mean a provider is implemented.
- `ShoppingListProducts` references a canonical product, with one row per list/product. The later service must explicitly choose quantity behaviour for repeat submissions.
- Current prices are unique by retailer mapping, optional location, scope, and currency. Separate filtered indexes enforce uniqueness when location is NULL. History preserves currency, promotion details, source, and check time. Amounts use `numeric(18,4)`; timestamps use PostgreSQL `timestamp with time zone` and must be supplied in UTC.
- GTIN is indexed but not unique. Future deterministic canonical resolution must handle concurrent imports and contradictory metadata rather than merging by title alone.
- Registered users require credentials. Trial accounts have no credentials and require expiry. Sessions store token hashes only; token issuance, authentication, and expiry enforcement are not implemented in this stage.
- `ProductImportJobs` persists quantity, ownership, URL, progress, retry scheduling, and claim lease information. Composite foreign keys enforce matching list ownership and resulting list-item identity.
- `ProductImportRetailerResults` preserves per-shop progress and failures even without a mapping or price. Each job has at most one result per shop.

## Background processing contract for the next stage

The database is the durable queue. A future `BackgroundService` must claim a queued job atomically, assigning a fresh `ClaimToken` and lease with its status transition. `Status` and `ClaimToken` are EF concurrency tokens; stale writes fail instead of silently overwriting a newer claim. Lease renewal and recovery must also use guarded writes. Child-result and catalogue writes must be fenced inside a transaction that verifies the current claim; a parent concurrency token alone cannot protect child-table writes.

The worker must recheck account/list access and expiry, validate outbound URLs at connection and redirect time, use separate scopes/contexts for concurrent retailer operations, and commit source results before comparisons finish. Restart recovery and retry processing are future implementation, not functionality supplied merely by these tables. Polling will expose partial results at approximately two-second intervals.

Later services must validate that a price's location belongs to its retailer, normalise email and GTIN, implement retry idempotency, and determine history insertion frequency. Business operations are not exposed yet.

## Verification

```powershell
dotnet test myshoppinglist-api.slnx
dotnet ef migrations has-pending-model-changes --project myshoppinglist-api.csproj
```

Model tests run without a database. Set `MYSHOPPINGLIST_TEST_CONNECTION` to a migrated PostgreSQL database to also run relational constraint and optimistic-concurrency tests. Each database test uses a transaction and rolls back its records; PostgreSQL identity sequences may still advance. Tests never drop or recreate the database. Without the environment variable, relational tests are explicitly reported as skipped.
