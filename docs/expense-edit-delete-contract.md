# Expense detail, edit and permanent deletion (#146 / guito-ui#70)

Pinned API/UI contract before implementation. Personal Postgres authority only;
all three routes reject `X-Sheet-Scope: smoke` or Sheets authority with 409.
Existing shared authorised humans and agents can use them irrespective of creator.
Reporting identities remain confined to GET /Expense/history; they cannot use these
routes. `/Smoke/{id}` remains independent Sheets-only cleanup.

## Wire contract

GET `/Expense/7` returns 200, `Cache-Control: no-store`, a strong `ETag: "1"`, and:

```json
{"id":7,"amount":"-9007199254740993.1200","currency":"EUR","description":"Purchase","category":"Groceries","bookingDate":"2026-07-02T14:23:45.123456+01:00","creatorEmail":"creator@example.com","creationDate":"2026-07-02T15:00:00+01:00","updateDate":null,"revision":"1"}
```

Revision is an opaque string (never a JavaScript Number); pass the received ETag
unchanged. GET reflects current committed state, not an edit session snapshot.
PUT `/Expense/7` requires exactly one strong quoted If-Match and all editable
properties, replacing description/category/amount/bookingDate together:

```http
If-Match: "1"
Content-Type: application/json

{"description":"Corrected purchase","category":"Groceries","amount":"-9007199254740993.1200","bookingDate":"2026-07-02T14:23:45.123456+01:00"}
```

EUR is implicit; optional currency must be EUR. Amount accepts the established
exact decimal string or numeric token without rounding, including negative and
zero values. Detail returns the original stored exact numeric text. Equal numeric
amounts retain stored scale; unchanged occurrence retains its exact instant,
seconds/microseconds. PostgreSQL numeric does not retain a negative zero sign.
Date-only means Lisbon midnight; explicit-offset timestamps identify instants
normalised to Europe/Lisbon. Naive local timestamps (DST gaps or unresolved folds)
and fractions longer than six digits are 400. Clients resolve Lisbon local inputs
before submission. Removed date/occurredAt fields are 400, other extensions ignored.
Category is an exact name in the authoritative seeded mirror; unknown is 422.
Description may be empty; omitted/null editable properties are 400.

PUT returns 200 with the same detail shape and new ETag/revision. Identity,
creatorEmail and creationDate are immutable. Every accepted PUT, even equal values,
sets updateDate from the database clock and advances revision. No update-user
attribution is invented. DELETE `/Expense/7` with the loaded If-Match returns 204
with no body and permanently removes only that ID. No undo or blind targeting by
values/list position. Latest/history subsequently reflect the committed mutation.

## Errors and concurrency

Errors use ProblemDetails: unauthenticated 401/403 under existing gates; invalid
Int32 path token/overflow 400; missing If-Match 428; malformed, weak, wildcard,
unquoted, multiple or unsupported revision tokens 400; missing/already-deleted ID
404; existing ID with stale revision 412; scope/store unavailable 409; invalid
category 422. Preconditions and mutation are atomic, not read-then-write races.
A successful update makes every older revision stale, including equal-value edits.
Concurrent update/delete may produce 412 or 404 according to committed existence.
Provider failures never imply success; cancellation/timeouts may be ambiguous.

## Uncertain outcome recovery

Do not automatically retry PUT or DELETE after timeout/disconnection/5xx. GET the
exact ID: 200 establishes current revision/values, not who made them; equal values
are not proof this caller succeeded. 404 establishes current absence, not that this
caller deleted it. Retain the original intent and loaded revision separately and
ask the user to reconcile with current state. Only a received successful mutation
response acknowledges that operation. A fresh revision requires deliberate renewed
user intent; never silently overwrite another writer or label their work yours.

## Bank link handoff (#134/#135)

