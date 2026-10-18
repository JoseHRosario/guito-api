# Persistent human login — configuration and rollout gates (#156)

## Current scope

API 0.27.0 adds server-managed user sessions; it does not replace Google business
authorization, agent keys or reporting identity. Source/test completion is not
provisioning or rollout approval. **Do not push this feature without José's shared
staging replacement approval:** `.github/workflows/ci-cd.yml` auto-deploys
`feature/**` to staging. Do not run deployment/migrations locally to test this guide.

## Wire contract

All new routes are POST on the API origin, use `credentials: include`,
`Content-Type: application/json`, exact trusted Origin and `X-Guito-Session: 1`.
Missing/untrusted Origin or wrong/missing header is 403; unsupported content types
are 415. Responses, including errors, are `Cache-Control: no-store`.

| Route | Body | Successful behavior |
|---|---|---|
| `/Auth/session/token` | `{code, codeVerifier, redirectUri}` | Validated PKCE exchange/Google identity; persist encrypted refresh credential; 200 short-lived tokens and cookie |
| `/Auth/session/refresh` | `{}` | Cookie authenticates its server session; validated Google renewal; 200 tokens and renewed idle expiry/cookie |
| `/Auth/session/logout` | `{}` | Idempotent 204; delete this cookie/session only; no ID token needed and no Google revoke call |

Both token responses have exactly `{idToken, accessToken, expiresIn,
sessionExpiresAt}`. `expiresIn` is seconds, `sessionExpiresAt` is an ISO-8601 UTC
time. `__Host-guito-session` has Max-Age 604800, Path=/, Secure, HttpOnly,
SameSite=Strict, no Domain. Google refresh tokens are never returned. The cookie
is bound to the API hostname: production and staging API hostnames keep separate
cookies even though both are under the same site. Browser same-site delivery is
not a substitute for the explicit Origin/header checks.

Permanent 401 is `{error:"session_expired",error_description:...}` and clears the
cookie; login returns to the requested route (no draft restoration). Retryable
provider/configuration/network/storage/concurrency failures are 503
`{error:"temporarily_unavailable",error_description:...}` with no cookie clearing
or idle renewal. Busy tabs should show/retry connection state, not redirect to
Google. A late refresh that lost a logout race returns retryable 503 and cannot
recreate the row; the next refresh sees 401. Only actual Google 400 invalid_grant
is classified as revoked refresh permission; even a 5xx body saying invalid_grant
is transient. Unverifiable, wrong-audience or expired renewed provider ID tokens
also preserve the session with 503; a cryptographically validated subject/email
binding or identity-policy failure is permanent. Missing refresh_token on establishment is 400
`refresh_token_required`; refresh may omit it and retain the encrypted stored one.
Code-exchange Google errors retain RFC 6749 shape/status but are sanitized, not
verbatim reflected text. Old token-only installations need a fresh explicit consent.
Legacy token/logout endpoints remain for existing clients; **new device signout
must never call `/Auth/logout`**, which revokes Google's grant.

## Encryption key configuration and separately gated field-name transition

Initial staging provisioning/deployment was owner-approved; staging currently has
`HumanSessionEncryptionKey` (historical deployed field, not supported by this
source). This review changes source only: it does **not** read or modify that
payload, rotate its value, provision another key, run migrations or deploy.

Runtime and deployment validation require **only `UserSessionEncryptionKey`**:
canonical base64 for exactly 32 bytes. The typed `SecretsPayload` property
deserializes JSON retrieved/decrypted by `ISecretsProvider` from SSM; it is not a
key literal or a second secret store. Missing, null, empty or invalid canonical
input fails closed (runtime 503 and deployment rejection). An old-only payload
is rejected. No key value is logged.

**Before any push or deployment of this source**, ask José to authorize renaming
the JSON field in the existing `/guito-api/staging` SecureString from
`HumanSessionEncryptionKey` to `UserSessionEncryptionKey`, retaining the **exact
same value** and removing the old field. Preserve every unrelated payload field,
parameter metadata and encryption settings. This is a rename, not key rotation.
Under that separate approval, first verify the currently deployed consumers and
rollback target can read the canonical field; otherwise stop for coordination.
Privately read back the exact parameter and verify the canonical value equals the
pre-change value, the old field is absent and unrelated fields/settings are
unchanged. Never print either value. Only after that gate and shared-staging
replacement approval may source be pushed/deployed. Account for the five-minute
secret cache and verify canonical-field consumers after deployment. No production
parameter change is authorized by this staging gate.

