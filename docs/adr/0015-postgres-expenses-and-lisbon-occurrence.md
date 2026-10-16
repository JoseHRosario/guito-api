# ADR-0015: Opt-in Postgres Expenses and Lisbon occurrence timestamps

> **Status amendment — #150:** Amended by issue #150: Postgres is the only Expense persistence implementation in every environment. ExpenseStore/EXPENSE_STORE, Sheets compatibility adapters and legacy smoke are retired; the legacy X-Sheet-Scope header is ignored. ADR-0014 is amended for sole-Postgres categories. Exact amounts, occurrence/identity, revision and transaction contracts remain supported. Original staged-selector/Sheets-smoke rollout text below is historical, not an operational recipe. Deployment, live acceptance and credential destruction remain separate approval/evidence gates. See [the approval-gated retirement/live-acceptance runbook](../sheets-persistence-retirement.md).

Status: Accepted; sole-Postgres persistence amended by #150 as noted above. The staged selector/production-enable procedure below is historical; #150 deployment/live acceptance is separately approval-gated.
Later owner-accepted #132 production cutover and #133 start-gate disposition are recorded in [expense-cutover.md](../expense-cutover.md) and ADR-0018. Unperformed rehearsal statements remain historical evidence, not fabricated completion.
Issue: #128 (first implementation slice of #126).
Supersedes: the Expense-only storage assumptions of ADR-0005, ADR-0011 and ADR-0013.
Categories follow ADR-0014 as amended by #150: authoritative Postgres, unchanged name-only API.

## Decision

One configured `AppConfiguration:ExpenseStore` (`Sheets` by default, or `Postgres`)
selects personal Expense reads and writes. Smoke CRUD remains Sheets-specific.
No dual writes, new database, ORM, driver or auth change. Postgres uses the existing
Data API client and ambient unit-of-work infrastructure. Issue #132 removes the
original Production-only runtime guard: an explicit existing selector now works in
all environments; this code preparation does not authorize production execution.

### One-time enablement amendment (#132)

José rejected reusable pipeline cutover controls in PR #144. There is no second
Expense approval flag, write-pause middleware or new workflow-dispatch choice.
The owner coordinates a one-time source/API writer hold and drains in-flight
requests. Production keeps its Sheets default until an approved explicit Lambda
`AppConfiguration__ExpenseStore=Postgres` change. Routine deployments omit the
selector and preserve installed authority via the existing environment merge;
they no longer pin production Sheets. Reporting uses ADR-0017's existing verified
identity mechanism and separate production enablement approval.

Import/parity is owner-complete under #129; do not repeat it or recreate retired
provenance. The [runbook](../expense-cutover.md) separates merge/deploy approval,
one-time configuration, pre-switch abort and post-switch repair preserving new
Postgres writes. Staging readiness remains BLOCKED until real rehearsal/reopen
evidence; no production switch or source freeze is claimed.

`005_expenses.sql` creates a resumable table and occurrence/identity index. Every
statement can be rerun after partial application by the existing semicolon parser.
IDs are database-generated `integer GENERATED ALWAYS AS IDENTITY` primary keys,
returned as Int32 JSON numbers on the wire, never content hashes. Sheets compatibility
IDs are Int32 absolute row indices, not slice ordinals.
No update operation can change an ID. A future `bank_transactions.expense_id` FK must
use a compatible integer type; this ticket does not add links or reconciliation.
Repeated identical purchases get separate IDs. Amounts use unconstrained finite `numeric`; invariant decimal
strings are explicitly cast on writes and read as text, never converted through double.
Create accepts both the existing JSON numeric `amount` and an invariant decimal
string, accepting positive, zero and negative values exactly representable as .NET decimal.
This supersedes ADR-0010's Expense API positivity validation, not bank-provider normalization. Latest and AI extraction
responses have one invariant exact-text `amount` so clients can prefill/resubmit
without a JavaScript Number conversion. The property remains decimal in the DTO with
a property-specific converter; there is no duplicate amount field. The text expresses
the supported decimal value, not arbitrary precision beyond .NET decimal. Latest
returns null when amount is absent. AI extraction names its DateTime output
`bookingDate`, retaining offset-free midnight serialization and calendar-day meaning;
clients send that day as `yyyy-MM-dd`, not as an inferred instant.
EUR remains the only supported wire currency and is returned as a compatibility constant;
there is no currency column. `category_id bigint NOT NULL REFERENCES categories(id)`
uses the existing seeded mirror. The adapter resolves the submitted category name by
exact match and binds its Int64 identity; latest joins categories to return the unchanged
wire name. Unknown, blank or unmirrored names return 422 ProblemDetails with no insert.
Requests never seed categories. The `/Category` API remains Sheets-backed (ADR-0014);
mirror parity must be established before enabling Postgres writes. Creator attribution
still comes only from the authenticated identity.

