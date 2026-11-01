# Password recovery and notification delivery

Local implementation, 1 November 2026. This adds working recovery/password-change workflows and optional email transports. It does **not** configure a provider, send external email, change the application database or activate production services. All browser and transport verification uses disposable accounts and a loopback SMTP receiver.

## Schema and compatibility

`20261101001539_AddAccountRecoveryAndEmailOutbox` adds only `AccountCredentialChanges` and `EmailOutboxMessages`. Existing account/list/product columns are unchanged, so ordinary authentication and shopping reads do not acquire a dependency on the new tables. Apply the migration before using password changes, enabling recovery or processing email. Existing hashes, sessions, ownership keys and saved prices are preserved. No existing accounts are marked verified or assigned new passwords.

The down migration locks both tables and refuses to erase any credential/delivery history. Disable the features and review recovery/retention before rollback. Tests rehearse additive upgrade, customer/catalog preservation, empty rollback and refusal to discard history. No migration was applied to an application/production target.

## Customer workflow

- **Settings → Password and account security** verifies the current password and expected account, validates the new password with the registration policy, then changes credentials under an account-row lock. The user is signed out on all devices. Lists and product data are preserved.
- **Sign in → Forgot password?** acknowledges every syntactically valid email identically. Both known and unknown addresses take the same encrypted enqueue path; account lookup is deferred to the worker. No account-existence result or reset proof is returned through the API.
- Requests for one normalized address are grouped for ten minutes with a database advisory lock, across application instances. Request/complete/change routes also have a five-per-fifteen-minute peer budget and existing CSRF/ingress protections. A bounded pending queue prevents unlimited recovery backlog. Trusted client-IP forwarding behind the website gateway still requires deployment configuration.
- The worker sends a purpose-bound, authenticated-encrypted reset capability to an active registered account's mailbox. It expires within fifteen minutes of the request, contains random entropy, and is tied to the current password hash/salt fingerprint. The capability is created in memory for sending; no reusable plaintext proof is persisted or logged.
- The link uses a URL **fragment**, never a query string. The website removes it from history, holds it only in component memory and accepts same-tab fragment changes. It sends the proof only in the CSRF-protected completion body. Refreshing after removal requires reopening the email link. Recovery pages are noindex/no-referrer and excluded from the public sitemap.
- Completion rechecks expiry and the credential fingerprint under the same account lock as password changes. Exactly one concurrent use can change the credential; the new random salt invalidates this and every older issued reset link, including when the chosen password text is unchanged. Invalid, expired, tampered and used links have the same error. No automatic sign-in occurs.
- Changes revoke all owned browser/native sessions, AI connections, OAuth tokens/authorizations and extension devices, cancel pending extension work and invalidate queued recovery requests atomically with the credential update. Consent rechecks its source session under the account lock, so a stale browser request cannot create a fresh grant after revocation. A password-change notice is queued when delivery is enabled.

Email ownership verification at registration remains unimplemented; the product does not claim that a registration email has been verified. Recovery proves access to the destination mailbox. MFA is not introduced by this work.

## Application email configuration

Defaults remain `EmailDelivery:Enabled=false`, `AccountRecovery:Enabled=false`, and `Operations:SimulateDelivery=true`. Configuration examples are placeholders, not activation:

```json
{
  "EmailDelivery": {
    "Enabled": true,
    "Host": "smtp.example.test",
    "Port": 587,
    "ImplicitTls": false,
    "FromAddress": "alerts@example.test",
    "Username": "configured-through-private-environment",
    "Password": "configured-through-private-environment",
    "TimeoutSeconds": 15,
    "MaximumAttempts": 3
  },
  "AccountRecovery": { "Enabled": true, "WebsiteOrigin": "https://shopping.example.test" },
  "AccountSecurity": {
    "KeyDirectory": "/restricted/persistent/myshoppinglist-keys",
    "KeyCertificatePath": "/restricted/secrets/key-encryption.pfx",
    "KeyCertificatePassword": "configured-through-private-environment"
  },
  "Operations": { "Enabled": true, "SimulateDelivery": false, "IncidentRecipients": ["owner@example.test"] }
}
```

Use mandatory STARTTLS on 587 or `ImplicitTls=true` for a provider requiring TLS on connect (normally 465). MailKit performs normal certificate validation; there is no protocol logging, certificate bypass or opportunistic plaintext fallback. Credentials must be supplied together. Explicit plaintext is allowed only with `AllowLoopbackWithoutTls=true`, an exact loopback IP, no credentials and Development; this is for local fixtures.

