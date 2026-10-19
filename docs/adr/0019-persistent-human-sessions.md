# Persistent user sessions without replacing Google business authorization

## Status

Accepted design for #156; API 0.27.0, companion UI 0.12.0. This unmerged ADR
was amended after José approved replacing Postgres session persistence with
DynamoDB. The previous Postgres-backed staging implementation demonstrated that
Aurora resume could exceed the 18-second readiness budget and prevent login.
Authentication must not wait for the financial database to resume.

José approved the DynamoDB dependency, fixed-capacity design, narrowly scoped
IAM changes, one fresh staging sign-in at cutover and the corresponding targeted
AGENTS.md update. This does not authorize production deployment, production key
setup, financial writes, Postgres session deletion or changes under ~/.aws.
Source/hermetic evidence, deployed staging evidence and owner Android acceptance
remain separate. See [the rollout runbook](../persistent-human-login.md).

## Decision

Each successful browser login establishes an independent server session. Seven
days of inactivity, measured with the server clock, expires it; successful
foreground renewal extends it seven days without an absolute maximum. The UI
coalesces foreground/bootstrap renewal and must not run a background keepalive.
No draft persistence or replay of financial requests is introduced.

The browser holds a 256-bit opaque capability in a persistent
`__Host-guito-session` cookie: HttpOnly, Secure, SameSite=Strict, Path=/, no Domain,
Max-Age=604800. Storage retains only its SHA-256 hash, bound Google subject/email,
idle expiry and an AES-256-GCM encrypted Google refresh credential. The existing
96-bit nonce, 128-bit tag, `v1` envelope and handle-hash authenticated additional
data remain unchanged. Plaintext credentials exist only in bounded process memory
and Google's TLS token form, never responses, browser storage or logs.

### Storage and access patterns

`IUserSessionRepository` remains the storage-agnostic aggregate port. Its adapter
uses the official AWS DynamoDB SDK, not Postgres or the shared financial Data API
transport. Every access addresses one session by `handle_hash` (String partition
key); no sort key, index, Scan, Query, transaction, Streams or operation ledger is
needed. Table keys are server-generated capability hashes, not client-selected
user or financial record identifiers. This is not a multi-user ownership model.

| Access pattern | DynamoDB operation and correctness boundary |
|---|---|
| Pre-exchange availability check | Non-mutating strongly consistent GetItem for a reserved non-session key; never an Aurora probe |
| Establish session | PutItem with attribute-not-exists condition; never overwrite an existing handle |
| Acquire refresh ownership | Conditional UpdateItem on an existing, unexpired item with no live lease; return updated item |
| Classify failed acquisition | Strongly consistent GetItem distinguishes missing/expired from a busy valid session |
| Complete renewal | UpdateItem only when item, exact lease, lease expiry and idle expiry remain valid |
| Release or invalidate | Conditional UpdateItem/DeleteItem bound to the exact lease identity |
| Device logout | Idempotent DeleteItem for that handle only |
| Remove abandoned expired records | Native TTL; eventual cleanup is never the authorization decision |

Expiry and lease comparisons retain UTC tick precision in numeric
`expires_at_ticks` / `lease_expires_at_ticks` attributes. Separate numeric `ttl`
is UNIX seconds derived from idle expiry and refreshed only after successful
establishment/renewal. Expired records must be rejected even while TTL deletion
is pending. Application credentials remain encrypted in `encrypted_credential`;
identity attributes are `subject` and `email`; ownership is `refresh_lease`.

### Isolation, permissions and cost

Region is Ireland (`eu-west-1`). Tables are `guito-api-user-sessions-staging` and
`guito-api-user-sessions`; `AppConfiguration:UserSessions` carries explicit
`TableName` and `Region`. No fallback to Postgres or implicit production table is
allowed. Financial-data persistence stays Postgres/Data API and may still incur
Aurora resume latency after authentication completes.

Each table uses Standard class and fixed provisioned capacity of one RCU and one
WCU. No autoscaling, indexes, global replication, PITR, customer-managed KMS key,
new log/audit subscription or other paid add-on is introduced. AWS-owned at-rest
encryption is retained, in addition to application-level AES-GCM. Deletion
protection is enabled. These are rebuildable login sessions, so requiring sign-in
again is preferable to a paid session-backup mechanism for this product.

AWS publishes an ongoing allowance of 25 provisioned RCUs, 25 provisioned WCUs
and 25 GB Standard storage, shared per Region/payer account. The approved tiny
configuration is expected to incur no DynamoDB capacity/storage charge while
within that allowance; this is not an unconditional whole-account billing
promise. José confirmed the empty table list was Ireland. The agent could not
inspect payer-level free-tier usage with its role. On-demand mode would charge
for requests and is deliberately not selected. See the runbook for primary
regional pricing sources and the distinction from temporary signup credits.