For any future owner-authorized provisioning (not authorized by this review):

1. Ask José to approve a new distinct encryption key for **each** environment. Do
   not read, create, generate, copy or provision live keys without that approval.
   Retain all unrelated runtime payload fields, Google human client credentials,
   bank/agent/API credentials and Data API database secrets.
2. Approved owner setup supplies a cryptographically random 32-byte AES key through
   the existing secure delivery process, base64 encoded into
   `UserSessionEncryptionKey` in `/guito-api/staging` or `/guito-api/prod`
   SecureString respectively. Never reuse a staging key in production. Placeholder
   schema only: `"UserSessionEncryptionKey": "<owner-provisioned-base64-32-byte-key>"`.
   The placeholder intentionally fails validation. Check SecureString type and the
   complete payload's 4096-byte standard-tier limit privately. No key goes in git,
   chat, shell arguments, screenshots, Lambda environment variables or this guide.
3. Read back the exact approved destination privately and validate decoded length
   without printing it. Existing `ISecretsProvider` SSM decryption/cache is reused;
   application parameter names/backend and IAM do not change. Local development
   setup is a **separate** owner operation using an ignored protected file and a
   local-only key, not a reason to copy production secrets. Fake keys exist only
   in tests. Build/hermetic tests require no credentials.
4. Missing/malformed key blocks deployment in the offline validation helper and
   fails runtime sessions closed with 503. Secret-provider cache lasts five
   minutes. There is one active key, not a multi-key rotation service: changing it
   makes existing credentials unreadable. Plan any approved rotation with session
   invalidation/re-login and deployment/cache coordination; never discard the
   old key or pretend an unreadable credential is a Google revocation.

## Next approved staging deployment gate (not executed by this simplification)

Before permitting any push, inspect staging's current deployed ref and migration
ledger through the approved identity; a sibling feature may already own shared
staging. Confirm José approves replacing that exact revision and the schema/key
changes. Migrations run through Actions before code replacement. José explicitly
requested one table migration while the feature is unmerged: the source now has
only `011_user_sessions.sql`, directly creating `user_sessions` and its expiry
index with `IF NOT EXISTS`. Hash/envelope checks, paired nullable lease fields,
AES-GCM envelopes/AAD and cookie/wire contracts are unchanged. The runner retains
its simple semicolon-separated statements and records the filename after success;
there is no rename/ALTER migration or DO-block support.

Earlier staging deployments applied `011_human_sessions.sql` and
`012_user_sessions.sql`, leaving the renamed `user_sessions` table. Those historical
ledger entries may stay: the new `011_user_sessions.sql` filename is not in that
ledger, so Actions runs its CREATE statements and records it. On a matching table
and index this is a no-op; dropping tables is not required. `IF NOT EXISTS` does
not validate/repair existing columns, constraints or index definitions. If staging
instead has an incompatible table/index or only the old `human_sessions` table,
pause for José's manual decision; do not automatically rename, copy or delete old
sessions. José reserves any needed staging cleanup. Do not automate table drops,
ledger deletion or arbitrary repair. No account/user ownership model, AWS resource
or IAM permission is introduced, and financial records must remain untouched.

`deploy/user-session-config.py` derives exact HTTPS UI origins from the base and
environment appsettings, emits nonsecret edge CORS and indexed app origin
configuration, and rejects empty/wildcard/path origins. Deployment removes stale
origin overrides before merging these values. It validates the existing key
without printing it and never provisions a missing value. The edge update is
fail-closed, with AllowCredentials true, custom session header allowed and no
wildcard origin. Exact POST session routes are reconciled as NONE **with** the API
integration target on both new and existing routes. All other business routes keep
the CUSTOM authorizer. Verify actual route selectors, CORS and integration targets
after deployment; source syntax checks are not deployed-state evidence.

