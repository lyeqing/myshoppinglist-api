# Google, Facebook and Microsoft sign-in

Implemented locally on 15 November 2026. Provider switches default off. No developer credentials were supplied, no live provider settings were changed, and the social migration has not been applied to the application's local or production database. Controlled protocol tests are not real-provider certification.

## Architecture and account ownership

The existing `UserAccounts.Id` remains the owner of lists, products, preferences, subscription/billing records and history. Microsoft-maintained ASP.NET Core Google, Facebook and Microsoft Account handlers validate OAuth responses server-side. Google/Microsoft use S256 PKCE; Facebook uses its maintained handler's supported OAuth flow and application proof. There is no paid identity service, browser identity SDK, second user directory or replacement session system. Successful social authentication issues the existing hashed opaque `UserSession` and normal HttpOnly application cookie. Provider access/refresh tokens are not persisted or returned to the website.

`ExternalIdentities` stores a unique `(Provider, Subject)` and account FK, plus an optional normalized email hint and creation date. A provider subject is case-sensitive, stable identity; an email is never an identity key. There is at most one identity per provider per internal account. Provider ownership/subject cannot be mutated. `ExternalAuthFlows` contains expiring, one-use workflow records bound to the original account and session when present; a composite FK prevents a flow from referencing another account's session. It contains no provider tokens. Expired flows older than one day are removed in bounded batches when another provider flow starts; there is no separate purge scheduler. Account support export discovers account-owned rows and redacts token hashes using the existing exporter.

All provider callbacks use the **website origin**, through its tightly allowlisted API gateway. The gateway forwards only the normal session cookie and narrowly named social correlation/temporary-ticket cookies; it strips cookie domains and validates redirect destinations. The server constructs callback URLs from `SocialAuthentication:WebsiteOrigin`, never browser-supplied return URLs. OAuth state/correlation and the encrypted ten-minute temporary ticket remain managed by the provider libraries and shared Data Protection configuration. Provider failures/cancellation redirect to fixed, non-sensitive messages. The final authenticated result is committed only by a protected POST when the customer presses Continue. Replays, expired flows, wrong browsers and changed sessions/accounts are rejected.

## Customer workflows

- **Existing password users:** sign in normally, open Account security, verify the current password or an already linked provider, then connect another provider. New provider authentication alone never proves ownership of the existing account. Fresh proof lasts five minutes and linking consumes that proof.
- **New social users:** a new permanent internal account and first list are created together with the external identity. Missing provider email is supported. Provider email is retained only as a collision hint; it is not copied into a trusted local recovery/password address.
- **Matching emails:** an unknown external identity whose email matches an existing account or external identity is directed to sign in through an existing method and connect the provider in account security. No automatic linking, merging or duplicate signup on a known matching hint. Missing/changed email cannot replace the stable provider subject. When no email hint exists, the server cannot infer another provider's ownership; customers should connect additional methods while signed in.
- **Active trials:** the same shared registration core upgrades the existing account/list IDs, preserves preferences and selected products, clears trial expiries and revokes trial-issued sessions. Original expiries are rechecked before commit. If the provider already belongs to a permanent account, a separate explicit confirmation is required to leave the trial separate and open that account. No trial lists/preferences are merged. Expired/revoked trial sessions cannot be silently converted or replaced.
- **Disconnect:** reauthentication is required; another configured/available provider or a password must remain. An unavailable configured-off provider does not count as the only remaining usable option. Unlinking signs out all application sessions and revokes existing MCP/extension grants using the existing credential-revocation service. Lists/subscriptions remain untouched.
- **Add email/password to a social-only account:** verify an existing method, request an email link and open it in the same signed-in browser/session. The single-use link expires in fifteen minutes, lives in a URL fragment and is removed from browser history immediately. Only after email proof and password-policy validation are the local email, verification date and password set; existing sessions/grants are revoked. Delivery uses the existing protected SMTP outbox. If SMTP is unavailable, existing providers remain usable. Email/password accounts retain their existing password-change and recovery behavior; existing addresses are not retroactively marked verified.

