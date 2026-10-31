# Public pages, discovery and launch measurement

These are local implementation/launch drafts. No website/store release, Search Console connection, paid campaign, production email or checkout is activated.

## Public page audit and implementation

The existing home page already renders its headline/description in initial HTML and keeps the working account/trial/list workflow. The public extension page had stale staging copy and exposed developer machine paths/ports. Its customer-facing replacement explains test-build access, supported pages, review/save, unknown prices, shared sign-in and separately approved AI requests; developer setup remains in the extension README/runbooks.

Public routes now cover `/`, `/features`, `/how-it-works`, `/extension`, `/faq`, `/pricing`, `/support` and `/privacy`, with server-rendered text, descriptive unique metadata, canonical URLs, Open Graph/Twitter metadata, internal navigation and a generated brand share image. The home page has minimal truthful SoftwareApplication JSON-LD. There are no fabricated reviews, ratings, paid prices or rich-result promises. Missing public origin fails closed for discovery; configure the real canonical HTTPS origin at build and runtime before public deployment. Rebuild localhost test artifacts for the actual deployment origin.

The owner chose **all crawlers on public pages**. `robots.txt` uses a wildcard allow rule and private-prefix disallows; it does not single out AI training/search bots. Sitemap contains public URLs only, without guessed modification dates. Private account/list/admin routes have noindex metadata, and API ownership/administrator authorization remains the data boundary. Administrator pages verify authorization on the server too. Crawlers do not receive personal lists, support tickets, consent grants or tokens in public HTML.

