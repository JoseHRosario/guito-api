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

## Verification

The endpoint's hermetic C# HTTP tests remain in CI, along with the existing
staging auth/business smoke suites. Full-history database parity is a one-time
acceptance check performed through the agent session, not a recurring pipeline
job. Record the exact deployed commit, all-page retrieval through both auth paths,
per-record stored parity and source-drift checks in the PR. No dedicated Python
verifier or tests of that verifier are retained.
