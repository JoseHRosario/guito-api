# Expense category/month query — Paperclip consumer contract

`GET /Expense?category=startup&month=2026-10` retrieves every individual matching
Expense in one JSON response. This is a read operation, not aggregation or totals.
It is available only when the selected Expense store is Postgres. Production remains
Sheets-backed: issue #139 does not authorize cutover or deployment.

## Authentication

Use HTTPS against the intended environment. An agent must send the **same valid
agent key in both headers**, with no Bearer prefix:

```http
GET /Expense?category=startup&month=2026-10
Authorization: <agent-key>
X-Api-Key: <agent-key>
Accept: application/json
```

The deployed API Gateway REQUEST authorizer's sole identity source is raw
`Authorization`; `X-Api-Key` alone cannot authenticate at the gateway. The API's
independent defense-in-depth agent middleware requires `X-Api-Key`; raw
`Authorization` alone cannot authenticate inside the API. Neither layer replaces
the other. Keep keys in the consumer's secret store, never in source, logs or URLs.

The existing human path is unchanged: an allowed Google ID token is sent as
`Authorization: Bearer <id-token>` at the gateway and `x-google-idtoken: <id-token>`
inside the API. A human token is not an agent key and invalid Google credentials
never fall back to agent authentication. Agent and human expenses share the existing
single-user visibility; `creatorEmail` is attribution, **not a filter**.

## Filters and ordering

- `category` is required and nonblank. Match the exact existing name in the Postgres
  `categories` mirror, including case and meaningful whitespace. No trimming, fuzzy
  match, category IDs or runtime seeding. URL-encode the name. `/Category` remains
  Sheets-backed and is not proof that a name has been mirrored into Postgres.
- `month` is required: exactly ASCII `YYYY-MM`, with a real calendar month and year
  0001–9999. For example `2026-10`, not `2026-1`, `2026-13` or a date/time string.
- Selection uses **bookingDate**, not creationDate. The range is the earliest instant
  of the Lisbon calendar month (inclusive) to the earliest instant of the next month
  (exclusive), each converted separately to UTC. Normally this is midnight; historical
  ambiguous midnights use the earlier instant, and nonexistent midnights advance to
  the first valid local time. Legacy date-only create validation is unchanged.
  For October 2026 it is
  `[2026-09-30T23:00:00Z, 2026-11-01T00:00:00Z)`; the October DST transition is included.
  December rolls into the next year. The last four-digit month uses PostgreSQL's
  year-10000 exclusive upper boundary.
- Order is `booking_date DESC, id DESC` (numeric identity breaks occurrence ties).
- No pagination, cursor, record cap or latest-endpoint limit. A known category with
  no matching Expenses returns `200` and `{"expenses":[]}`.
- `X-Sheet-Scope: smoke` selects Sheets and therefore returns `409` for this query,
  even when normal personal Expenses use Postgres. There is no smoke query fallback.

## Successful response

```json
{
  "expenses": [
    {
      "id": 123,
      "bookingDate": "2026-10-10T14:23:00.123456+01:00",
      "creationDate": "2026-10-11T09:00:00+01:00",
      "updateDate": null,
      "currency": "EUR",
      "amount": "-123456789.1234567890123456789",
      "description": "Original purchase",
      "category": "startup",
      "creatorEmail": "agent@example.com"
    }
  ]
}
```

This reuses the existing Expense read fields. IDs are Int32 JSON numbers; amounts
are invariant exact decimal **strings**, including zero and negative values, never
JavaScript floating-point numbers. Preserve the amount string without a Number
conversion. Timestamps render in Europe/Lisbon with the instant's actual offset;
audit timestamps are independent of bookingDate, and updateDate can be null.
There are no totals or extra amount/date aliases.

## Errors and all-or-fail completeness

Expected query errors use `application/problem+json` ProblemDetails:

| Status | Meaning |
| --- | --- |
| 400 | Missing/empty category or month; malformed/non-calendar `YYYY-MM`. |
| 422 | Nonblank category has no exact Postgres mirror match. |
| 409 | Sheets is selected, including smoke scope. No alternate Postgres dataset is queried. |
| 502 | Data API provider failure/response limit, unsupported exact database amount, or the complete JSON response exceeds the application budget. No partial array is returned. |
| 500 | Unexpected server failure, or the existing unapproved Production/Postgres cutover guard. |

Authentication errors retain the existing gateway/API contracts, not the query's
ProblemDetails contract: missing credentials normally yield 401 and invalid credentials
are rejected (403 inside the API; the gateway may deny earlier). Do not treat a gateway
failure as a successful empty result.

A `200` means the entire query result was returned. The SQL contains no LIMIT/OFFSET,
and a database failure cannot produce an empty/partial success. The
[RDS Data API terminates responses above 1 MiB](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/data-api.limitations.html).
The API does not silently page or truncate around that transport constraint.

After fetching and mapping every row, the API serializes the entire JSON before
starting a response and enforces a **2,000,000-byte UTF-8 JSON budget**, counting
escaped text and the full response shape. It emits the exact serialization checked.
This conservative budget leaves room for proxy-envelope JSON escaping or base64
expansion under [Lambda's 6 MB synchronous response quota](https://docs.aws.amazon.com/lambda/latest/dg/gettingstarted-limits.html),
which is tighter than API Gateway's payload ceiling. Exceeding the budget returns
502 ProblemDetails, never `200` with a shortened array or serialization-time truncation.
The database transport limit may be reached first. A future pagination decision is
out of scope; consumers must surface these failures rather than interpreting them
as no spending.

## Verification scope

Hermetic HTTP tests run controller → service → real Postgres repository → fake Data
API transport. Seeded transport rows independently exercise SQL predicates/order,
DST/range edges, attribution and precision. They do not prove live PostgreSQL execution,
DDL, privileges, persistence or deployed gateway behavior. Production cutover remains
separately gated. Run local verification without credentials:

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test --filter "Category!=Integration"
```