Master's legacy schema has nullable **text** bank_transactions.expense_id, no
Expense FK or separate review-state column; Pending is expense_id IS NULL.
Independently deployed #133 (migration `009_bank_review.sql`, version 0.20.0)
converts the link to **integer**, adds an Expense FK/unique link and a review-state
constraint. Staging has that schema already. Deletion inspects only column metadata
inside the transaction: legacy text links are cleared using canonical ID text;
integer links are bound as typed Int64 and atomically set to NULL with
review_status = 'needsReview'. Unknown/partial schema returns 409 and rolls back.
Unlinked Ignored rows and other Expenses' links are unchanged. Updates retain links.
The transaction locks the exact Expense before bank rows and clears its link before
deleting the Expense, never a Transaction. Future matching writers must also lock
Expense before bank rows. UI warns before amount/date/time edits on confirmed
matches when matching ships. No matching capability is added.

## Verification and rollout

Hermetic HTTP -> real repositories -> fake Data API tests are default. Predicate
sensitivity and races in a fake do **not** prove PostgreSQL locking, DDL/privileges,
foreign keys or persistence. Separately approved isolated staging acceptance is
required before production approval: pipeline applies migration 010 (009 is reserved
by #133); select Postgres
Expense authority and verify categories, authorised human/agent credentials and
reporting confinement. Do not run against personal production data, copy secrets,
or reset existing tables. Seed only uniquely marked disposable staging Expenses;
GET exact text/audit/ETag; PUT and read after reconnect; race same-revision PUT/PUT
and PUT/DELETE; stale attempts must not change rows; delete/read 404; verify linked
Transaction survives with expense_id NULL and review_status = 'needsReview' on the
#133 schema using operator-approved staging fixtures.
Verify latest/history and unrelated routes; clean only fixture IDs with their fresh
revisions. Ambiguous cleanup follows the recovery procedure, not blind retry.
Routine pipeline deployment and production enablement require owner approval;
local hermetic evidence does not claim deployed/live acceptance.

Release version is 0.21.0: master declares 0.19.0 and #133 reserves 0.20.0.
This branch does not include #133's review routes or migration. José explicitly
approved temporarily replacing its staging deployment with this master-based build
and intends to merge #146 before Bank Review (#147). Schema compatibility does not
mean feature parity: Bank Review routes will be unavailable until that story is
redeployed. Retain staging's already-applied migration 009 and its constraints;
never reset them to make the older application base work. Production rollout
remains gated on José's review and merge.
API Gateway CORS and both application policies expose ETag; gateway preflight must
allow PUT/DELETE and If-Match. Verify those installed settings after deployment.

## Review follow-up and request-scope regression

`ExpenseWriteValidationFilter` is deliberately shared by create and update: it
rejects removed occurrence aliases at the MVC boundary while request DTOs remain
data-only. `HttpExpenseRevisionResolver` owns header parsing behind the service
interface; it does not replace the repository's atomic revision check. Both detail
operations use the explicit `ExpenseDetailResponseMapper` at the service boundary.
Personal deletion is `IDeleteExpenseService` / `DeleteExpenseService`; the separate
Sheets smoke cleanup is `IDeleteExpenseRowService` / `DeleteExpenseRowService`.
The HTTP DELETE verb and reporting confinement are unchanged.

Human acceptance now exercises the real HTTP pipeline and Google SDK RS256
validation against a fake JWKS transport, with both authentication gates enabled
and no agent key. GET, PUT and DELETE succeed on another creator's Expense;
invalid signatures cannot reach storage. The middleware receives the SDK's
`ValidationSettings` through DI, with unchanged SDK defaults in production.
The test-only certificate-cache hook uses reflection because Google keeps that
hook internal; it changes only transport and does not replace signature, issuer,
expiry, audience or allowlist checks. There is no global cache replacement.

Unsupported/partial link metadata tests first produced six unexpected 204s while
the fake ignored its new metadata configuration, then passed with configurable,
predicate-sensitive schema reads. All six require 409, rollback, unchanged HTTP
Expense detail and all bank links, no commit and no unlink/delete statement.
Human success tests first produced three 403s with outbound certificate retrieval
blocked, then passed after wiring the per-host transport hook into SDK settings.

The live-reported Lambda failure was reproduced hermetically: resolving the detail
service makes an idle or abandoned request scope fail synchronous disposal because
`DataApiUnitOfWork` implemented only `IAsyncDisposable` (two failing cases).
The concrete unit now also supports `IDisposable`, waits for rollback completion,
and propagates rollback failure without clearing the open transaction context.
Coverage now drives the actual HTTP create/detail/latest/history/warm composition
through a test-only host lifecycle filter, without resolving infrastructure or
inspecting transaction context in the test methods. An abandoned HTTP creation is
rolled back and read back through HTTP; both disposal modes, pending rollback and
provider failure/recovery are exercised by the host filter. No test SQL or new
AGENTS seam exception is needed. This is a local regression fix, not deployed acceptance.

## Local test evidence

Resumption reproduced the missing ETag exposure (36 passed, 1 failed); the CORS
fix made all original 37 pass. The integer-link/state regression first failed with
500 against the legacy delete implementation, then passed with schema-aware
unlinking. Removing PUT's revision predicate made stale PUT return 200 and both
same-revision writers succeed (2 failures in 4 tests). Restoring CAS and removing
only the ID predicate failed exact-target update (1 failure in 1 test). All
sabotage was restored before rebuilding; the original endpoint coverage had 72
passing cases. Review follow-up passes 148 targeted cases and the full hermetic
solution suites (539 API + 79 authorizer). Replaying the original unit-of-work
against synchronous scope/HTTP tests reproduced the exact Lambda exception in
five of six cases; `/warm` remained successful because it does not resolve that
unit. Restoring the fix passed all nine scope regressions. These are local
HTTP-host/real-repository checks, not live PostgreSQL or deployed acceptance.

## Aggregate repository consolidation

The per-operation services now consume `IExpenseRepository` for `ReadAsync(id)`,
`UpdateAsync(id, revision, ExpenseEdit)` and `DeleteAsync(id, revision)`, alongside
existing create/latest/category-month operations. The port exchanges only Model
values, never wire DTOs. `DataApiPostgresExpenseRepository` contains all personal
Expense SQL, retaining the exact row locks, revision CAS, transaction boundaries,
amount-scale preservation, database audit clock and schema-aware bank unlinking.
Shared category, parameter and timestamp mapping remain inside Infrastructure.
The redundant detail port, adapter and unavailable adapter are removed.

`DeleteSmokeAsync(id)` is explicitly distinct on the aggregate port and both
adapters. `DeleteExpenseRowService` and `/Smoke/{id}` still clean only Sheets;
Postgres rejects this cleanup with 409. The compatibility Sheets adapter is
retained and rejects personal read/update/revision-aware delete with 409 without
external transport. `PersonalExpenseOperationFilter` guards only the three
personal routes on invocation (not controller/DI construction), before revision
resolution, so missing If-Match cannot accidentally admit an unavailable operation.
Existing create/latest/history guards and smoke routes remain usable.
`HttpExpenseRevisionResolver`, model/DTO boundaries, version 0.21.0 and migration
010 are unchanged.

Consolidation verification: four new HTTP store/smoke guard cases first failed
with 428 instead of 409, then passed. Removing synchronous unit disposal after
relocating all nine lifecycle cases behind HTTP produced six failures (including
the original Lambda IAsyncDisposable-only exception); restoring it passed. The
regular solution build passed with five pre-existing bank-test nullable warnings.
The targeted expense/smoke/host suite passed 277 cases; the full hermetic
`Category!=Integration` suites passed 543 API and 79 authorizer cases. Linux runs
used `DOTNET_USE_POLLING_FILE_WATCHER=1`. Removed-symbol searches include tracked
hidden configuration. No push, deployment, external comment or AWS write was made
for this consolidation.

Parent-reported independent deployed staging acceptance passed 48 checks for
**24fc9f730eddcd54fbc55ea0865e5a9e89b79403**, with cleanup counts zero. That
evidence predates this consolidation and does not validate its resulting commit;
publication and fresh deployed acceptance remain the parent/owner follow-up.
