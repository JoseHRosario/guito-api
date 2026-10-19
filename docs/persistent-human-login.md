# Persistent user login — DynamoDB configuration and rollout (#156)

## Scope, decisions and approval boundaries

API 0.27.0 implements server-managed device sessions; Google remains the business
request authority. [ADR-0019](adr/0019-persistent-human-sessions.md) defines the
current design. The earlier Postgres-backed attempt and resume-budget regressions
are retained as [historical evidence](user-session-cold-start-verification.md),
not instructions to retain Aurora in login.

José approved replacing session persistence with DynamoDB, the official SDK,
fixed provisioned capacity, narrowly scoped deployment/runtime IAM changes, one
fresh staging sign-in at cutover, and the targeted AGENTS.md session update.
The existing feature staging lane is the rollout path. Feature pushes automatically
replace shared staging, so compare its served revision and confirm that replacement
is still within the owner's approved scope before publishing. Production deployment,
production key setup, live financial writes, personal session migration/destruction
and modifications under ~/.aws are not authorized by this change.

## Wire and security contract — unchanged

All session routes are POST on the API origin with `credentials: include`,
`Content-Type: application/json`, an exact trusted Origin and `X-Guito-Session: 1`.
Missing/untrusted Origin or wrong/missing header is 403; unsupported content type
is 415. All responses, including errors, are `Cache-Control: no-store`.

| Route | Body | Successful behavior |
|---|---|---|
| `/Auth/session/token` | `{code, codeVerifier, redirectUri}` | Validate PKCE/Google identity; persist encrypted refresh permission; return tokens/cookie |
| `/Auth/session/refresh` | `{}` | Authenticate cookie/session; renew Google tokens; slide seven-day idle expiry |
| `/Auth/session/logout` | `{}` | Idempotent 204; delete this device's session; clear cookie; never revoke Google grant |

Token responses contain exactly `{idToken, accessToken, expiresIn,
sessionExpiresAt}`. `expiresIn` is seconds; `sessionExpiresAt` is UTC ISO-8601.
`__Host-guito-session` has Max-Age 604800, Path=/, Secure, HttpOnly,
SameSite=Strict and no Domain. Staging and production API hostnames keep cookies
separate. SameSite is not a substitute for Origin/header validation.

Session cookies never authorize business routes. Existing Google signature,
issuer, audience, expiry and identity checks at both gates remain unchanged;
agent and reporting paths remain independent. Legacy `/Auth/token` and
`/Auth/logout` remain available, but persistent-device sign-out never calls
legacy Google revoke. Request only `openid email profile`; no scope broadening.

Permanent missing/idle-expired/deleted/definitively revoked sessions return 401
`session_expired` and clear the cookie. Transient storage/provider/key/configuration,
network and lease-contention failures return 503 `temporarily_unavailable`,
without cookie clearing or expiry extension. Only actual Google HTTP 400
`invalid_grant` is a revoked refresh permission, not a 5xx body containing that
text. A validated identity change/policy rejection is permanent; an unverifiable,
wrong-audience or expired renewed ID token is transient. Missing refresh permission
at establishment is 400 `refresh_token_required`.

Never log session handles/hashes, identity, credentials, encryption keys,
OAuth code/verifier, request/response bodies or raw exception messages/objects.
Session diagnostics are categorical stage/type/correlation/timing only.

## Runtime and table configuration

| Environment | Region | Table |
|---|---|---|
| Staging | `eu-west-1` | `guito-api-user-sessions-staging` |
| Production — separate rollout gate | `eu-west-1` | `guito-api-user-sessions` |

Configuration is `AppConfiguration:UserSessions:TableName` and `Region`; deployment
emits the corresponding `AppConfiguration__UserSessions__*` environment variables.
Never substitute a production table during local/staging testing. Missing or
invalid configuration fails sessions closed; there is no SQL fallback.

- Standard table, String HASH key `handle_hash`, no sort key or indexes.
- Fixed provisioned **1 RCU / 1 WCU per table**, no autoscaling.
- Existing AES-GCM `v1` encrypted credential envelope and handle-hash AAD unchanged.
- Numeric UTC-tick fields `expires_at_ticks` and `lease_expires_at_ticks` retain
  application comparison precision; `refresh_lease` identifies the owner.
- Numeric `ttl` is UNIX seconds derived from idle expiry. Update it after successful
  establishment/renewal only. DynamoDB deletion is eventual: expired items must be
  rejected immediately by application predicates even if the item still exists.
- AWS-owned at-rest encryption, deletion protection on; no customer-managed KMS
  key, Streams, PITR, global replication, paid backup or new audit subscription.
  Losing a session requires sign-in, not financial-data recovery.

### Access and timing

`IUserSessionRepository.EnsureReadyAsync` uses a non-mutating strongly consistent
GetItem before consuming a Google authorization code. It does not query Postgres,
DescribeTable or wake Aurora. Table/schema/settings checks belong to deployment.
No old 18-second database-resume retry loop remains on session paths.