Security logs use fixed outcome categories plus ordinary request correlation, without subjects, emails, tokens or provider error details. Account and subject locks, uniqueness constraints and deferred database credential guards protect races and the last sign-in method. A linked identity cannot authenticate a suspended account. No account-merging facility was added.

## Configuration

Set server-side environment variables or ignored local settings. Never put a client secret in a `NEXT_PUBLIC_*` variable, tracked file, URL, log or chat. Enable each provider independently only after its application is registered:

```json
{
  "SocialAuthentication": {
    "Enabled": true,
    "WebsiteOrigin": "http://localhost:3001",
    "Google": { "Enabled": false, "ClientId": "", "ClientSecret": "" },
    "Facebook": { "Enabled": false, "ClientId": "", "ClientSecret": "" },
    "Microsoft": { "Enabled": false, "ClientId": "", "ClientSecret": "" }
  }
}
```

Environment equivalents are `SocialAuthentication__Enabled`, `SocialAuthentication__WebsiteOrigin`, and `SocialAuthentication__Google__Enabled`, `SocialAuthentication__Google__ClientId`, `SocialAuthentication__Google__ClientSecret` (replace `Google` with `Facebook` or `Microsoft` for the other providers). The frontend needs only `MYSHOPPINGLIST_API_URL` pointing at the backend and `MYSHOPPINGLIST_PUBLIC_ORIGIN` matching the website origin. Provider credentials never cross the gateway to the browser.

Production requires a trusted HTTPS website origin and explicitly persistent, certificate-protected Data Protection keys using the existing `AccountSecurity:KeyDirectory`, `KeyCertificatePath` and optional certificate password configuration. Social startup refuses missing production key configuration even when email delivery is disabled. Keep the keys stable across replicas/restarts; protect/rotate them according to the existing account-security runbook. The existing email verification/password enrollment also needs the SMTP settings in [account security](ACCOUNT_SECURITY.md). There is no implicit SMTP/provider activation.

## Exact provider callbacks

Register these on the **website**, not the backend-only MCP URL:

| Provider | Local example | Production |
| --- | --- | --- |
| Google | `http://localhost:3001/api/auth/social/callback/google` | `https://YOUR-WEBSITE/api/auth/social/callback/google` |
| Facebook | Use an allowed HTTPS development website origin plus `/api/auth/social/callback/facebook` | `https://YOUR-WEBSITE/api/auth/social/callback/facebook` |
| Microsoft | `http://localhost:3001/api/auth/social/callback/microsoft` | `https://YOUR-WEBSITE/api/auth/social/callback/microsoft` |

Use exact registered URIs and one consistent hostname; `localhost` and `127.0.0.1` are different cookie/origin hosts. Register the actual port if an explicitly approved test port differs. Current port-3001 website and API-only ngrok MCP endpoint are not automatically converted into a new social-login deployment. For Meta, use a website HTTPS development tunnel if its dashboard rejects HTTP localhost. Production always uses HTTPS; never disable provider redirect validation or TLS to make a callback work.

### Google

