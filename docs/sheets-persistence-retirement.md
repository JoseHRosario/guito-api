# Sheets persistence retirement and live acceptance (#150)

## Supported architecture and evidence boundary

Expense and Category authority is solely Aurora PostgreSQL through the existing
RDS Data API infrastructure, in every environment. GET `/Category` and AI expense
extraction use `DataApiPostgresCategoriesRepository` and alphabetical category
names. Preserve `{"categories":[{"name":"…"}]}`, internal Int64 IDs/FKs and
unknown-name rejection; no schema changes, reseeding, CRUD or automatic creation.

Sheets is presentation-only. The private Apps Script loader reads API history and
writes refreshed reporting tabs; it never reverse-syncs edits into the datastore.
Preserve human Google login/token exchange/logout, agent authentication,
`Google.Apis.Auth`, reporting identity/authorizer configuration, installer OAuth,
required Sheets API access and spreadsheet-write permissions. Removing API-side
Sheets persistence is not removal of Google authentication or reporting.

`ExpenseStore` / `EXPENSE_STORE`, API Sheets ranges/client/repositories and
service-account/key-file configuration are retired. `/Smoke/{id}` is removed and
must never map to personal cleanup. Any `X-Sheet-Scope` header is rejected with
400 before persistence writes, regardless of its value (including empty/unknown).
Supported Expense operations retain validation, revisions, transactions and
concurrency protections. There is no Sheets fallback, alternate scope or dual write.

**Evidence status at documentation preparation:** no #150 live gate has been run
and no credential has been revoked/deleted by this documentation change. Code
presence, local tests, CI deployment, live acceptance and credential retirement
are separate assertions. Historical #129 import evidence is complete for that
operation; imports/provenance must not be resurrected. #132 cutover records are
historical, not evidence for the retirement revision or present installed state.

## Approval gates

1. Owner approval of reviewed merge/deploy revision and environment/window.
   Deployment and migrations run through existing GitHub Actions; merging master
   may trigger production deployment. No local deployment fallback.
2. Separate approval of synthetic staging create/delete **after** datastore
   isolation is established. A staging endpoint, UUID marker or creator email is
   not isolation: creator attribution is not a per-user data boundary.
3. Separate production read-only acceptance and Apps Script refresh/recovery
   approval. A reporting refresh writes presentation tabs, not financial records;
   preserve archives/formulas and follow [the loader runbook](sheets-refresh.md).
4. Separate explicit destructive credential/permission retirement approval, naming
   exact targets and confirming exclusive ownership. Code removal is not approval
   to revoke a key, remove an ACL or delete a secret field.

AWS inspection/approved operations use `MinervaAIAgent` authority. A denial is a
blocker, not permission to use José's identity. Never edit `~/.aws`, output secret
values, publish tokens/financial rows, or store recovery secrets in git/pruned scratch.

## Isolated staging Postgres preflight

Before any mutation, an owner/operator must inspect the exact installed staging
API mapping, API/authorizer build stamps and Lambda configurations. Establish:

- Exact HTTPS origin maps to the intended staging API/function.
- Installed `Database__ResourceArn`, `Database__DatabaseName` and
  `Database__SecretArn` identify a disposable, non-personal Postgres database with
  exclusive non-production database credentials. Compare production references
  read-only; the `(resourceArn, databaseName)` pair and secret ARN must differ.
- The database contains no personal financial data; permitted categories already
  exist and no other users/jobs write concurrently. Verify actual credential
  privileges and consumers, not resource naming alone. No personal data copy,
  staging reset, schema provisioning or credential creation is authorized here.
- Deployment, configuration/domain remapping and concurrent writers are held for
  the entire run; initial readback is not a server-enforced lock. The reviewed
  code/config has not drifted since inspection. Pin Lambda
  `RevisionId` and `CodeSha256`; retain approval and inventory privately.

If any condition cannot be proven, mark the write gate **BLOCKED** and use
read-only checks only. Existing staging may contain imported personal records;
its name does not make it disposable. Do not fall back to those records.

