# WebMCP evaluation

Reviewed 1 November 2026. Recommendation: **defer production integration** and retain the remote OAuth MCP implementation. No WebMCP runtime or production feature flag was added.

## What it provides and current evidence

WebMCP lets a web application register structured JavaScript tools for browser/in-page agents, reducing reliance on visual DOM interaction. The official [draft](https://webmachinelearning.github.io/webmcp/) retrieved for this review is dated 27 October 2026 and explicitly identifies itself as a Community Group report, not a W3C Standard or Standards Track document. Its current imperative API extends `Document` with `modelContext` in secure contexts, with `registerTool`, `getTools` and `executeTool`. Earlier examples using other API shapes should not be copied without rechecking the draft.

Chrome's [official announcement](https://developer.chrome.com/blog/webmcp-epp), dated 10 February 2026, describes an early preview program and imperative/declarative approaches. This page does not establish general availability in every current Chrome release, other browser engines or the user's installed browser. Cross-browser production support was not established. No claim of stable, universal browser availability is made.

The more recent [Chrome Platform Status entry](https://chromestatus.com/feature/5117755740913664), updated 28 September 2026 and queried during this review, still reports `Proposed` and `is_released: false`, with no shipped desktop/Android/iOS/WebView milestone. It records a developer-trial milestone of 146, an experimental-platform-feature flag name, and no Firefox or Safari signal. Trial milestones and a stated future rollout plan are not evidence of a stable release. Together these current primary sources support deferral rather than relying only on the February announcement.

## Difference from remote MCP

| Concern | Existing remote MCP | WebMCP |
|---|---|---|
| Execution | Server tools through MCP HTTP transport | Tools registered inside an open website document |
| Identity | Audience-validated OAuth connection and narrow scopes | Website session plus browser/agent exposure controls; backend authorization still mandatory |
| Availability | Can manage lists without an open website tab; retailer work still needs the user's extension | Requires the website/document and a supporting agent/browser |
| Consent | Client/scope consent, explicit destructive contracts, local extension approvals | Must show equivalent application approval for mutations; hints alone are insufficient |
| Product benefit | Supported remote assistants use existing account-scoped services | Potential guided, visible list editing and accessible browser-agent interaction |

## Security and maintenance risks

The current draft addresses tool/output prompt injection, misrepresented user intent, parameter privacy and same-origin exposure. Registering tools does not remove those risks or authorize new privileges. Customer list/product text is untrusted. A tool description or consequential annotation is not a security boundary. Cross-origin exposure must remain disabled by default; the user's open tab must never become an unauthenticated remote API.

API movement and incomplete verified browser coverage would add ongoing maintenance and a second consent surface today. The existing remote MCP already provides list access and approved extension product search/capture, so a rushed prototype would have little immediate benefit. Essential list functions must remain independent of experimental APIs.

## Future isolated prototype design

If a supported test browser and a specific browser-agent journey are selected, add a disabled-by-default `NEXT_PUBLIC_ENABLE_WEBMCP_PROTOTYPE` gate and feature-detect the then-current API. Start with a read-only `get_my_lists` using the existing same-origin API client. Require an active first-party session, bound result size, cancellation and a fixed schema. Register fixed metadata only; never put customer text in tool descriptions or accept an account ID as identity. Tear down registrations on account change/document lifecycle using the API supported by that browser version.

Before any write prototype, show an application review dialog containing the exact intended list/item changes, require affirmative user confirmation, and send existing expected-account/version/request-ID fields through the ordinary CSRF-protected gateway. Cancel pending work on logout/account change. Keep token values and extension credentials inaccessible to tools. Test anonymous access, account switching, denied consent, cancellation, stale versions and adversarial retailer/list text.

Only reconsider production after verifying stable target-browser support, agent compatibility, genuine usability benefit and the complete security test matrix. A future browser permission prompt alone does not replace these checks.
