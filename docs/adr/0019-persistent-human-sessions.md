# Persistent human sessions without replacing Google business authorization

## Status

Accepted design for #156; implementation is local until reviewed. Key provisioning,
shared staging replacement, real Google acceptance and production rollout are
separate owner gates. API version 0.27.0; companion UI 0.12.0.

## Decision

Every successful browser login establishes an independent persistent server
session. Seven days of **inactivity**, measured with the server clock, expires it;
successful foreground renewal extends it seven days. There is no absolute maximum.
The UI coalesces foreground/bootstrap renewal and must not run a background
keepalive. No draft persistence and no replay of financial requests are introduced.

The browser holds a 256-bit opaque capability in a persistent `__Host-guito-session`
cookie: HttpOnly, Secure, SameSite=Strict, Path=/, no Domain, Max-Age=604800.
The Data API persists only its SHA-256 hash, the bound Google subject/email, idle
expiry, and an AES-256-GCM encrypted Google refresh credential. A random 96-bit
nonce and 128-bit tag accompany each encrypted credential. The handle hash is
authenticated additional data, preventing credential-envelope substitution between
sessions. Plaintext refresh credentials exist only in bounded request memory and
Google's TLS token form; they never cross an HTTP response or logging boundary.

`SecretsPayload.HumanSessionEncryptionKey` is a base64 32-byte key in the existing
**per-environment** SSM SecureString runtime parameter, accessed through
`ISecretsProvider`. It is not added to the shared human OAuth client secret and is
not placed in Lambda environment variables. No new dependency, IAM permission,
service or plaintext fallback is introduced. Missing keys, wrong keys, unavailable
secrets and damaged envelopes fail closed with a retryable error, not session loss.
Provisioning is an explicitly owner-approved operation, never a deployment side effect.

New exact POST routes `/Auth/session/token`, `/Auth/session/refresh` and
`/Auth/session/logout` bypass the edge and in-app bearer/API-key gates, but enforce
an explicit trusted Origin, `X-Guito-Session: 1`, JSON, and their own PKCE/Google or
cookie authentication. Credentialed CORS is restricted to the same exact
per-environment UI origins. Redirect URI must be an allowed origin plus
`/auth/callback`. The UI still owns OAuth state/PKCE generation and callback state
validation; the server never claims an unvalidated browser state is server-owned.

New tokens are RS256/signature/JWKS, Google issuer, exact configured human client
and expiry validated, with nonempty subject, verified email and the existing human
allowlist. Refresh must preserve both subject and email. The new adapter reuses
the existing Google JWT verifier, with fresh request-bounded JWKS transport; the
reporting verifier/cache and business middleware validation are unchanged.
**A session cookie never authorizes business routes.** Humans still send Google's
short-lived ID token to both existing authorization layers. Agent and reporting
identities remain independent. Legacy `/Auth/token` and `/Auth/logout` remain
compatible; persistent-device logout never calls legacy revoke.

## Failure and concurrency semantics

Only missing/expired/deleted sessions, a validated identity change/rejection, or an
actual Google HTTP 400 `invalid_grant` on refresh are permanent 401
`session_expired` failures. Provider network/timeout/JWKS failures, invalid-client
configuration, 429/5xx, malformed/incomplete or unverifiable/wrong-audience/expired
renewed provider tokens, unavailable
storage/secrets and concurrency contention are retryable 503
`temporarily_unavailable`. They do not clear the cookie or extend idle expiry.
Code exchange retains the RFC 6749 response shape and upstream 4xx status; new
session endpoints sanitize descriptions/error codes instead of reflecting provider
text that could include credentials. Missing refresh permission is explicit 400
`refresh_token_required`, not pretend persistence. Existing sessions survive a
failed new login.

An atomic conditional Data API UPDATE acquires a two-minute per-session lease,
without an operation ledger or long database transaction across Google HTTP.
Refresh work is bounded to 20 seconds, with at most five seconds of awaited lease
cleanup; abandoned leases recover after two minutes. A competing tab receives a
retryable 503 without issuing another Google refresh. Completion and invalidation
are conditional on the exact lease; completion additionally checks lease and idle
expiry and is UPDATE-only. Logout deletes the row. A late worker cannot insert it
again or delete a newer lease. If logout races completion, no valid server session
can be resurrected, even if an already-issued cookie response arrives late. The UI
must independently suppress stale in-flight results after local signout.

Successful refresh preserves the old credential when Google omits refresh_token
and stores a newly encrypted credential when Google rotates it. Google HTTP and
database commit cannot be one atomic transaction: a lost rotated-token response
or failed database write can ultimately require consent again. No artificial
provider continuity or indefinite login guarantee is claimed.

## Consequences

Device logout deletes only that server session and cookie, never Google's shared
grant. Already-issued short-lived ID tokens remain valid until their Google expiry;
o global bearer revocation list is added. Other devices remain signed in. Offline
logout cannot promise server deletion until a successful request; the UI immediately
signs out locally and suppresses cookie bootstrap with its local signed-out marker.

Migration 011 is additive and normal pipeline-owned. Hermetic HTTP tests use signed
fake Google responses/JWKS, fake Data API and clock. They are not live PostgreSQL
locking, Google consent, cross-origin browser-cookie or Android restart acceptance.
See [provisioning and acceptance runbook](../persistent-human-login.md).
