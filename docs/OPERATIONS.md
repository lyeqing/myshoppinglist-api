# Operations, private support and local monitoring

This is a local implementation and deployment runbook, not a production activation. `Operations:Enabled` defaults to false. The existing administrator policy and account audit service remain the authorization boundary. AI/MCP has no administrator tool or access to these routes.

## Enable only in an explicitly selected environment

Apply the additive `20261031043409_AddOperationsAndPrivateFeedback` migration to a disposable target for verification. Configure administrator account IDs in `Admin:AccountIds`; registration never grants administrator status. Never identify administrators by display name or email matching. The website's `/superadmin` and existing `/admin` routes verify the first-party session on the server and inherit noindex/no-referrer metadata; backend administrator policy applies independently to every data route. Client caches are cleared on account changes and late account responses are fenced.

Example placeholders (private recipients belong in ignored local settings/environment):

```json
{
  "Admin": { "AccountIds": [123] },
  "Operations": {
    "Enabled": true,
    "SimulateDelivery": true,
    "IncidentRecipients": ["owner@example.test"],
    "Release": "local-test",
    "RawRetentionDays": 30,
    "AggregateRetentionMonths": 12
  }
}
```

The owner supplied a recipient privately. No email/monitoring provider, spending budget or live delivery credentials have been selected. This implementation contains **no live email transport** and rejects `SimulateDelivery=false`. Simulation results are labelled `simulated`, `suppressed` or `not_configured`, never `sent`. A configured address is not evidence of delivered email. Public/private settings do not enable Stripe or production payments.

Before deployment: review persistent OAuth keys, exact client/extension allowlists, HTTPS, known reverse proxies/forwarded addresses, database privileges, rate limits, backups, monitoring placement, storage encryption and provider configuration. Do not trust arbitrary forwarded headers. With a gateway/proxy, per-IP limits need deployment validation so unrelated anonymous customers do not share the proxy's partition. Default-off features are not a production rollout.

## Administrator and support workflow

- `/superadmin` presents registered/trial/free/paid-or-complimentary counts, observed activity, saved-item activation, consented visitor/session/view estimates, fixed-name API/MCP outcomes, queue/outcome/version/latency measurements, incidents and simulation delivery history. Current successful database reads do not prove external availability. MCP tool results are measured separately from HTTP authorization/transport failures; HTTP 200 alone does not mean tool success.
- Account management reuses `/admin`: email search, trial/free/paid/suspended filters, sign-in/contact and saved-item times, versioned updates, a required reason and an explicit confirmation before account changes. Suspension revokes sessions; restoration requires a new sign-in. Existing manual `IsPaid` remains a complimentary/legacy entitlement flag, not proof of subscription revenue.
- Customer Support includes private feedback, optional rating and an explicitly chosen/approved screenshot. The browser redraws images into PNG, removing metadata and resizing before upload. The backend bounds PNG dimensions/size and serves authenticated attachments with `nosniff`; it never serves uploaded markup. Screenshots/messages are not logged or placed in analytics. Other accounts cannot read tickets/images. Native extension feedback uses the same service and source session, without collecting unrelated browsing or auto-capturing the screen.
- The inbox supports new/triaged/planned/resolved/closed, optimistic versions, internal issue references/reasons and in-ticket customer responses. Internal references/reasons are not returned to customers. Reviews, screenshot access, dashboard/journey views and simulations have administrator audit entries. Responses are stored for the customer to read; no email is sent.
- The owner selected **support-reviewed account export/deletion requests**. Support offers explicit draft controls, then requires the customer to send the private request. No automatic deletion occurs. Verify ownership through the active account, scope and requested action; explain applicable retention and delivery; record review/decision in the ticket. Never request passwords or tokens. Verify any external provider records and retained historical/global relationships before fulfilment. Production deletion and record-retention decisions need a separate reviewed procedure; do not improvise cascading SQL against real data.

## Support export preparation and recovery rehearsal

`tools/operations/export_account.py` prepares a read-only, repeatable-read account snapshot using PG credentials from the environment. It validates schema/account input, selects direct account-owned rows plus explicit list/import/brand children, and removes password/token/claim/hash/salt/authorization/installation/internal-review fields. OAuth token tables are never exported. It refuses absent accounts, query failures and existing output files. It prints only a manifest (path, digest, counts), not private content.

This is a **support preparation artifact**, not automatic legal fulfilment or public download: review historical shared-catalog references, staff-only security records, anonymous consent identifiers and external providers separately. Use a restricted encrypted directory, an approved secure delivery channel, a case/audit record, and an explicit cleanup/retention decision. Never commit exports or email them from local tests. Customer deletion remains support-reviewed; a request does not revoke access or delete data by itself.

```powershell
python tools/operations/export_account.py --psql D:/PostgreSQL/bin/psql.exe --schema public --account-id 123 --output .local-backups/reviewed-case-export.json
```

`verify_backup_restore.ps1` is deliberately restricted to `127.0.0.1:55432` and an explicitly named test database. It creates a unique source schema, applies migrations there, seeds two disposable accounts/support data, tests one-account export/redaction, creates a custom-format `pg_dump`, restores into a new uniquely named disposable database, compares every table fingerprint, and removes only its own database/schema. Dumps and fixture exports remain in ignored `.local-backups` for evidence. No application/public data is changed.