In [Google Cloud credentials](https://console.cloud.google.com/apis/credentials), configure the OAuth consent/branding screen and create a **Web application** OAuth client. Add the exact website callback above. Configure the testing audience/test users for development and the app's real support, privacy and domain information before public release. Supply the client ID and client secret to the backend. Use only the handler's identity scopes (`openid`, profile, email); do not add Drive, Gmail or contacts access. Public availability/brand or domain verification depends on the selected audience and Google's current requirements. The button uses the standard multicolor G paths from [Google's approved vector asset](https://developers.google.com/static/identity/images/branding_guideline_sample_lt_sq_sl.svg); review the final deployed presentation against [Google branding guidance](https://developers.google.com/identity/branding-guidelines) before provider verification.

### Facebook / Meta

In [Meta for Developers](https://developers.facebook.com/apps/), create an application supporting Facebook Login and configure the web login use case. Set the website URL/app domains and exact **Valid OAuth Redirect URI**. Keep strict redirect matching and HTTPS enabled. Use app roles/test users while the app is in development. Put **App ID** in `ClientId` and **App Secret** in `ClientSecret`. Request only basic identity and optional email; no friends, posts or activity. Complete the privacy policy, data-deletion instructions, app publication/review and any business/data-use verification required by the actual dashboard before accepting public users. Meta pages were rate-limited during this implementation, so publication/verification requirements and live email-denial/reauthentication behavior require checking in the developer dashboard; none is represented as completed. See [Facebook Login](https://developers.facebook.com/docs/facebook-login/) and [ASP.NET Core Facebook setup](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/facebook-logins?view=aspnetcore-10.0).

### Microsoft (including personal accounts)

In [Microsoft Entra](https://entra.microsoft.com/), create an App registration with **accounts in any organizational directory and personal Microsoft accounts** so Outlook/Hotmail/personal accounts are supported. Add the exact callback as a **Web** platform redirect, not SPA/native. Create a client secret and use its **Value**, not its secret ID, with the Application (client) ID. The maintained Microsoft Account handler uses Microsoft's common authorization flow and the basic Graph profile permission; do not grant mail/contacts/calendar permissions. Review consent and publisher-verification requirements for the chosen audience/tenant policies before public release. [Microsoft setup](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/microsoft-logins?view=aspnetcore-10.0).

Keep each registered provider application/client ID stable when rotating secrets: Facebook subjects in particular are app-scoped. Register separate development/production clients as appropriate; never transfer identities between internal users to compensate for a changed app ID.

## Migration and local testing

Additive migration `20261115012833_AddSocialSignIn` adds the two external tables, nullable `UserAccounts.EmailVerifiedAt` and `UserSessions.ReauthenticatedAt`, and updates credential/outbox constraints. **Apply this migration before starting the updated API, even when social providers are disabled:** ordinary account/session reads also map the new columns. Existing IDs, email/password hashes, entitlements and shopping data stay intact. Deferred triggers prevent permanent accounts without a credential and trial accounts with external identities. Identity ownership is immutable. Populated rollback refuses to delete identities, flows or security/verification data; disable new flows and use a reviewed roll-forward or recovery. Apply only to a selected, backed-up target after the disposable rehearsal; production/local application deployment is not implied by implementation approval.

1. Build backend and frontend; rehearse/apply the migration to a disposable database with the existing PostgreSQL tooling. Keep live/application targets out of automated tests.
2. Configure one developer provider at a time with the real client ID/secret and exact website callback. Start the API against the selected test database, and the website on its approved port with matching origins.
3. Confirm `/api/auth/social/providers` reports only configured providers available. Start a new sign-in, cancel once, then complete sign-in in the same browser. Confirm the normal session/list appears.
4. Test password login and a three-hour trial. Convert a trial through a new identity, then separately test an existing identity and explicit non-merge confirmation.
5. Test linking/unlinking in account security, incorrect credentials, selecting the wrong provider account, stale proof, last-method protection, account switching and disabled-provider behavior.
6. With SMTP configured, test email/password enrollment in the same session and invalid/expired/replayed links. Confirm the old session and delegated connections are revoked after credential changes.

Controlled tests use fake token/user-info responses through the real provider handlers. The real-browser harness additionally crosses the production website, actual gateway/API, cookies and disposable database; provider pages are intercepted with controlled redirects. Desktop cases register new accounts and mobile cases convert active trials in place. They cover protocol state/correlation and PKCE, not actual provider accounts, external consent screens, mobile provider browsers or provider publication. The separate social Playwright config uses explicitly approved port **3002**, preserving the existing server on 3001 and user port 3000: `node node_modules/@playwright/test/cli.js test --config playwright.social.config.ts`. Browser journeys mock API outcomes; the gateway test uses a loopback stub backend to exercise real cookie and redirect filtering. Do not call those mocks live-provider tests. Final observed results are recorded in [security testing](SECURITY_TESTING.md).

## Remaining owner setup

Provide the three developer application IDs and secrets **through local environment/secret storage**, choose the actual website origin/callbacks, and configure SMTP if email/password enrollment is needed. Complete each provider's audience/test-user, privacy/domain/publication requirements. After that, run actual Google, Facebook and personal Microsoft logins on desktop/mobile and confirm the deployed button presentation and recovery journey. No credentials, provider approval, production activation, account merging, Apple or passkey integration was fabricated or added.
