# MCP security

This project already implements remote MCP. Reuse `Mcp/AiIntegrationSetup.cs`, `AiOAuthEndpoints.cs`, `AiIntegrationEndpoints.cs` and the existing tools, rather than introducing another server. See [AI connections](AI_CONNECTIONS.md) for setup and tool contracts.

## Identity and authorization

- OAuth authorization code flow requires PKCE S256 and exact registered callback URLs. No open dynamic client registration or arbitrary metadata fetching was introduced.
- Tokens are short-lived reference tokens with a matching configured resource audience. Validation checks token and authorization entries on requests. Refresh tokens have bounded lifetime and existing rotation/revocation handling.
- The backend rechecks active registered account, allowed presenter/client and unrevoked account-owned connection. A first-party session cookie/bearer token is not an MCP access token. No AI-provided account identifier establishes identity.
- `McpAccount.Require` requires one authenticated identity, one valid account claim and the exact tool scope. Read and write permissions remain separate. Write tools are registered only when configured scopes permit them.
- Services enforce owned list/item/product conditions, optimistic versions and UUID mutation receipts. Retried identical commands are idempotent; changed retries/conflicts do not silently overwrite newer edits.
- Users review client/scopes on the website and can revoke the connection immediately. Tool annotations describe effects but do not replace backend permissions or user consent. Destructive list operations require the existing explicit confirmation/version contracts; browser capture/search also requires local approval.

## Extension boundary

AI tools enqueue only typed search/capture requests for the authenticated user's device queue. The user approves an operation in their own extension before page reading, then reviews results before saving. Offline devices return the existing reconnect/unavailable guidance. No arbitrary script, cookie extraction, shell or browser-control tool is exposed.

Devices are bound to account, originating session and configured extension ID. Authentication uses a hashed device credential and a separate device scheme; session expiry, revocation and inactive accounts invalidate access. Queue claims include a random claim capability, device/account checks, bounded task lifetime and state transitions. Another user/device cannot claim or complete the task. Identical completion replay returns the stored result; conflicting replay is rejected rather than executing again.

Retailer URLs/results are validated by supported host/path, result count/length and observation time. Search output explicitly reports candidate links rather than verified prices. Description strings remain untrusted content, not commands; fixed tool methods and backend authorization decide effects. Do not interpret instructions embedded in results or relay OAuth/device/session tokens to retailers or other services.

## Transport and operational controls

Existing protocol protection validates MCP host/origin and throttles protocol traffic before OAuth handlers can complete it. General request hardening adds the 64 KiB MCP body bound and account-level budget. Calls retain cancellation/time limits and existing retailer budgets. MCP tool audit logs fixed operation/outcome categories with safe correlation, excluding raw arguments/products/tokens. Operations middleware separately measures authentication/transport rejections.

Deployment requires configured HTTPS issuer/resource, persistent signing/encryption certificates, reviewed clients/scopes and exact browser/extension origins. Default-off AI/bridge configuration was preserved. Test with a real external client after deployment; local SDK tests are not proof of third-party production connectivity.

## Standards reviewed

Official [MCP authorization](https://modelcontextprotocol.io/specification/latest/basic/authorization) and [security guidance](https://modelcontextprotocol.io/specification/latest/basic/security_best_practices) were consulted on 1 November 2026. Important controls are intended-resource validation, no token passthrough, consent per client, exact redirects, scoped tools and ownership of every workflow handle. This assessment preserves the installed SDK's implemented protocol negotiation; it does not claim every future specification feature is implemented.