Runtime permissions are only GetItem, PutItem, UpdateItem and DeleteItem on the
two named table ARNs. The existing shared Lambda execution role also serves both
authorizers; this retains the current role topology rather than claiming that
shared-role IAM provides per-function isolation. Configuration chooses the table;
application boundaries and tests protect environment selection. Deployment has
only the named-table create/describe/TTL/backup-status permissions it needs. It
has no DeleteTable, Scan, UpdateTable or backup-enablement permission for sessions.
Provisioning runs through the existing approved Actions lane, not a local deploy.

### Encryption and authentication boundaries

`SecretsPayload.UserSessionEncryptionKey` remains the canonical base64 32-byte
key in each environment's existing SSM SecureString, through `ISecretsProvider`.
The staging canonical-field rename was separately approved and verified without
rotation; this store migration does not read/copy/rotate/provision a key. Missing
or damaged key/envelope fails closed with retryable 503, not silent session loss.
Production key provisioning remains a distinct owner operation.

Exact POST `/Auth/session/token`, `/Auth/session/refresh` and
`/Auth/session/logout` bypass edge/in-app bearer/API-key gates but enforce their
own exact trusted Origin, `X-Guito-Session: 1`, JSON and PKCE/Google or cookie
authentication. Credentialed CORS allows only the explicit environment UI origins.
The UI owns state/PKCE generation and callback-state validation. Redirect URI
must be an allowed origin plus `/auth/callback`.

Google RS256 signature/JWKS, issuer, client/audience, expiry, subject, verified
email and existing allowlist checks remain. Renewal must preserve subject and
email. A session cookie never authorizes business routes: humans still use
Google's short-lived ID token at both gates. Agent/reporting paths and legacy
`/Auth/token` / `/Auth/logout` remain unchanged. Device logout never calls legacy
Google grant revocation.

## Failure and concurrency semantics

Missing/expired/deleted sessions, validated identity rejection/change and actual
Google HTTP 400 `invalid_grant` during refresh are permanent 401
`session_expired` failures. Provider, configuration, secrets, invalid or
unverifiable renewed tokens, storage/throttling/network/permission failures and
refresh contention are retryable 503 `temporarily_unavailable`; they do not clear
the cookie or extend expiry. Code-exchange errors retain the sanitized RFC 6749
shape; missing refresh permission is explicit 400 `refresh_token_required`.

A two-minute durable lease serializes provider refresh without holding a database
transaction across Google HTTP. Establishment/renewal have one caller-linked
25-second operation budget; awaited refresh cleanup has an independent two-second
maximum. No 18-second Aurora readiness loop remains on session paths. SDK retries
must not silently replay uncertain session mutations. Lease cleanup/invalidation
must match the exact owner; completion also checks live lease/idle expiry and
must never upsert. Logout deletes the item; a late worker cannot resurrect it or
delete a newer worker's session. UI epoch/sign-out safeguards remain necessary
when responses arrive late.

Refresh may retain the existing encrypted credential when Google omits a new
refresh token or store a new encrypted credential on rotation. Google renewal
and DynamoDB writes cannot be one atomic transaction; lost rotation responses or
ambiguous completion may eventually need fresh consent. Neither indefinite
provider validity nor automatic retry of a one-use code/financial mutation is
promised. Logs retain categories/type names and correlation only, never raw
provider/storage exception payloads, tokens, cookies, identity or key material.

## Cutover and consequences

José chose one fresh staging sign-in rather than copying live refresh credentials.
There is no SQL-to-DynamoDB fallback, lazy import, dual write or migration sweep.
The unmerged `011_user_sessions.sql` is removed from source and solution; its
previously applied staging table and ledger entries remain untouched. No DROP,
ledger edit or automatic broad cleanup is part of deployment. Their eventual
removal is an explicit separate owner operation.

Rollback is not a seamless store conversion: the two stores do not share renewal
or logout state. Do not re-enable old Postgres session code blindly; it could
accept a stale session deleted only in DynamoDB. Keep the DynamoDB table/key and
review any rollback/invalidation plan with José. Financial migrations and
financial records remain unchanged.

Device logout affects only that session and cookie, never Google's shared grant.
Already-issued short-lived ID tokens keep normal validity until expiry. Offline
logout suppresses local bootstrap but cannot guarantee server deletion before a
successful request. Provider revocation/inactivity/token-count limits remain
independent of Guito's seven-day idle policy.

Hermetic HTTP tests use signed fake Google responses/JWKS, predicate-aware fake
DynamoDB transport and a controlled clock; a regression must make every financial
Data API call fail while login, renewal and logout still succeed. Fake tests do
not prove live DynamoDB conditions, Google consent, Secure/Strict browser-cookie
or installed Android retention. Staging and owner acceptance remain separate
verified gates, not conclusions inferred from the selected architecture.
