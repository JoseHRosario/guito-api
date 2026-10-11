# Expense history reporting contract

API version 0.16.0; issue #130. See ADR-0017 for snapshot and retention decisions.

## Requests and authentication

Initial: `GET /Expense/history?pageSize=100` (pageSize optional, default 100).
Continuation: `GET /Expense/history?pageSize=100&cursor=<nextCursor>`.
Keep the initial pageSize unchanged, within 1–100. Omit cursor only to start a new
snapshot; empty/whitespace cursor is invalid. URL-encode returned cursors and treat
them as opaque, never construct them from IDs or offsets.

Use the existing authenticated agent or human path on **every** request. Deployed
agents supply the existing raw `Authorization` key plus `X-Api-Key`; humans retain
the existing edge Bearer token and API `x-google-idtoken` requirements. This endpoint
adds no credential or public route. Never put tokens/cursors in source control or
financial data in public caches. Cursor possession alone grants no access.

Only approved non-Production `AppConfiguration:ExpenseStore=Postgres` environments
expose history. Sheets authority or `X-Sheet-Scope: smoke` returns 409 with no store
activity; this endpoint never exports the Smoke Test tab or personal Postgres records
in smoke scope. Production's existing cutover guard remains intact.

## Response

HTTP 200, JSON, `Cache-Control: no-store, no-cache`:

```json
{
  "snapshotId": "d07d4728-e502-49d8-afb2-9a30c4eaa706",
  "expiresAt": "2026-10-11T22:30:00+00:00",
  "totalCount": 1,
  "expenses": [
    {
      "id": 42,
      "bookingDate": "2026-07-02T00:30:00.123456+01:00",
      "creationDate": "2026-10-11T22:00:00+01:00",
      "updateDate": null,
      "amount": "-123456789.1234567890123456789",
      "currency": "EUR",
      "description": "Original description",
      "category": "Restaurants",
      "creatorEmail": "original@example.test"
    }
  ],
  "nextCursor": null
}
```

The example is illustrative, not a deployed record. `snapshotId` and `totalCount`
remain unchanged throughout one export. `expiresAt` is the fixed database expiry
instant, serialized in its database offset; Expense timestamps use **Europe/Lisbon**
with DST-aware offsets, not a fixed +01:00. Amount is an invariant exact decimal
**string**, including zero/negative amounts, never a JavaScript Number. Preserve
stored numeric precision/trailing zeros; use an exact-decimal library for arithmetic.
IDs are Int32 JSON numbers, passed unchanged. `creationDate` is an actual record
lifecycle timestamp, not purchase time; `updateDate` is null until an explicit update.

Ordering is bookingDate instant DESC, integer id DESC. Identical purchases remain
separate. The initial statement freezes committed membership and every returned
value including category/creator labels. Later additions, backdated inserts, delayed
lower-ID commits, edits and category renames cannot alter the export. Empty history
has expenses `[]`, totalCount `0`, a valid snapshotId and nextCursor `null`.

A non-null nextCursor means another page exists. Exactly full final pages also return
null. Continue until null; require stable snapshotId/totalCount, unique IDs, valid
fields and total collected count matching totalCount **before replacing reporting
data**. Retry the same continuation token to get the identical page/next token before
expiry. Restart a failed/expired export rather than combining independent snapshots.

## Errors and operational behavior

- 400 ProblemDetails: malformed/empty/unknown cursor, unsupported/malformed pageSize,
  or pageSize changed during paging.
- 410 ProblemDetails: known cursor expired, including exactly at its 30-minute expiry.
- 409 ProblemDetails: Sheets authority, smoke scope or unapproved Production cutover.
- Existing 401/403 auth responses: missing/rejected agent or human credentials.
- Datastore/provider errors fail the request; incomplete persisted pages return 502,
  never a successful empty/partial page. No fallback to Sheets or capped latest.

GET is read-only with respect to authoritative Expenses/Categories but persists a
reporting snapshot/cursors and opportunistically removes expired snapshot payloads.
Header/cursor tombstones remain to preserve explicit 410 semantics; no scheduler is
introduced. Account for snapshot storage when operating high-volume consumers.
Migration 009 must be applied before enabling the endpoint. The existing deployment
runner grants runtime SELECT/INSERT/UPDATE/DELETE on all public tables after migration.
No new credentials or Production change is needed/authorized.

## Separate staging acceptance

The pipeline-owned `scripts/verify-expense-history.py`, enabled through
`GUITO_VERIFY_EXPENSE_HISTORY=1 scripts/run-deployed-tests.sh staging`, retrieves all
pages with authenticated requests, compares them against independent stored seeded
Expenses and checks source records before/after for parity/drift. It must run only
against staging, without smoke scope. The test reads authoritative data and does not
seed, update or delete Expenses. It does create the endpoint's internal reporting
snapshot. A green hermetic fake suite is not evidence of real PostgreSQL privileges,
MVCC/DDL correctness or a deployed acceptance pass. Deployment execution remains
pipeline-owned and was not performed during local implementation.
