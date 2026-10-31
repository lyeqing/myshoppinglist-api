# Security audit

Reviewed 1 November 2026. Scope: the API repository and sibling `myshoppinglist` website/customer extension. This is a source and local-test assessment, not a penetration-test certification or production-host inspection. No deployed host, Cloudflare account, email provider or production backup service is configured in the reviewed repositories. Existing implementation detail remains in [AI connections](AI_CONNECTIONS.md), [operations](OPERATIONS.md) and [release readiness](RELEASE_READINESS.md).

## Findings and status

| Severity | Finding and evidence | Correction / status |
|---|---|---|
| Critical | Website Next.js 16.3.1 is covered by current npm remote-code-execution advisories, including Windows server and `next/og` paths. The project runs on Windows and has an ImageResponse route. No exploit was attempted. | Updated Next.js and matching ESLint configuration to 16.4.3; refreshed lockfile. Build passes. See testing document for advisory and browser results. |
| High | Production dependency tree included vulnerable `sharp` and `source-map-js`. | Updated to sharp 0.35.5 and source-map-js 1.2.2; no findings remain in the production audit and no high or critical findings remain in the full npm audit. |
| Medium | API default body limit was the server default; many routes had no smaller bound. Capture limits existed as metadata but had no independent streaming enforcement. | Added 16 KiB default, 64 KiB MCP bound, and streaming enforcement including unknown-length bodies. Preserves existing explicit screenshot/capture limits. Oversize receives 413. |
| Medium | Login/import/MCP limits existed, but ordinary list read/write endpoints had no general abuse budget. | Added account-partitioned global API/MCP limit, default 300/minute, plus pre-authentication API ingress limit of 1200/minute per peer. Existing narrower policies remain. Peer partitions never trust supplied forwarded headers. |
| Medium | Website had no configured CSP, frame protection or content-type protection. | Nonce-based script CSP, blocked objects/frames/base changes, no-referrer, nosniff, permissions restrictions. HTTPS HSTS when public HTTPS origin is configured. Nonce pages render dynamically and are not shared-cacheable. |
| Medium | Operational data had generic rejected requests but no grouped authentication/permission/throttling incident rules. | Added fixed sign-in category and configurable five-minute failure threshold, durable deduplication and existing cooldown/recovery handling. Delivery remains explicitly simulated. |
| Low | A JSON null element in an extension search-result array passed the array check and caused a server exception instead of a validation response. | Explicitly reject null candidates with 400 before processing/completion; extended the owned-device search regression. |
| Medium | No password recovery/change or email verification flow exists. | Open: implement verified account recovery with a selected email delivery service, hashed one-use expiring reset capabilities, generic responses, account/IP throttling and session/OAuth/device revocation after reset. Do not substitute an unauthenticated password replacement route. |
| Medium | Login limits see the Next.js gateway peer, so customers behind it share a per-IP login budget. In-memory budgets are per process. | Open deployment configuration: exact trusted gateway/proxy chain, controlled client-IP forwarding and multi-instance abuse budgets. Never globally trust X-Forwarded-For. Authenticated general API budgets now separate accounts even behind that gateway. |
| Medium | Infrastructure backups, public database reachability, least-privilege grants, production HTTPS and WAF cannot be verified from local source. | Deployment checklist in architecture document; existing disposable backup/restore rehearsal tool retained. No infrastructure changes made. |
| Medium | Incident delivery has no live email transport and cannot notify during a database outage from that database. | Existing simulated outbox is truthful; choose/configure a transport and independently hosted uptime probe before launch. No mail was sent. |
| Low | Full npm audit retains moderate minimatch-related development lint dependency findings. | Documented; compatible fixes applied. Audit suggests destructive tool downgrades; no unsupported major override or forced downgrade applied. Production dependency audit must remain clean. |
| Low | Customer extension intentionally reads this application's session cookie via Chrome's cookies API, using localhost host permission. | Retained to preserve working first-party login integration. Token stays in extension session storage, never page messages/localStorage. This is an application credential, not retailer cookies. Production origin/extension allowlists still require deployment configuration. |

## Existing measures verified by source inspection

- Password PBKDF2 hashing with random salt and timing-resistant verification; generic sign-in failures and dummy unknown-account verification; bounded password input. Tokens generated cryptographically, hashed in PostgreSQL; expiry, inactive accounts and revocation checked on each authenticated request.
- Browser cookies HttpOnly, SameSite=Lax, Secure outside development; trial conversion revokes old sessions and rotates credentials. Native and extension bearer transports are distinct from browser cookie responses. No authentication token found in website localStorage.
- Custom-header CSRF protection and same-origin/fetch-site checks on mutations, including anonymous session creation. No permissive CORS middleware. Gateway forwards only allowlisted API routes and selected headers, bounds request bodies and upstream time, rejects redirects and uses no-store.
- Backend list, item, product-library, capture and preference services constrain resources by authenticated account. Administrator endpoints require the explicit server account-ID allowlist. Trials do not become administrators. Existing cross-account isolation tests exercise these boundaries.
- EF LINQ, interpolated parameterized SQL and explicitly validated identifiers; no untrusted SQL string concatenation identified in reviewed customer operations.
- Retailer URL allowlist, HTTPS, no arbitrary ports/credentials; HTTP transport rechecks resolved public IPs, pins numeric connection destinations, disables proxy/cookies/automatic redirects, enforces response/time limits. Browser search uses its own network guard.
- Existing OAuth MCP uses PKCE S256, explicit client redirects, resource audience validation, narrow scopes, database-backed token/authorization revocation, per-client user consent and hashed/idempotent mutation receipts. Ordinary session tokens cannot act as MCP OAuth credentials.
- Customer extension bridge has per-user/per-session devices, hashed credentials and claims, fixed search/capture operations, explicit local approval, expiring tasks, replay/conflicting-completion protection and validated retailer results. It does not expose arbitrary JavaScript/OS tools or cross-user sharing.
- Structured Serilog correlation and MCP audit categories; private support data is access controlled. Local secret configuration is ignored and excluded from publication. Audit checked tracked filenames/configuration, not every historical Git revision; historical secret scanning/rotation remains a deployment responsibility.

## Phase delivery

1. Audit / critical fixes: inspected both repositories and actual dependency advisories; patched confirmed vulnerable dependencies. No new ownership bypass was reproduced.
2. Hardening: request bounds, global throttling, headers/CSP, grouped incident rules, tests and new CI workflows implemented. Production email/recovery/infrastructure remain open above.
3. MCP / extension: reviewed and preserved the existing implementation; existing tests cover protocol, scopes, isolation, approvals and replay. No redundant second MCP server or broader extension permissions added.
4. WebMCP: official current sources evaluated; production integration deferred with a future prototype design. See [evaluation](WEBMCP_EVALUATION.md).

Detailed test commands/results and any failures are maintained in [security testing](SECURITY_TESTING.md). No database schema migration was introduced by this hardening work.

## Advisory references

- [Next.js Windows server RCE](https://github.com/advisories/GHSA-p293-qw3h-jr36)
- [Next.js ImageResponse RCE](https://github.com/advisories/GHSA-vcvr-r3jv-pc5j)
- [Next.js image optimization RCE](https://github.com/advisories/GHSA-2464-fhr4-w9xw)
- [sharp librsvg advisory](https://github.com/advisories/GHSA-wq5f-xc86-pv6w)
- [source-map-js denial of service](https://github.com/advisories/GHSA-68fv-2mgg-jv7q)
- [Development minimatch advisory](https://github.com/advisories/GHSA-g3hv-75wg-m249)
