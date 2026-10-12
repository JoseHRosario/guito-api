# Expense history read

`GET /Expense/history?pageSize=100&pageIndex=0` uses the existing independent
human/agent authentication paths. Defaults are `100` and `0`; `pageSize` must be
1–100 and `pageIndex` a nonnegative Int32. Invalid input returns 400.

The JSON response is `{pageSize, pageIndex, totalCount, hasMore, expenses}`.
Each expense contains integer `id`, exact stored numeric-text `amount`, `EUR`
`currency`, Lisbon `bookingDate`, category label, description, creator email,
`creationDate` and nullable `updateDate`. Amount never passes through .NET decimal
or JavaScript Number; keep its text unchanged when consuming an export.

The only source is `expenses`, joined to the existing categories mirror for
labels. One SQL statement reads count and rows in the same PostgreSQL MVCC view,
ordered by `booking_date DESC, id DESC`. Offset arithmetic uses Int64.
Empty history and pages beyond the end return 200 with an empty array, the current
count and `hasMore: false`. No tables, writes, cursors or expiry mechanism are added.

**Pages are live, not a frozen export.** A write between requests may change the
count, shift rows, or cause repeated/missed rows across pages. Replaying a page
reads current values; clients requiring a stable archive must coordinate writes
outside this endpoint. Single-page consistency does not guarantee multi-page
consistency.

Sheets authority and `X-Sheet-Scope: smoke` return 409 without Postgres access;
there is no Sheets-history fallback. The existing unapproved Production Postgres
cutover guard remains in force. Latest/create/smoke contracts are unchanged.

Hermetic HTTP tests exercise real service/repository composition through
`FakePostgresDataApiClient`. They do not prove deployed database privileges or
provider response-size limits. Data API failures fail the request rather than
returning partial successful history. A bounded row count is not a byte-size
guarantee for unusually large descriptions/amounts.

## Verification scripts: purpose and execution

- `scripts/tests/test_expense_history.py` tests the acceptance verifier with fake
  HTTP/AWS transport. It runs in the hermetic CI job on every branch push. It
  requires no credentials or network and makes no real database changes.
- `scripts/verify-expense-history.py` is a deployed staging acceptance check, not
  part of the endpoint or normal Sheets refresh. After staging deployment, CI runs
  `GUITO_VERIFY_EXPENSE_HISTORY=1 scripts/run-deployed-tests.sh staging`. The runner
  resolves credentials and invokes the verifier, which fetches every page through
  both authentication paths and compares exact values with independently read
  stored Expenses. Source reads before/after detect drift. It writes no Expenses
  or schema, logs only counts/status, and refuses production.

These checks verify #130's hermetic and deployed parity criteria; they do not add
runtime export storage or run when Google Sheets requests the endpoint.