Establishment/renewal retain one caller-linked **25-second operation budget** across
readiness, lease, secret/crypto, Google and completion. Refresh cleanup is awaited
under a separate **two-second maximum**; durable leases expire after **two minutes**.
These are ceilings, not accepted normal login latency. Google transport deadlines
remain shorter; observed Lambda/gateway limits are 30 seconds and the UI session
HTTP limit is 35 seconds. No keep-warm scheduler or provisioned concurrency.

Atomic item conditions enforce creation without overwrite; active idle expiry and
lease availability on acquire; exact lease plus live idle/lease expiry on completion;
and same-owner cleanup/invalidation. Completion/release never create a missing item.
An acquire conflict uses a strongly consistent read to distinguish busy valid
sessions (503) from expired/missing sessions (401). Lazy expiry deletion must not
remove a session concurrently renewed since that read. Logout is handle-local;
a late refresh must not recreate it. The SDK must not silently retry uncertain
mutations. A failed financial request or uncertain one-use code exchange is never
automatically replayed.

Google refresh and DynamoDB completion cannot be atomic together. Lost rotated
credentials or ambiguous completion can ultimately require consent again. Do not
promise indefinite Google permission or zero provider/Lambda latency. Subsequent
Expense/Category/Bank reads still use Aurora and can wait for its resume.

## Encryption key — retain existing canonical configuration

`SecretsPayload.UserSessionEncryptionKey` is a typed field deserialized from the
existing per-environment SSM SecureString through `ISecretsProvider`, not a key
literal or second secret store. Require canonical base64 of exactly 32 bytes;
missing/null/invalid/old-only input fails closed. No `HumanSessionEncryptionKey`
compatibility alias remains.

The staging field rename to canonical `UserSessionEncryptionKey` was separately
approved and privately verified with the exact value and unrelated fields/settings
preserved. The DynamoDB migration neither renames nor rotates it and needs no
SSM payload mutation. Never copy staging encryption material to production.

Any future key generation, provisioning, rotation or production setup is a separate
owner gate. Keep key values out of source, chat, argv, Lambda environment variables,
logs, screenshots and recovery files. Deployment validates an existing value and
never supplies a fallback. A key change can make existing credentials unreadable;
plan consent/session invalidation deliberately instead of treating decryption
failure as Google revocation. Build/hermetic tests use fake keys and need no secrets.

## Infrastructure and IAM

Storage lifecycle is now defined by the retained environment-specific session
stacks and import-only shared Postgres stack in
[ADR-0020](adr/0020-cloudformation-storage-ownership.md). The
[storage runbook](storage-infrastructure.md) covers explicit import plans,
exact-change-set execution and readback. Ownership transfer does not reset tables,
copy credentials or change the session wire contract.

José's current infrastructure decision is **Finish DynamoDB; pause Aurora
adoption**. Aurora is TEMPLATE ONLY, not adopted or deployable. Supplied operator
evidence records denied `rds:ModifyDBCluster` during import despite a full baseline
match, an empty `guito-postgres-shared` stack in `ROLLBACK_COMPLETE` with native
resources 0/unowned Aurora, and identical before/after database settings and
identities. No RDS Modify grant, import retry or empty-stack deletion is approved.
Normal deployment gates only on session-stack ensure plus the native DynamoDB
verifier before unchanged financial SQL migrations/code; unmanaged Aurora is not
an adoption prerequisite. See [the storage runbook](storage-infrastructure.md).

Routine infrastructure/code rollout runs through the existing GitHub Actions lane,
not a local `deploy.sh` invocation. CloudFormation is the provisioning owner;
`deploy/user-session-dynamodb.py` only verifies native metadata after stack
reconciliation. It must treat only typed ResourceNotFound as missing, fail rather
than create an unmanaged table, and never repair TTL directly. It reads exact
settings back and fails closed on access denial, malformed schema, wrong
mode/class/capacity, indexes, customer-managed encryption or paid-backup drift.

The original `deploy/user-session-provisioner-policy.json` grants the existing
`guito-api-deploy` role CreateTable, DescribeTable, DescribeTimeToLive,
UpdateTimeToLive and DescribeContinuousBackups on the two fixed table ARNs.
Those native permissions support CloudFormation and metadata verification; the
Python helper no longer uses them as a competing provisioning controller.
The separately approved CloudFormation deployment/inspection policies are
versioned alongside it. Their addition must be exact-readback-verified before
adoption. No operator DynamoDB item grants, List/Scan/DeleteTable, new RDS
create/modify/delete authority or financial permissions are introduced.