Production email/recovery refuses startup without a persistent Data Protection directory and a private RSA key-encryption certificate (at least 2048 bits). Share the protected key ring between replicas; restrict directory/certificate access; back up keys and the certificate independently of PostgreSQL. Losing them prevents queued payload decryption and invalidates pending links. Retain the certificate needed by existing keys when planning rotation. Do not put keys in a public web directory, source control or published artifacts.

Review migration/runtime grants on the two new tables. Select a verified sender and provider, configure the sender's required DNS protections, and verify acceptance, actual inbox delivery, failure/retry and recovery with approved addresses before activation. `sent` means SMTP accepted the message, not proof of inbox delivery.

## Durable delivery, privacy and incidents

The shared PostgreSQL outbox encrypts recipients/content with Data Protection. Only fixed message kinds are accepted. Claim leases are committed before network calls; SMTP does not hold customer locks. Multiple workers use `FOR UPDATE SKIP LOCKED`, claim tokens, bounded attempts/timeouts and delayed retries. Successful/terminal jobs clear encrypted payloads. Completed metadata is retained for seven days while the email worker is enabled. Disabling the worker pauses its maintenance; review retained pending jobs when turning delivery off.

Transport is **at least once**: if SMTP accepts a message but recording the result fails, a later retry may duplicate it. Stable message IDs support provider deduplication but do not promise exactly-once inbox delivery. Reset links remain single-use despite duplicate emails. Failed jobs remain truthful `failed` records, never `sent`. Logs contain fixed categories/message references rather than SMTP exception strings, addresses or links.

Existing incident grouping/cooldowns remain. Real critical incidents enter the same outbox using their unique delivery IDs; repeated dispatch cannot create a second queued intent. Administrator `simulation:*` incidents never enter the live outbox, even when SMTP is enabled. Multiple private alert recipients are blind-copied. The dashboard distinguishes simulation, pending, sent and failed outcomes and does not claim that a configured recipient proves delivery.

Failed/not-configured sends do not consume the successful-send cooldown. A live recovery notification waits for the corresponding opening notification to be accepted; if that opening fails permanently, recovery is suppressed. Simulations still record their own open/recovery outcomes without entering live delivery. The independent probe follows the same ordering and cooldown rules.

The generic email outbox is not a customer-export endpoint. Its encrypted payloads are not included by the account export tool. Credential-change timestamps contain no passwords/proofs. Support-reviewed account deletion should also cancel pending messages for the known account address and review the seven-day email metadata retention.

## Independent outage email

The Python uptime probe can now deliver from its own SQLite outbox while the application/PostgreSQL is unavailable. It must run outside that failure domain. The default remains simulation, in `uptime-simulation.sqlite3`; explicit live mode uses a separate `uptime-live.sqlite3`, so simulated history cannot be replayed as live mail.

Configure privately:

- `MYSHOPPINGLIST_MONITOR_ID`: stable, unique environment identifier (letters/digits/dot/underscore/hyphen, at most 64 characters).
- `MYSHOPPINGLIST_INCIDENT_RECIPIENTS`: one to five comma-separated addresses.
- `MYSHOPPINGLIST_SMTP_HOST`, `MYSHOPPINGLIST_SMTP_PORT`, `MYSHOPPINGLIST_SMTP_FROM`.
- Optional matching `MYSHOPPINGLIST_SMTP_USERNAME` / `MYSHOPPINGLIST_SMTP_PASSWORD` and `MYSHOPPINGLIST_SMTP_IMPLICIT_TLS=true` for implicit TLS.

```powershell
python tools/operations/uptime_probe.py --base-url https://api.example.test --state-dir /restricted/persistent/monitor --send-email
```

`--send-email` cannot be combined with `--simulate`. Plaintext SMTP needs `--allow-loopback-smtp`, a loopback health target with `--allow-loopback-http`, an exact loopback SMTP IP and no credentials. Never use that switch for deployment. Delivery attempts use SQLite claims/retries, mandatory TLS and fixed messages; exceptions are not retained. A failed or pending delivery makes the invocation unsuccessful even if health has recovered. Schedule bounded invocations with an appropriate process timeout. Outbox claims recover after an interrupted sender; SMTP delivery may be duplicated after ambiguous acceptance. Finished history is retained for thirty days and CLI output is bounded to fifty records.

Independent placement, scheduling, protected SQLite storage, actual provider credentials and external delivery remain deployment responsibilities. No monitor/scheduler was installed and no external message was sent here.

Design references: [OWASP forgot-password guidance](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html) and [MailKit mandatory STARTTLS behavior](https://mimekit.net/docs/html/M_MailKit_Net_Smtp_SmtpClient_ConnectAsync_2.htm).
