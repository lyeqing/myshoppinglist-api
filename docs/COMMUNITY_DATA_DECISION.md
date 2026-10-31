# Decision record: personal capture and optional community observations

31 October 2026 · MSL-600–603 · **Design only; sharing and cross-user execution are not authorised.**

The owner authorised the existing customer's own extension workflow, with approval before opening supported URLs and review before saving/sharing a result with their own connected AI app. That authorisation does not include contributing observations to other accounts. The current release remains personal-only. This document supplies the phase's design/feasibility record; it does not substitute for owner approval, retailer permission, legal advice, Chrome review or empirical product validation.

## Options and decision

| Option | Possible benefit | Main risks and cost | Current decision |
| --- | --- | --- | --- |
| A. Personal observations only | Customer controls what is captured for their own list; clear provenance and ownership | Stale/local/personalised prices; retailer terms still matter; extension maintenance and support | Retain existing authorised local scope; default-off bridge, fixture-tested, no claim of live retailer permission |
| B. Explicit contribution of already captured public facts | Could reduce repeated manual checks and identify useful alternatives | Location/time mismatch, indirect identity leakage, poisoning, licensing/redistribution limits; consent, deletion, moderation, cache and provenance costs | No implementation or sharing flag; revisit only after evidence, policy review and explicit owner approval |
| C. Another user's request executed in a customer's browser | Could seek a fresher observation | Stranger-controlled work, bandwidth/battery and account exposure, retailer blocking, coercive incentives, abuse and consent complexity | Not approved; do not build or silently route tasks between accounts |

A technically supported URL is not permission to automate it. Browser approval alone does not resolve retailer access/redistribution restrictions. No option permits bypassing challenges, rotating identities/proxies, harvesting catalogues, collecting unrelated browsing or treating uncertain observations as current universal prices. A blocked retailer must stop the request with an honest unavailable result.

## Inspected policy evidence and unresolved permission

These primary pages were inspected on 31 October 2026. This is a scoped engineering assessment, not a legal determination or store approval:

- [Chrome Web Store program policies](https://developer.chrome.com/docs/webstore/program-policies/policies): data collection must serve the disclosed single purpose, browsing activity has narrow user-facing-purpose constraints, permissions must be minimal, and material practice changes require disclosure and informed consent. Derived/anonymised data is still covered by limited-use restrictions. Opt-in alone is not a blanket permission to redistribute data. Review the precise proposed B workflow with qualified reviewers before distribution.
- [ALDI website terms](https://www.aldi.com.au/legal-notice), Use of the Site: automated access is restricted, with a stated search-engine exception; distribution/exploitation is also restricted. This shopping extension is not established to fall within that exception. Treat live automated ALDI capture and B/C as requiring resolved permission before launch.
- [Woolworths terms](https://www.woolworths.com.au/shop/services/terms-and-conditions), clause 1.4: retrieval/indexing mechanisms and excessive load are restricted. No permission for this proposed workflow was obtained. No live automated collection/redistribution approval is claimed.
- [Coles website terms](https://www.coles.com.au/important-information/terms/website-terms-conditions), sections 2 and 4: personal use and reuse restrictions require review. The content definition excludes pricing data, but that exception does not itself establish permission for an automated commercial collection or redistribution service. Obtain a workflow-specific decision/permission rather than assuming public visibility is a licence.

No retailer was contacted, no permission was obtained and no live product collection was performed for this report. Existing synthetic capture journeys demonstrate software behavior, not compliance or real-world capture reliability. Public marketing must not imply retailer affiliation, guaranteed prices or a compliant live collection service.

## Conditional B data contract — specification only

Only after a new approval, propose a separate `ProductObservation` containing: opaque observation ID; retailer/source URL stripped of tracking/session parameters; exact product/pack identity; channel and approved coarse store/location context; `capturedAt`, `contributedAt`, `expiresAt`; public price/currency/unit-price type and explicit conditions; extraction/source confidence; consent version/reference; moderation/revocation state. Retain a contributor account link **privately** for ownership, abuse investigation and deletion, never in public responses. Avoid importing retailer descriptions/images without rights review.

Propose `SharingConsent`: private account ID, immutable policy/version, explicit affirmative choice and timestamp, scope of fields/purpose/audience, revocation timestamp and audit reference. Default OFF; do not infer consent from sign-in, using AI, a paid plan, saving a product or accepting analytics. The existing `ContributionObservation` and legacy contribution/queue code are not this approved contract and must not be repurposed automatically. Review legacy routes/workers before any deployment; their presence is not an authorisation to enable them.

Reject customer identities, account/session data, cookies, browser history, shopping-list contents, retailer account IDs, loyalty/member/personalised offers, delivery addresses and precise home location. A public fact mixed with private context is not safe by default. Start with explicit per-observation review; no automatic bulk contribution. Missing public-price evidence means rejection, not guessing. Observation freshness windows and retention remain policy decisions; display source/time/location/conditions and expire stale observations without pretending they were rechecked.

## Consent UX prototype — text only, not a shipped control

1. After a personal save: **“Keep this observation private”** is selected. Optional separate action: **“Review a public contribution”**. Neither affects existing personal features.
2. Review card shows only the exact proposed public fields, audience, source, captured time, store/channel, price conditions and expiry. Plain warning: **“Other customers may see these facts. This is a saved observation, not a live price guarantee.”** Actions: **“Cancel and keep private”** / unchecked **“I choose to contribute these public facts”**, then **“Contribute this observation”**.
3. Contribution history shows state/freshness and **“Withdraw this contribution”**. A separate **“Stop future contributions”** choice immediately revokes consent; offer removal of existing contributions. Explain published-cache/deletion timing accurately once designed. Never promise removal from copies already lawfully received by others.

Account changes, missing consent, ambiguity, failure or cancellation keep data private. Withdrawal must not remove personal saved products, reduce normal service or re-enable itself. Privacy/store disclosures must be updated before introducing this flow.

## Evidence, tests and rollout gates

There is **no empirical evidence yet** that B improves customer decisions or is worth its operating/privacy cost. Existing lab/browser timings and fixture success are not user-demand evidence. A future authorised study should use synthetic/licensed observations and a small consented cohort: compare A/B comprehension, freshness/location mistakes, useful successful comparisons, withdrawal completion and support load. Define the success threshold, recruitment permission, retention and spending budget before running it. No recruitment or experiment was launched.

Before any B implementation: obtain explicit owner approval for the exact scope; resolve retailer rights/access and Chrome single-purpose/disclosure compatibility; approve privacy/retention/withdrawal policy; validate user value with approved evidence; estimate hosting, moderation and support costs with a hard approved budget; threat-model poisoning, forged timestamps, identity inference and cache leakage. Prefer licensed retailer feeds if feasible. No cost estimate is presented as measured production spend.

Required tests before release: two-user read/write separation; consent defaults and version changes; contribution retry idempotency; account switch; public-field allowlist and personal-offer rejection; forged URL/location/time; exact product versus alternative; stale expiry; permission and source checks; revocation racing publication; owned deletion through aggregates/search/caches/backups under an approved policy; staff access audit; workload bounds; failure cancellation; unchanged personal service after opting out. Independent security/privacy and store reviews remain gates.

For C, require a **separate** security/abuse impact assessment, consent prototype, retailer/policy review, budget and new owner approval. Do not add a dormant cross-user execution switch now. No endpoint, schema, background job, task-routing change or sharing implementation was added for this phase.

**Gate outcome:** design deliverable complete; optional B/C implementation gates remain closed. Next operational work is resolving release prerequisites for the existing personal product, not building community collection.