The normal deployment installs a separate `guito-api-user-sessions` runtime policy
on `guito-api-lambda-role` containing only GetItem, PutItem, UpdateItem and DeleteItem
on the two named table ARNs. Preserve all other inline/attached policies. The role
is already shared by API/authorizer and both environments: this design does not
claim IAM isolation between those functions. The authorizer does not acquire a
session consumer; application configuration and path boundaries remain explicit.
After rollout, read back the exact runtime policy with the approved operator role.

## Cost contract

Checked public Ireland pricing on 19 October 2026. AWS publishes an ongoing
allowance of **25 provisioned RCUs, 25 provisioned WCUs and 25 GB Standard storage**,
shared per Region/payer account, not per table. Staging and production's tiny fixed
capacities are expected to remain free within that allowance. José confirmed the
console's empty table list was Ireland; payer-level quota consumption in linked
accounts was not inspected. This is not a promise that the entire AWS bill is zero.

Beyond the allowance Ireland rates are $0.000147/RCU-hour,
$0.000735/WCU-hour and $0.283/GB-month. On-demand request charges are not covered
by the provisioned throughput allowance; temporary signup credits are not the
basis for this design. Do not enable autoscaling or paid extras to fix throttling
without discussing the cost impact with José. Bursts may be throttled at the fixed
limit; preserve the session and return retryable failure rather than forced login.

Sources: [official pricing](https://aws.amazon.com/dynamodb/pricing/),
[Ireland regional price file](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonDynamoDB/current/eu-west-1/index.json),
[always-free allowance](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Introduction.html#Intro.Pricing),
[TTL semantics](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/TTL.html).

## Staging cutover and retained Postgres state

José explicitly selected **one fresh staging sign-in**, not migration of live
refresh credentials. There is no import, dual write or read fallback. A previous
Postgres-only cookie will get the normal missing-session result and need a new
Google sign-in; new logins then establish DynamoDB sessions.

Remove unmerged `011_user_sessions.sql` from source and solution. Do not execute
DROP/DELETE/TRUNCATE, rewrite historical staging ledger rows or modify any live
Postgres session/financial table. Applied historical session migration names may
remain in staging's ledger. Stale table/row removal is José's later separate
operation, not automatic deployment cleanup. Existing financial migration runner
semantics are unchanged.

Before a staging push, verify PR #157 remains open, the intended remote/local heads
and staging's served stamp; ensure no unmerged sibling is being overwritten outside
approval. Preserve paired UI #76; no session wire or frontend behavior change is
required by this store replacement. Keep the PR draft until outstanding live gates
are disposed of. A source push does not approve production.

## Verification gates

1. **Hermetic:** build and run `dotnet test --filter 'Category!=Integration'`, Python
   deployment/IAM tests and retained Apps Script tests. Use real HTTP session
   controller/service/repository composition with a predicate-aware fake DynamoDB
   client, signed fake Google/JWKS and clock. Cover expiry before eventual TTL deletion,
   busy/stale leases, logout races, transient errors, crypto/identity, cancellation,
   shared deadlines, awaited cleanup and no sensitive logging. Make every financial
   Data API call throw while login/renewal/logout still work; prove that test failed
   before replacing the Postgres adapter. No credentials/network/live database in CI.
2. **Deployment:** record exact-head workflow, table ACTIVE/mode/class/schema/capacity,
   deletion protection, TTL, no paid extras and exact runtime IAM policy readbacks.
   Check the served API stamp and actual session CORS/boundaries. Health alone
   does not prove DynamoDB, Google or cold-path acceptance.
3. **Real staging owner acceptance:** use staging UI/PWA and test identity. Sign in
   afresh after cutover. Verify restart/reopen, renewal after short-lived token expiry
   and sign-out without Google grant revocation; other devices remain usable.
   Confirm sign-in/renewal/logout do not depend on Aurora being awake. Do not force
   financial writes or modify DB contents merely to induce coldness.
4. **Remaining explicit tests:** distinguish hermetic race/expiry evidence from live
   DynamoDB conditional behavior, real Google consent/cookies, installed Android
   reboot retention and seven-day inactivity. Document each unperformed/waived
   experiment honestly. Any live fake-session writes require a separately approved
   exact staging table/record plan; financial writes are not needed.
5. **Production:** separate owner review/merge, production key/readiness and rollout.
   Staging success is not production execution authority.

## Rollback and operational limits

Do not blindly roll back to the Postgres session adapter: stores do not share
renewals or logout, and stale Postgres sessions could become accepted again. Keep
DynamoDB tables, key and IAM intact; prefer a reviewed DynamoDB-compatible build.
Any return to another session authority requires an owner-approved invalidation
or migration plan. No destructive rollback or duplicate-store fallback is supplied.

Device logout does not invalidate already-issued Google ID tokens before their
normal expiry. Offline logout blocks local bootstrap but cannot prove server
deletion until the request succeeds. Google's grant revocation, inactivity and
per-account/client token-count policies remain independent of Guito's idle policy.
Never expose stored session contents to diagnose those cases.