```powershell
# Set explicit matching PG* and MYSHOPPINGLIST_TEST_CONNECTION variables first.
./tools/operations/verify_backup_restore.ps1 -PostgresBin D:/PostgreSQL/bin
```

For a real recovery: take a fresh protected backup; restore to a separate restricted environment; compare migration history, ownership keys, grants, task/receipt history and counts; validate sign-in, lists, private capture, feedback and revocation; keep collection and provider sending disabled; obtain deployment/cutover approval. Never overwrite the live database as a rehearsal. Backup encryption, off-host copies, recovery objectives and actual production schedules remain deployment configuration, not verified local facts.

## Privacy, sampling and retention

Visitor analytics defaults to no collection until optional consent. First-party browser storage holds a random visitor identifier for at most 30 days and a separate browser-session identifier; events contain fixed public-page/action names and a strictly limited campaign-source slug. No raw referrer/URL/query, personal product/list contents or unrelated browser history is accepted. Declining/withdrawing deletes retained raw events for the known visitor capability. Aggregate daily totals cannot be reidentified/deleted individually. Without browser storage, telemetry is omitted rather than making shopping fail.

Visitor/session/view counts are different. Multiple profiles/devices can count separately, and nonconsenting visitors are absent. Report estimates, never exact people. Daily distinct counts must not be added and called monthly unique visitors. Signup events reported by the browser are estimates; registered-account and saved-item counts come from owned persisted data. Attributed funnel reports are limited by consent, known source and retained raw history; no invasive cross-device identity matching occurs.

Operational events use fixed component/action/outcome/failure categories, server time, release, bounded latency/status and optional authenticated account/trace/client metadata. Website errors/timeouts are reported as fixed categories with one event per category per minute; no exception text, stack, page URL or form values is sent. Anonymous diagnostics contain no user identifier. Ordinary accepted operational events are recorded (sampling 1:1); HTTP 401/403/429 security rejections are sampled at at most 120 records per minute per process to bound logging load, so security counts are lower bounds during a flood; diagnostics/analytics are rate limited and browser error reports are untrusted reports. Backend exceptions retain the existing safe correlation pipeline. If recording fails, a fixed warning is logged and the customer operation retains its original result; loss of telemetry is not silently called health.

Every minute the operations worker evaluates bounded incident rules and simulation delivery. Hourly it transactionally rebuilds closed UTC daily summaries, then deletes raw operational/visitor events older than 30 days and summaries older than 12 months. Summary rows contain counts/duration sums, no account/visitor/session identifiers. Shutdown is cancellable; unavailable storage leaves durable queued rows for retry. Feedback, account records and task histories are customer/support records, not visitor events; their later deletion is handled through the reviewed support process. Existing legacy logs/backups also need deployment retention review.

## Incident rules and independent uptime

- P0: externally observed application or database readiness outage. Administrator simulations use a separate `simulation:*` fingerprint, create one durable open notification and one recovery per cycle, and never send mail.
- P1: API 5xx strictly above 5%, at least 100 samples spanning a five-minute window; capture failures strictly above 20%, at least 20 completed/failed capture attempts across at least three accounts spanning 15 minutes. Cancelled/declined requests are not technical failures. Insufficient samples are unknown, not recovery. Payment/webhook rules belong to billing groundwork.
- P2: fixed-category isolated failures are visible on the dashboard; they do not generate immediate notifications. No scheduled digest email is configured.
- Repeated open observations are deduplicated. Reopening has a 30-minute notification cooldown; suppressed opens do not produce misleading recovery notifications. Open/recovery rows have unique incident-version-kind keys. Simulation delivery records attempts/outcome/recipient count without exposing addresses.

Run `uptime_probe.py` **outside the application host** to detect an unavailable API/database independently. It probes only `/health/live` and `/health/ready`, bounds time/body size, rejects redirects as success, and uses a local SQLite state/outbox independent from PostgreSQL. It accepts configured HTTPS origins; explicit loopback HTTP is test-only. A failed app check leaves database state unknown rather than claiming a separate DB failure/recovery. The application cannot notify from its database if that database is down; independent placement is essential.

```powershell
python tools/operations/uptime_probe.py --base-url http://localhost:5392 --allow-loopback-http --simulate down
python tools/operations/uptime_probe.py --base-url http://localhost:5392 --allow-loopback-http --simulate healthy
python -m unittest discover -s tools/operations -p 'test_*.py'
```

Schedule a bounded invocation (for example once per minute) on an independently managed host after its configuration is reviewed. No scheduler/service is created by this implementation. Keep state storage protected and durable. `MYSHOPPINGLIST_INCIDENT_RECIPIENTS` supplies private simulation recipient configuration, but there is no SMTP/provider send. Connecting a provider later requires separate credentials, test delivery/retry verification, budget and explicit live activation. Do not describe the current local simulations as delivered production alerts.

## Rollback

Set `Operations:Enabled=false` to stop support/analytics/admin operations routes, collection and maintenance; existing account administration and basic readiness/session gates remain. Preserve data/history. The additive migration locks all six new tables and refuses rollback if any contains feedback, events, summaries, audits, incidents or deliveries. Do not erase data to make downgrade succeed. Production migration/provider activation remains separately approved.