## Owner-approved real-browser/Google acceptance gate (not executed)

Use only the approved staging environment and test identity; no Expense mutation
is necessary. Keep token/cookie/key values out of reports and screenshots.

1. Confirm the staging UI/API origins, certificate, configured Google client and
   redirect URI. Request only `openid email profile`, PKCE/state and offline access;
   ask for consent where required to obtain a refresh token. Do not accidentally
   add `include_granted_scopes=true` or broader grant scopes.
2. Verify actual browser preflight allows only that UI with credentials and
   X-Guito-Session, then login. Verify cookie **attributes only**, never its value.
   An untrusted/missing Origin request must be rejected without deleting a session.
3. Close/reopen the browser/PWA, then reboot the phone. After short-lived token
   expiry, reopen and confirm cookie renewal restores the requested route. A fake
   HTTP-host restart test does not establish real Android storage retention.
4. On a second browser/device establish another session. Sign out on the first
   after its ID token expires; the second must continue. Confirm no Google revoke
   call. Existing already-issued ID tokens can still pass their normal expiry
   checks; no immediate global revocation is promised.
5. Under separately approved safe fault conditions verify retryable network/provider
   failure preserves the session, no forced login, no expiry extension and no
   financial request replay. Genuine revoked/idle-expired sessions require login
   and return to the intended route, discarding unsaved form fields by design.
6. Verify the seven-day boundary via an explicitly approved isolated staging test
   arrangement or elapsed real time, not modifications of personal records or a
   production clock override. Verify schema constraints/conditional lease behavior
   on real Postgres separately from fake Data API tests. Record unperformed cases
   as unperformed; do not turn an owner waiver into a pass.

Production deployment, production key provisioning and Android owner acceptance
remain distinct approvals after staging evidence. No such operations are performed
by this implementation.

## Operational limits and rollback

- Seven days is a Guito **idle** limit, not a Google credential guarantee. Google's
  [OAuth token-expiration guidance](https://developers.google.com/identity/protocols/oauth2#expiration)
  permits Testing-mode refresh credentials with only name/email/profile/openid
  scopes beyond the otherwise seven-day Testing cap. Provider revocation,
  inactivity policies, account changes and the 100-refresh-token per account/client
  limit (oldest token invalidated) still apply. Many device logins may invalidate
  older Google refresh credentials independently of Guito's active-session policy.
- Google's [revocation guidance](https://developers.google.com/identity/protocols/oauth2/web-server#tokenrevoke)
  says revoking an access token can revoke its corresponding refresh token. This
  is why new logout deletes only the server session. It does not sign out Google
  or every other device. Offline logout must suppress UI bootstrap locally until
  a deliberate new login, even when server logout could not complete.
- Rotation/lost provider responses cannot be atomically committed with Postgres;
  ambiguous refresh-token rotation can eventually need new consent. No financial
  mutation is retried, and no operation ledger is introduced for this limitation.
- Successful refresh has a 20-second budget plus bounded awaited cleanup; leases
  last two minutes to recover killed Lambda workers. No refresh transaction holds
  database locks across Google HTTP. Session completion/invalidation is conditional
  on lease identity; logout deletion is final for that handle.
- Expired credentials are lazily deleted on attempted renewal. Abandoned rows
  otherwise remain encrypted until an owner-approved expiry cleanup; there is no
  new scheduler or automatic broad cleanup. Never delete active sessions merely
  to make a test pass. Do not expose row contents in support logs.
- Do not roll back to API builds querying `human_sessions`; they cannot
  use the current table. Roll back API/UI only to a reviewed `user_sessions`-compatible
  build that still accepts the deployed key field. Do not reverse DDL, add a second
  session store or alter migration history to make rollback appear safe. Keep the table/key
  intact. Legacy UI rollback may need explicit Google
  login and its legacy logout has grant-wide effects. Removing the key/table or
  rotating credentials is a separate owner-approved destructive operation.
- No-store responses, sanitized RFC errors and no body/header credential logging
  are required. Existing access logs may record path/status only, never token
  request bodies, cookies or OAuth parameters in URLs.