`booking_date`, `create_date`, `update_date` are `timestamptz`. Database `now()` supplies
`create_date`; `update_date` is nullable, has no default and stays null on creation.
Only a future explicit user update may set it; no update operation exists in this slice.
The wire names are `bookingDate`, `creationDate`, `updateDate`.
Occurrence is never an audit timestamp. HTTP renders all instants in Europe/Lisbon.
Create has one required `DateTimeOffset BookingDate`, mapped to `booking_date`; its JSON
string is either `yyyy-MM-dd`
or an explicit-offset ISO8601 timestamp. The instant may use any valid explicit offset
(including UTC `Z`); the server normalizes it to Europe/Lisbon without changing the
instant or microseconds. Internal ExpenseDraft and ExpenseSummary carry only
BookingDate, with no duplicate calendar Date or OccurredAt. Summary BookingDate is
nullable for malformed legacy Sheets cells. Sheets derives its stored calendar day
inside the adapter from the normalized instant.
A known instant is not reinterpreted as a Lisbon wall clock. The removed request fields
`date` and `occurredAt` are rejected with 400 (including null and casing variants) using a targeted Infrastructure action filter over
unmapped properties (DTOs contain data only); other unknown properties remain tolerated. Latest returns
only `bookingDate` for occurrence. Time-enabled clients send latest `bookingDate` as create
`bookingDate`; legacy/prod clients send only `yyyy-MM-dd`. No database migration is needed. Naive local times, including
spring DST gaps and unresolved autumn repetitions, are rejected; time-enabled clients
must resolve local input using Lisbon DST rules before submitting an explicit instant.
Timestamp inputs with more than six fractional digits are rejected rather than rounded.
Date-only clients map to unambiguous Lisbon midnight, not midnight UTC or request time.
Missing dates are invalid. A property-specific converter in Infrastructure/Serialization
binds and validates the transport date; services do not parse strings.
A stateless `Services/TimeService` owns Lisbon instant normalization and legacy midnight
resolution; domain models contain no time logic or clock abstraction. Latest keeps
bookingDate, currency and nullable audit fields, without date/occurredAt aliases. Create
and latest IDs are Int32 numbers; Data API text/Int64 values are checked before
narrowing, independently of category/bank BIGINT IDs. Latest orders booking date
DESC then ID DESC and is bounded to 1–100 records. `storedOrder` is removed from
both adapters and their contracts; callers must use `id` unchanged.

Smoke creates/reads select the real Sheets adapter via `X-Sheet-Scope: smoke`, even
when Postgres is personal Expense authority. DELETE `/Smoke/{id}` always selects the
Sheets adapter independently of headers and deletes only its configured Smoke Test tab.
It never queries or deletes Postgres personal records; numeric IDs from the two stores
are not interchangeable. The Postgres adapter refuses deletion with 409 as defense-in-depth.
There is no Postgres scope column or scope index, and no general expense deletion endpoint.
The deployed smoke round-trip always sends date-only payloads and actually performs
Sheets create/list/delete; it does not prove Postgres persistence or DDL correctness.

## Compatibility and rollout

Sheets remains the default and production authority, with its frozen date-only layout
and transient absolute row IDs. Latest maps the real requested range start plus row
offset (including blank positions), never the slice-relative ordinal. Existing
legacy date-only creation and reporting formulas stay operational. The frozen schema
cannot retain intraday instants: such creates explicitly return 409 rather than
silently discarding the supplied time. Do not deploy the time-enabled UI against a
Sheets-authoritative environment. Sheets audit times remain null; none are invented.

Staging deployment can set `EXPENSE_STORE=Postgres`; omitted settings
preserve existing Lambda authority in either environment. Production defaults to
Sheets until the explicitly approved one-time selector change above. Coordinated API/UI staging rollout and separate
real Postgres create/list/persistence acceptance are required, separately from Sheets smoke.
Staging recreation of the unmerged 005 table and migration ledger is an explicit CI-only
test reset; it is not a new migration and is never a production reset. After Postgres accepts
writes, returning to stale Sheets requires reconciliation, not a simple config flip.
Import, exception/parity reporting, export and production cutover are subsequent slices.

## Verification

HTTP tests use the actual service/repository composition and fake Data API transport:
integer ID wire contracts and bounds, exact-text decimals, attribution, legacy midnight, offset round trips, DST,
numeric ordering and ties, category FK binding/missing-category 422, nullable update times,
bounded reads, explicit production Postgres composition and Sheets-only smoke cleanup. Existing
Sheets/auth tests remain hermetic. Fake SQL execution does not establish PostgreSQL
DDL, privileges, persistence or constraints; real staging acceptance is separate.
