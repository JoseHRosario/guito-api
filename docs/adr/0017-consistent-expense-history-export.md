# ADR-0017: Persisted consistent Expense-history export

Status: Accepted for staged implementation; production cutover remains unauthorized.
Issue: #130, parent #126. Extends ADR-0015; no change to Expense/category authority or authentication.

## Decision

`GET /Expense/history` is separate from the capped latest endpoint. A dedicated
Controller → operation Service → domain-only Repository port resolves only when
ExpenseStore is Postgres, the environment is not Production, and the request is not
Sheets smoke scope. Other scopes return 409 without constructing a database client
or accessing either store. Existing dual middleware and edge authorization apply
to every page; cursors are not authentication and creator attribution is not tenancy.

The initial request atomically persists the entire committed Expense view in one
PostgreSQL data-modifying CTE statement. A materialized source captures membership,
all Expense values, and joined category labels. Snapshot ordinal positions follow
booking_date DESC, integer id DESC. Header/count and copied rows derive from that
same source. A sequence maximum or audit/booking timestamp watermark is insufficient:
a transaction can reserve a lower identity and commit later, or insert backdated data.
No transaction remains open across HTTP requests. Subsequent pages read immutable
copied values rather than rejoining mutable Expenses or Categories.

`009_expense_history_snapshots.sql` adds snapshot headers, copied rows and cursors.
DDL is idempotent and compatible with the current semicolon migration runner.
Existing db-apply runtime-role blanket table grants cover these tables; no role,
secret, new service, ORM or TCP connection is added. Snapshot Expense IDs intentionally
have no live-Expense FK: later live deletion must not invalidate the frozen export.

A cursor is a cryptographically random UUID v4 persisted in a cursor table. The
snapshot UUID is distinct and cannot itself retrieve a page. Server-side cursor
state contains the snapshot, offset and fixed page size, so callers cannot forge or
edit an offset, snapshot, expiry or page size. The unique snapshot/offset/size key
makes next-cursor issuance deterministic under retries and concurrent replay.
Authenticated callers share the same personal reporting authority; tokens are not
bound to a creator email because this is not a new ownership model.

Snapshots expire 30 minutes after database creation. PostgreSQL's clock determines
expiry, with equality expired. Expired known tokens return 410; malformed, empty,
unknown and changed-page-size tokens return 400. UUID tokens are opaque to clients.
A new initial request starts a fresh export. Headers and cursor tombstones remain so
410 stays distinguishable from unknown tokens. On each initial request the same
statement removes expired snapshot payload rows. There is no background job; if no
new export is requested, expired payloads remain stored but inaccessible. Metadata
is retained indefinitely in this slice; an approved future retention policy can
bound that footprint, but deleting tombstones changes old tokens to unknown/400.

Page size defaults to 100 and is bounded 1–100, independent of latest's cap. A page
must contain exactly the expected number of rows or fail, not succeed partially.
Data API/provider errors fail the whole request; there is no silent truncation or
page skipping. Responses are no-store. Initial GET performs internal reporting-cache
writes/expiry cleanup but never changes authoritative Expenses or Categories.

History amount is exact PostgreSQL numeric text copied once, never parsed through
double or decimal. Unlike create/latest's .NET-decimal representability constraint,
export can report any stored finite numeric precision without silently rounding it.
This is a separate reporting contract, not an expanded create contract. Currency is
EUR (ADR-0015). Occurrence/audit timestamps retain instants and microseconds and render
with Europe/Lisbon's applicable offset; updateDate remains nullable.

## Verification and limits

Hermetic HTTP tests retain real controller/service/repository composition and fake
Data API transport. The fake models copied data, predicates, ordering, expiry and
uncommitted lower-ID rows; it does not establish actual PostgreSQL MVCC, privileges,
DDL or provider response limits. Separate pipeline-owned staging acceptance compares
all API pages with independently queried seeded Expenses and checks source parity
before/after. No local/live infrastructure or production operation is authorized by
this implementation. Existing deployed human-auth checks remain necessary; local
history tests cover valid agent and missing/rejected human credentials without
introducing an auth bypass or modifying Google signature validation.