The runner contract is also documented in [scripts/deployed-tests.md](../scripts/deployed-tests.md).
The runner requires `GUITO_STAGING_ISOLATION_FILE`, a protected operator-attested
JSON file outside git. It is evidence of a human approval, not an approval generator.
Populate these fields from actual inspection (never from this document's examples):

| Field | Required meaning |
| --- | --- |
| `environment` | Exact `staging` |
| `baseUrl` | Exact resolved HTTPS staging origin |
| `approvedBy` | Owner/operator approval attribution |
| `expiresAt` | Offset-aware UTC/ISO8601 expiry, future and within four hours |
| `resourceArn`, `databaseName`, `secretArn` | Exact installed isolated Data API references |
| `revisionId`, `codeSha256` | Exact installed staging Lambda configuration/code readback |
| `category` | Existing allowed Postgres category name for the synthetic Expense |
| `disposable`, `noPersonalData`, `exclusiveDatabaseCredentials`, `syntheticCreateDeleteApproved` | Each explicitly `true`, only when actually established |

`scripts/validate-staging-isolation.py` reads both Lambda configurations and
fails closed on target/reference/revision drift or missing attestation. It does
not prove database contents or privileges independently; the operator must prove
those facts before attesting. Re-establish the gate after code/config changes or
expiry. Keep this approval file out of shared logs and the repository.

## Approved live commands and round-trip

Read-only is the runner default. These commands contact deployed services and
resolve retained secrets, so run only inside the approved acceptance window:

```bash
scripts/run-deployed-tests.sh staging read-only
scripts/run-deployed-tests.sh production read-only
```

Only after the isolated staging preflight and separate write approval:

```bash
# Set GUITO_STAGING_ISOLATION_FILE to the protected, inspected approval file.
scripts/run-deployed-tests.sh staging round-trip
```

Production `round-trip` is forbidden/fail-closed. Do not invoke SIT via bare
`dotnet test` to bypass the runner's target/isolation controls. CI read-only runs
do not count as staging write acceptance; no automatic approval file is created.

The isolated test uses supported `POST /Expense`, captures the returned positive
integer ID, lists it through `/Expense/latest/100`, reads `/Expense/{id}` through
the retained auth paths, and verifies exact synthetic values. Cleanup runs even
if list/read assertions fail after ID capture: re-read only that returned ID,
verify this run's synthetic values and strong ETag/revision, then send
`DELETE /Expense/{id}` with the unchanged `If-Match`. Verify GET is 404 and the ID
is absent from latest afterward. Record unexpected orphan/cleanup outcomes.

Never infer a cleanup target from list position, category or marker search; never
refresh a revision to force deletion after stale/ambiguous results. On uncertain
create outcome without captured ID, timeout, changed values, stale revision or
cleanup failure, stop and reconcile the isolated datastore with owner approval.
No blind retry/reset or personal cleanup. Follow the existing
[revision/recovery contract](expense-edit-delete-contract.md#uncertain-outcome-recovery).

Production acceptance checks build/config readback and authenticated reads of
Category, latest/detail/category-month/history using existing records. Preserve
IDs, exact amounts and Lisbon occurrence/audit timestamps; attribute human/agent
failures to each gate. No production create, edit, deletion or bank sync. Retired
header/route no-write rejection is proven hermetically or in isolated staging,
not by attempting writes to production.

## Retained reporting/authentication verification

Verify human login/token exchange/logout and agent authentication remain intact;
a health check alone is insufficient. Confirm API and authorizer retain the
pinned reporting audience/subject and GET `/Expense/history` confinement. Under
separate loader approval, exercise full-page refresh, failure/recovery and owner
editor reopening per [sheets-refresh.md](sheets-refresh.md). Staging isolated
write acceptance does not establish production reporting configuration.

Preserve Google installer identity/OAuth consent and spreadsheet permissions;
these are not API-side Sheets service-account credentials. Keep loader/auth
verification evidence separate from ordinary agent/human SIT results. Hermetic
negative tests demonstrate reporting cannot authorize writes/other routes; no
production financial mutation is needed to demonstrate confinement.

## Credential inventory and destructive retirement

1. Inventory both environments and development machines read-only: API-side
   service-account identity/key IDs and owners, local key files, obsolete
   `GoogleServiceAccount` payload fields, Sheets persistence settings/ranges,
   Google spreadsheet ACLs and AWS/Google grants. Inventory metadata/field names
   only; never print decrypted credentials. Verify all remaining consumers,
   including shared roles, scheduled jobs, old deployment policy writers and
   unrelated projects. Shared service-account identity is not automatically
   exclusively owned by Guito.
2. Record each target's exact identity/location, last known consumer, exclusivity
   evidence and retention/recovery decision privately. Preserve `ApiKeys`, human
   OAuth payloads, OpenRouter/bank credentials, runtime SSM providers, database
   Secrets Manager/Data API grants and Apps Script/reporting identities/ACLs.
   Preserve unrelated fields in shared JSON payloads. Do not delete an entire
   environment parameter merely to remove one obsolete field.
3. After approved rollout and retained auth/reporting checks in both environments,
   obtain explicit owner approval for the exact destructive targets. Generate a
   redacted dry-run diff; abort on drift, ambiguity or permission denial. Select
   deletion/revocation ordering and protected recovery policy with the owner;
   revocation may be irreversible and no recovery copy is automatic.
4. Perform only approved removals: exclusively obsolete API-side key revocations,
   secret-field/config cleanup, proven-exclusive permissions/ACL removal and
   approved local-file retirement. Preserve unrelated statements and fields;
   read back each exact target after a mutation. Shared payload updates are not
   transactional across environments. Inspect partial state before retrying.
5. Record redacted field-name diff, key status/absence, permission/config readbacks
   and approval references; rerun retained auth/reporting and read-only API checks.
   Report blocked/pending targets separately. Never equate source removal or a
   successful update response with verified credential destruction.

## Evidence ledger and residual-reference audit

Record separate rows in the PR/owner evidence; no secret values or financial rows:

| Gate | Initial #150 evidence status | Evidence to attach |
| --- | --- | --- |
| Source/config/dependency audit | Runtime/current-reference audit completed on `feature/150-retire-sheets`, based on `49ffc0242fa5d6b138f8cb0a79060f13e84bd5a2`; allowed residuals classified below | Final commit ref and reviewed diff |
| Build/hermetic tests | Release solution build passed; 504 API, 79 authorizer and 23 guard tests passed via `dotnet test -c Release --no-build --filter "Category!=Integration"`; 31 Python and 28 Apps Script tests passed. Five pre-existing nullable warnings remain in the unchanged Bank pending test file; no new build errors | Final commit ref; hermetic tests used `DOTNET_USE_POLLING_FILE_WATCHER=1`; no live suites executed |
| Deployment | Not verified here | Owner approval, CI run, API/authorizer stamps/config readbacks |
| Isolated staging round-trip/cleanup | NOT RUN; blocked until proven isolation | Approval/attestation, exact refs, create/list/detail/delete/absence and orphan outcome |
| Production verification | NOT RUN; read-only only | Approval, build/config and authenticated read evidence |
| Retained auth/reporting refresh/recovery | NOT RUN for retirement revision | Environment, identity readbacks, loader result and owner editor reopen |
| Credential inventory/retirement | NOT PERFORMED; destructive operations NOT APPROVED by this document | Exclusive inventory, per-target approval, redacted diffs/readbacks and post-checks |

Audit tracked source, hidden config, tests, scripts, deployment and current docs
for retired repositories/client, selectors, service-account fields/key settings,
ranges, `DeleteSmokeAsync`, `/Smoke` and `X-Sheet-Scope`. Classify residuals:

- **Allowed historical:** ADR-0004/0005/0007/0008/0009/0011/0013–0016 rationale,
  archived #132 cutover checklist, #129 import records and migrations/seed SQL,
  dated #146 consolidation evidence. Each affected doc has a retirement/status
  note; historical commands are not runnable supported procedures.
- **Allowed supported reporting/auth:** `scripts/sheets-refresh/`, loader runbook,
  ADR-0017 and `SheetsReportingAuthentication` options, Google user auth, installer
  OAuth and spreadsheet-write scopes. These do not imply API-side Sheets access.
- **Allowed safety/audit:** retired-token rejection tests and removal inventory.
- **Unexpected active:** any runtime/deployment/operational path selecting Sheets
  persistence, consuming its key/ranges or instructing legacy smoke CRUD. Fix or
  report it; a historical allowlist does not exempt an active consumer.

This issue amends existing ADRs rather than adding a new ADR/solution member.
`CONTEXT.md` is absent at the inspected base; use existing `GLOSSARY.md` and fix
its pointers, without recreating a second glossary. #149 owns broader routing,
mandatory-context budgets and file restructuring. Coordinate overlapping root
and docs edits when integrating branches; do not absorb that refactor or future
reconciliation work into this retirement.
