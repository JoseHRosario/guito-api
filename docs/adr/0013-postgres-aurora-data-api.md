# Postgres (Aurora Serverless v2 + Data API) becomes the Bank Transactions store

Date: 2026-10-05 (decided in the 2026-10-03 grilling session, T4 evolution — issues #81/#82)

## Status

Accepted

## Context

ADR-0004 kept Google Sheets as the store while flagging Postgres as the anticipated future store (ADR-0011 made the repository pattern the seam for exactly this migration). The Bank Transactions aggregate (Enable Banking sync) is the first datastore decision after that seam exists. Bank transactions are *relational* by nature — account → transactions with an FK, a UNIQUE dedup key, and "unmatched vs matched" state queried by predicate — which is awkward in Sheets (no joins, no constraints, no indexes, row-index-as-id). Sheets stays the Expense/Category store; no data migrates.

Enable Banking facts that shaped the design (from the EB API reference):

- EB transactions carry **no unique transaction id**. Idempotent storage must be built from a composite: account uid + booking_date + amount + credit_debit_indicator + entry_reference + counterparty → hashed into a `sync_key` (UNIQUE constraint, `ON CONFLICT DO NOTHING` on re-sync).
- Only `status=BOOK` rows are final; PDNG/INFO rows are skipped at the fetch boundary.
- Amounts arrive positive with a `credit_debit_indicator` (DBIT/CRDT); only DBIT (expenses) is stored for now (ADR-0010 still holds).
- Auth is a **PS256** (RSA-PSS) JWT, fresh per request — not RS256 as ADR-0004 originally said.
- Pagination via `continuation_key` until null; fetch strategies `longest` (history) vs `default` (date range).

## Decision

**Aurora PostgreSQL Serverless v2, accessed exclusively through the RDS Data API.**

- **Aurora Serverless v2 with min 0 ACU**: scales to zero-ish idle cost — an on-demand personal expense tracker must not carry a running bill.
- **Data API over HTTPS, not a TCP driver**: the Lambda stays **VPC-less** — no VPC, no subnets, no NAT gateway, no private-endpoint plumbing. The database URL is callable like any other HTTPS dependency.
- **Raw SQL + small mappers — no EF Core, no Dapper**: a handful of statements for one new aggregate; an ORM's dependency and mapping surface buys nothing at this size. SQL lives in the repository implementations under `Infrastructure/Postgres/`.
- **One cluster, three databases**: `guito_dev`, `guito_staging`, `guito_prod` on a single cluster — one thing to provision and pay for; environment isolation via separate databases, not separate clusters. (Provisioning itself was done in issue #80; numbered schema scripts live under `db/migrations/` with a `_migrations` ledger table, applied per environment by `deploy/db-apply.sh`.)
- **Database credentials per environment in Secrets Manager**: `guito-api/db-dev`, `guito-api/db-staging`, `guito-api/db-prod`, plus `guito-api/db-admin` for migrations. RDS Data API consumes their secret ARNs; these are separate from SSM application payloads (ADR-0008 amendment). Local dev connects to the cloud `guito_dev` DB — one access path, no local Postgres install to drift.
- **No data migration**: the Expenses/Category Sheets data stays in Sheets (ADR-0005 unchanged). This ADR scopes Postgres to **Bank Transactions** (and its `bank_accounts` companion); if a later decision moves Expenses to Postgres, that is a separate ADR.

## Consequences

- `Infrastructure/Sheets/` is no longer the assumed terminal datastore layer; `Infrastructure/Postgres/` coexists with it. ADR-0011's prediction ("adds `Infrastructure/Postgres/` and deletes `Infrastructure/Sheets/`") is amended to "…and *eventually* deletes Sheets only if/when Expenses move" — Bank Transactions never touch Sheets.
- Local dev now requires network access to the dev DB (hermetic *unit* tests are unaffected — they fake `IPostgresDataApiClient`; only SIT exercises the real DB).
- The `_migrations` ledger makes schema drift between environments explicit; numbered scripts in `db/migrations/` are the single source of schema truth.
- Idle cost is near-zero but not zero (per-ACU-hour floor plus storage); acceptable for a single user.
- If Expenses move to Postgres later, the matching flow (#83+) simplifies from a cross-store join to a local one — deliberately not decided here.

> **Amended by ADR-0014 (2026-10-08):** the "no data migration" scope claim is superseded
> for categories — a *categories mirror* table (seeded from Sheets, Sheets still the
> source of truth) now lives in Postgres to back `bank_transactions.suggested_category_id`.
> Expenses themselves remain Sheets-only. See ADR-0014.