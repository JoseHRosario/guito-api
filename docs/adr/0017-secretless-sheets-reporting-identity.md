# ADR-0017: Secretless, reporting-only Apps Script identity

Status: Accepted for implementation; live staging identity verification and production enablement remain gated.
Issue: #131 (reporting slice of #126).
Amends ADR-0003 with a third, narrowly scoped identity. ADR-0015 storage selection is unchanged.

## Decision

Use a **private standalone** Apps Script project and an authorized installable open
trigger, running as its installer. Obtain a short-lived Google OIDC ID token with
`ScriptApp.getIdentityToken()` using explicit `openid` and `https://www.googleapis.com/auth/userinfo.email`
scopes. Send the **same** token in `Authorization: Bearer <token>` (gateway identity
source) and `x-google-idtoken: <token>` (in-app contract). No long-lived Guito key,
Google refresh token, or API credential belongs in Google, cells or script properties.
The script installation/edit-access and refresh recovery procedure is documented in
`docs/sheets-refresh.md`; viewer-only opens are not promised.

The installer `sub` is authoritative, not an email allowlist. Both gates verify
RS256 against Google JWKS, trusted Google issuer, unexpired token, exact reporting
`aud`, exact pinned `sub`, a present nonblank email and boolean `email_verified=true`.
The reporting audience **must differ** from the UI OAuth audience. Missing/partial
reporting configuration cannot authorize reporting. An overlapping audience is a
fail-closed misconfiguration, not a way to grant the reporting installer human access.
No existing human allowlist is widened. Normal human email policy and agent-key
validation remain independent and unchanged.

Reporting authorizes **only GET `/Expense/history`**, with the API's case-insensitive
route spelling. It does not authorize POST, OPTIONS, other routes, trailing slashes,
prefix matches or subpaths. The gateway uses `requestContext.http.method/path`, not
an ARN suffix (HTTP API `$default` ARN is not the requested path). IAM Allow applies
only to the exact request `RouteArn`; authorizer cache TTL stays zero. Reporting
uses the standard Bearer header as the authoritative transport. The duplicate
`x-google-idtoken` header is optional; if present it must match at either gate.
Staging acceptance showed that the API received the Bearer token but not that
custom header. Requiring both rejected a valid signed identity; this correction
changes transport handling only, not signature/claim validation or route scope.
Unsigned audience inspection is only a dispatch hint; it never grants permission.
Selected reporting requests do not fall back to human or agent authorization.

The API has its own reporting middleware and JWKS validator. The edge reuses its
signed Google validator with explicit reporting subject/verified-email policy.
Signed/JWKS logic is duplicated across the independent assemblies by existing
repository policy; the API's existing Google human validation library is untouched.
Reporting has its own per-request marker and never sets the agent marker or creates
an unrestricted human principal. Public anonymous routes remain unchanged.

## Configuration and rollout

API options: `AppConfiguration:SheetsReportingAuthentication:{Audience,Subject}`.
Lambda environment equivalents:

- `AppConfiguration__SheetsReportingAuthentication__Audience`
- `AppConfiguration__SheetsReportingAuthentication__Subject`

Authorizer environment:

- `SHEETS_REPORTING_GOOGLE_AUDIENCE`
- `SHEETS_REPORTING_GOOGLE_SUBJECT`

Values are public identity selectors, **not secrets**. No audience or subject is
invented or committed. Before enablement, inspect an actual identity token issued
by the installed script; establish its signed audience and installer subject and
exercise both deployed gates on staging. A locally signed test fixture is not this
live acceptance. Do not enable production without José's explicit approval.

`deploy/deploy.sh` accepts:

- `SHEETS_REPORTING_AUTHENTICATION=preserve|disabled|enabled` (default `preserve`).
- `enabled`: supply both exact `SHEETS_REPORTING_GOOGLE_AUDIENCE` and
  `SHEETS_REPORTING_GOOGLE_SUBJECT` plus `SHEETS_REPORTING_IDENTITY_VERIFIED=true`
  only after the live identity check. Production additionally requires
  `ALLOW_PRODUCTION_SHEETS_REPORTING=true` representing explicit user approval.
- `disabled`: write blank selectors to **both** gates.
- `preserve`: retain an existing complete, consistent pair from both Lambdas;
  a new environment stays disabled. Partial, disagreeing or UI-overlapping installed
  selectors abort before migrations/code/config writes. Supplying claims in preserve
  mode is rejected rather than silently enabling reporting. There is no cross-environment
  reporting identity inference or copying from production.

The selector reads existing configuration before any deployment mutation. Denied
configuration discovery aborts; only a missing Lambda is treated as a new stack.
Routine staging deployments therefore cannot silently disable an installed trigger
or enable a half-configured identity. Both runtime gates still fail closed during a
non-atomic Lambda configuration update. Disable explicitly before rotating an
installer if uninterrupted reporting is not possible.

## Verification

Hermetic tests enter through API HTTP and `FunctionHandlerAsync`, signing local
RSA tokens and faking Google JWKS HTTP transport. They exercise history access,
subject and verified-email rejection, issuer/expiry/signature, header disagreement,
route/method confinement, partial/overlapping configuration, exact IAM resource,
`$default` ARN handling, and independent human behavior. Deployment selector tests
cover explicit enablement, new-stack disablement, installed-policy preservation,
misconfiguration and the production approval gate. No network credentials or live
writes are involved. Real Apps Script tokens, staging reopening/data parity and
production approval remain separate acceptance gates.