Robots rules are not security or guaranteed URL removal. A blocked crawler may not read a page’s noindex tag; an externally linked URL can still be known to a search engine. Keep authentication/no-store responses independent, avoid public links to token-bearing URLs, and use verified webmaster removal/inspection tools for any already-indexed sensitive URL. [Google noindex guidance](https://developers.google.com/search/docs/crawling-indexing/block-indexing), [Google robots guidance](https://developers.google.com/search/docs/crawling-indexing/robots/intro).

Prices are dated personal observations, not automatic current checks. The trial is currently three hours, registered free accounts have two active lists, and paid/complimentary accounts have five. Prices and live checkout are not published. Optional AI and capture depend on service rollout configuration. Public copy must be reviewed when limits, distribution or billing change; a code change alone is not a public launch.

## Measurement definitions and limitations

- Optional visitor analytics starts only after consent. Events use fixed public-page/action names, random first-party visitor/session identifiers and a limited `utm_source` slug. Source is remembered only in the consenting browser session across navigation. No raw referrer, whole URL/query, unrelated history or product/list contents is recorded. Withdrawal clears session attribution and requests raw visitor-event deletion.
- CTA clicks mark explicit public actions; signup-start events mean validated submission attempts, and signup-complete browser reports are separate from server-confirmed registrations. Duplicate event IDs do not add counts. Campaign reports are client-reported estimates, not verified ad billing/conversion evidence.
- `/superadmin/marketing` shows up to 25 sources over retained 30-day raw history, consenting visitor estimates, page views, CTA clicks, signup attempts/completions, registered accounts with saved-item history, and observed 7/30-day account activity. Different windows/populations are labelled. A saved item is an activation proxy; observed activity is not D7/D30 cohort retention. Do not divide unlike counts into an invented conversion rate or add daily unique estimates into monthly unique people.
- Revenue and payment conversion remain unavailable until confirmed billing events/commercial configuration exist. A manual paid/complimentary flag is not money received. Billing groundwork must remain test-only until separately approved.
- Raw events are pruned after 30 days and anonymous aggregates after 12 months. Counts before instrumentation, nonconsenting customers, missing sources and elapsed history are unknown or excluded. Anonymous aggregates cannot reconstruct individual attribution. See OPERATIONS.md for privacy/retention/support details.

## Search and AI-search checklist before publishing

1. Choose and verify the actual HTTPS domain/operator/privacy contact. Set `MYSHOPPINGLIST_PUBLIC_ORIGIN` consistently at build/runtime, validate canonical URLs and HTTPS redirects, and keep preview environments nonindexable.
2. Inspect public initial HTML, unique titles/descriptions, sitemap, robots, share image and structured data on desktop/mobile. Do not add fictitious SoftwareApplication offer prices or reviews just to pass rich-result requirements. Semantic markup does not guarantee a Google rich result. [Google SoftwareApplication guidance](https://developers.google.com/search/docs/appearance/structured-data/software-app), [Schema.org SoftwareApplication](https://schema.org/SoftwareApplication).
3. Verify ownership in Google Search Console and Bing Webmaster Tools using owner-controlled DNS/account access; submit the public sitemap and inspect representative URLs. No credentials are provisioned by this work. Monitor crawl/index reports, canonical selection and rendering. [Bing webmaster guidelines](https://www.bing.com/webmasters/help/webmaster-guidelines-30fba23a), [Bing sitemap discovery](https://blogs.bing.com/webmaster/2025/7/Keeping-Content-Discoverable-with-Sitemaps-in-AI-Powered-Search/).
4. Review CDN/WAF/Cloudflare settings so allowed search/AI crawlers can fetch public text/assets without receiving personal data or bypassing authentication. Robots instructions alone do not establish crawler identity. Do not disable protection globally to chase visibility.
5. Measure actual deployed field performance and availability. Local production-navigation timings are a reproducible lab check, without representative network/CPU throttling or field Core Web Vitals validation. No rank, inclusion, page-speed score or AI-answer recommendation is promised.

## Chrome Web Store draft (not submitted)

**Proposed name:** MyShoppingList — Private shopping lists

**Short description:** Review supported product pages and save private, dated product observations to your shopping list.

**Draft listing copy:**

Keep the products you choose with your shopping list. Open a supported Coles, Woolworths or ALDI public product page, choose your list and quantity, read the product and review it before saving. Observations stay private to your account and include when you captured them. Missing prices remain unknown; saved observations do not guarantee current prices, stock or store availability.

Sign-in is shared with MyShoppingList in your Chrome profile. Review and cancel before saving; retry uncertain saves using the same request. Where your service enables AI requests, use a native extension review page to approve each supported capture or small search. No other customer’s browser tasks run in your extension, and the service cannot wake a closed browser.

Distribution, production host permissions, public privacy/operator details, supported service origin, store branding/claims and the live feature configuration require review before submission. Do not publish localhost permissions or a local filesystem installation path as the commercial onboarding flow. Do not claim retailer affiliation, savings guarantees, rankings, store endorsement or unpublished functionality.

**Screenshot recommendations:** use actual verified UI with disposable accounts and clearly marked fixture observations, never real customer lists/notes/email or a fake live-price guarantee. Capture (1) review before saving; (2) private list/quantity and observation date; (3) clearly unknown price; (4) explicit AI approval if that feature is included in the submitted release; (5) account/browsers revocation. Keep text readable at actual size. Prepare 1280×800 screenshots, then check current dashboard requirements before submission. [Chrome listing assets](https://developer.chrome.com/docs/webstore/cws-dashboard-listing), [Chrome listing quality guidance](https://developer.chrome.com/docs/webstore/best-listing).

## Small launch experiments, after readiness and owner approval

Start with a small invited cohort and a working supported-product workflow. Measure installation completion, consented signup attempts/successes, first reviewed save, support failures and return activity. Record denominators and unknowns. Compare one onboarding/copy change at a time with a stated audience/window; use private feedback to explain failures. Fix reliability before expanding acquisition. Paid ads/spending, email campaigns and store/website publishing require separate authorization; none are launched here.

Stop or roll back an experiment when consent/auth isolation fails, first-save reliability deteriorates or support volume is unmanageable. Disable optional AI/bridge/operations features using their documented flags without dropping history. Marketing copy must follow actual enabled capabilities and confirmed limits.
