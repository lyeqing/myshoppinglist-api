# Security architecture

## Trust boundaries

Browser → same-origin Next.js allowlisted gateway → .NET API → PostgreSQL.

Remote AI → OAuth-protected `/mcp` → existing account-scoped backend services → owned extension task queue → authenticated customer extension → supported retailer.

The website is not an authorization boundary. Every list/product/device operation must retain its backend ownership condition. Account IDs in bodies detect stale account context; they never establish identity. Unknown or unavailable owned resources return the existing not-found/conflict results without disclosing another account's content.

The local customer extension has an intentional cookies permission to reuse **only MyShoppingList's** website session. It does not receive retailer cookies/passwords. Retailer pages and returned descriptions are data, never instructions. A separate legacy Coles worker uses an operator credential and is not a customer-shared browser service; keep legacy shared collection disabled for personal-mode deployments.

## Request controls

`RequestSecurity:DefaultBodyBytes` defaults to 16384 (valid 1024–65536). MCP allows 65536 bytes. Existing endpoint metadata explicitly permits capture evidence up to 2500000 bytes, reviewed search results up to 20000 and support submissions up to 750000. Both declared length and actual streamed length are enforced before binding completes. Request streams remain streaming; no whole-body buffering was added.

`RequestSecurity:RequestsPerMinute` defaults to 300 (valid 1–10000), partitioned by authenticated account or anonymous actual peer IP. A separate `RequestSecurity:IngressRequestsPerMinute` defaults to 1200 (valid 1–60000), per network peer, before API authentication/database lookups and authorization short-circuits. Existing authentication, contribution, import, device and pre-authentication MCP protocol policies still apply. Limits are process-local; they do not replace a gateway/WAF or distributed protection. Health probes are outside these limiters. Behind a gateway, the ingress budget is shared by its clients and must be tuned/verified for that deployment.

Mutation CSRF protection requires `X-MyShoppingList-Request: 1`, checks browser origin/fetch-site, and keeps existing authenticated bearer exemptions. No credentialed cross-origin website API access is enabled. Do not add wildcard CORS to repair a deployment-origin error.

API responses use nosniff, DENY and no-referrer; API/MCP/OAuth responses are no-store. Problem responses include a server correlation `traceId`; server failures use generic titles/details. Existing Serilog correlation remains the investigation key. Never add request bodies, cookies, Authorization headers, raw query strings or retailer page contents to logs.

The website shows safe reference IDs for serious server errors. Gateway-generated failures have a random reference and a structured log containing only fixed event name, HTTP status and reference; upstream exception text is not shown. The client bounds reference syntax/length and never displays private internal 5xx details.

## Website policy

Next.js `src/proxy.ts` overwrites supplied nonce/CSP headers and generates a new random nonce per document request. Framework and JSON-LD scripts receive it. Production scripts require the nonce; development alone allows eval. Styles permit inline styling for framework compatibility; image sources permit HTTPS/data/blob for product images and previews. Connections are same-origin, with local HMR WebSockets in development. Objects, frames and base elements are blocked. Camera, microphone and geolocation permissions are disabled.

Nonces require dynamic rendering and private/no-store HTML, including public pages. Static JS/CSS, sitemap, robots and generated share image retain their normal asset behavior. This trades shared HTML caching for stronger script-injection protection; test actual-host latency before deployment. Do not cache HTML and reuse its nonce. If public-page caching is later required, implement build-generated script hashes with equivalent browser verification.

When `MYSHOPPINGLIST_PUBLIC_ORIGIN` starts with HTTPS, website responses use one-year HSTS and CSP upgrades insecure requests. The API uses production HSTS/HTTPS redirection. Neither code change proves TLS terminates correctly on a real host. The gateway must stay reachable only through reviewed deployment paths and private API URL configuration.

## Monitoring

Existing operations are enabled explicitly with `Operations:Enabled=true`; delivery stays simulated. Sign-in failures have a fixed category. `Operations:SecurityFailuresPerFiveMinutes` defaults to 50 (valid 10–600), grouping HTTP 401/403/429 into separate incidents. A window with no sampled security rejections resolves those incidents; intermediate counts or activity in another rejection category preserve open incidents. Shared durable incident/outbox logic deduplicates transitions and applies the existing 30-minute reopening cooldown. These are volume signals, not proof of an attack. Ordinary errors do not generate one email each.

Security-rejection persistence is capped at 120 events per minute across each process, including ingress rejections. Those counts are sampled lower bounds during an attack, not exact request volumes. This prevents telemetry from bypassing ingress protection with unlimited database writes. Ordinary success/failure events keep existing recording behavior. Thresholds above the sample budget require multi-minute sustained observations; use independent ingress metrics for exact attack volume.

Existing [operations runbook](OPERATIONS.md) describes retention, independent uptime placement, safe exports and backup/restore. Live notification credentials/provider are absent. Do not label `simulated` deliveries as sent. Database outages require the independent probe outside the application/database failure domain.

## Deployment settings still required

1. Select the actual HTTPS website/API origins, deployment host and DNS ownership. Configure exact AllowedHosts, firewall ingress and TLS. Restrict direct API access to the gateway plus the deliberately public OAuth/MCP routes as applicable.
2. Set exact trusted reverse-proxy addresses/hops. Have only trusted ingress replace incoming forwarded headers. Validate different customers receive separate sign-in budgets; never clear known-proxy restrictions to trust the internet.
3. Configure persistent OAuth signing/encryption keys, explicit AI clients/callbacks, exact extension IDs and production host permissions. Keep private keys outside source and published artifacts; protect/rotate backups of keys.
4. Bind PostgreSQL to private interfaces; firewall public access; use TLS where appropriate and separate migration/runtime credentials. Runtime roles must not own schemas or have superuser/createdb/createrole privileges. Review grants against the deployed schema before removing privileges.
5. Configure Cloudflare proxying/WAF/rate rules if selected, protect the origin from bypass and review realistic request/capture limits. No Cloudflare zone/account settings were available to verify or change.
6. Select a protected backup schedule, off-host encrypted retention, recovery objectives and an independent restoration target. Run the existing restore rehearsal and real workflow checks before cutover. Local fixture recovery does not verify a production backup schedule.
7. Select an email delivery service and verified sender, protect credentials, test delivery/retry/cooldown independently, then activate critical notifications and secure recovery. No outbound messages are sent as part of this work.

No infrastructure or customer-data migration was required for these source changes. Production compatibility and live activation remain deployment work.
