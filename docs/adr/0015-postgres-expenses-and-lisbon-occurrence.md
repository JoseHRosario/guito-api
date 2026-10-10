# ADR-0015: Opt-in Postgres Expenses and Lisbon occurrence timestamps

Status: Accepted for staged implementation; production cutover is not authorized.
Issue: #128 (first implementation slice of #126).
Supersedes: the Expense-only storage assumptions of ADR-0005, ADR-0011 and ADR-0013.
Categories remain governed by ADR-0014.

## Decision

One configured `AppConfiguration:ExpenseStore` (`Sheets` by default, or `Postgres`)
selects the whole Expense repository: reads, writes and smoke cleanup switch together.
No dual writes, new database, ORM, driver or auth change. Postgres uses the existing
Data API client and ambient unit-of-work infrastructure. Production rejects Postgres
selection until the separately approved cutover removes the guard.

`005_expenses.sql` creates a resumable table and occurrence/identity index. Every
statement can be rerun after partial application by the existing semicolon parser.
IDs are application-generated UUID strings stored as text, never content hashes,
row indices or insertion-order identity. No update operation can change an ID.
The text PK permits a future `bank_transactions.expense_id` FK without type conversion;
this ticket does not add links or reconciliation. Repeated identical purchases get
separate IDs. Amounts use unconstrained positive finite `numeric`; invariant decimal
strings are explicitly cast on writes and read as text, never converted through double.
EUR is explicit and the only supported currency in this slice. Category labels and
creator attribution remain independent of the seeded category mirror.

`occurred_at`, `creation_date`, `update_date` are `timestamptz`. Database `now()` supplies
actual audit events (initial creation and update are equal); occurrence is never an
audit timestamp. HTTP renders all instants in Europe/Lisbon. New time-enabled clients
supply an explicit-offset ISO8601 `occurredAt` alongside the existing `date`. The
instant must match the submitted Lisbon date, local clock and timezone offset. Spring
DST gaps are rejected; autumn repetitions are accepted only with a valid explicit
offset. Inputs finer than PostgreSQL microseconds are rejected rather than rounded.
Date-only clients map to unambiguous Lisbon midnight, not midnight UTC or request time.
Missing dates are invalid. Output keeps compatible date datetimes plus occurrence,
currency and audit fields. Create and latest IDs are strings; latest orders occurrence
DESC then ID DESC and is bounded to 1–100 records. `storedOrder` is deprecated and
omitted in Postgres responses; callers must use `id` unchanged.

Smoke creates/reads are scoped by the existing `X-Sheet-Scope: smoke` header. Postgres
stores scope explicitly; DELETE `/Smoke/{id}` always predicates `scope = 'smoke'`,
regardless of header. Missing, stale and normal-scope IDs return 404. It cannot delete
normal Expenses. No general expense deletion endpoint is introduced.

## Compatibility and rollout

Sheets remains the default and production authority, with its frozen date-only layout
and transient absolute row IDs. Latest maps the real requested range start plus row
offset (including blank positions), never the slice-relative ordinal. Existing
legacy date-only creation and reporting formulas stay operational. The frozen schema
cannot retain intraday instants: such creates explicitly return 409 rather than
silently discarding the supplied time. Do not deploy the time-enabled UI against a
Sheets-authoritative environment. Sheets audit times remain null; none are invented.

Staging deployment can set `EXPENSE_STORE=Postgres`; an omitted setting preserves an
existing Lambda override, so routine redeploys do not silently roll back authority.
Production deployment pins Sheets. Coordinated API/UI staging rollout and separate
real create/list/persistence/cleanup acceptance are required. After Postgres accepts
writes, returning to stale Sheets requires reconciliation, not a simple config flip.
Import, exception/parity reporting, export and production cutover are subsequent slices.

## Verification

HTTP tests use the actual service/repository composition and fake Data API transport:
opaque IDs, exact decimals, attribution, legacy midnight, offset round trips, DST,
ordering and ties, bounded reads, production guard and scope-safe cleanup. Existing
Sheets/auth tests remain hermetic. Fake SQL execution does not establish PostgreSQL
DDL, privileges, persistence or constraints; real staging acceptance is separate.
