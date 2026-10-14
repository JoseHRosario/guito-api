# Guito — Domain glossary

Personal expense-tracking system (API + webapp) used to control expenses and maximize savings. Single user: José.

## Glossary

- **Expense** — a spending record. Stored in the environment-selected Expense Store (Sheets by default, opt-in Postgres); created manually, extracted by AI, or matched from a bank transaction.
- **Expense Amount** — the Expense's exact decimal value; the API accepts positive, zero and negative values without rounding. Manual-entry UI conventions remain positive. See ADR-0015 (supersedes ADR-0010's API validation).
- **Expense Id** — an Int32 JSON number passed unchanged by callers. In Postgres it is an immutable database-generated integer identity key, independent of content and row position. The compatibility Sheets adapter exposes a real absolute row ID, but it is transient and not durable across row movement. There is no `storedOrder`; a slice ordinal is never identity.
- **Expense occurrence** — when spending happened, submitted through the sole create `bookingDate` JSON string bound to DateTimeOffset (date-only or explicit-offset ISO8601), returned as `bookingDate` in Europe/Lisbon and stored in `booking_date` as a timezone-aware instant. Legacy date-only records use midnight on their original Lisbon day, not UTC midnight.
- **Expense audit timestamps** — `create_date` and nullable `update_date` (wire `creationDate` / `updateDate`) describe actual record lifecycle events, not purchase time. `update_date` has no default and remains null until an explicit user update. Sheets lacks these values and returns null; historical values are not fabricated.
- **Frozen Expense snapshot** — an archived complete bounded B:H source capture, including formatted and underlying cell tokens, source coordinates and EUR/decimal conventions. Its hash identifies the batch, never an Expense or duplicate purchase.
- **Import provenance** — temporary `(snapshot_id, source_row)` → Expense Id/exclusion evidence for a one-off historical import. Batch/source coordinates are not permanent Expense identity. See [the historical record](docs/historical-expense-import.md) and ADR-0016 for accepted completion/retirement.
- **Expense history page** — a live, offset-indexed read directly from Postgres expenses, with current total count and exact stored amount text. Ordering is occurrence descending then integer ID descending. Writes between requests can shift rows; it is not a frozen export or cursor snapshot. See `docs/expense-history-export.md`.
- **Sheets reporting identity** — the private standalone Apps Script installer’s short-lived Google OIDC identity, pinned by distinct audience and exact subject at both gates. It may only GET `/Expense/history`; it is neither a human UI session nor an agent API key. No long-lived credential is stored in Google. See ADR-0017.
- **Expense Store** — one environment-controlled read/write authority, Sheets by default or explicitly selected Postgres. Production selection is a one-time owner-approved operation; routine deployments preserve installed authority rather than reverting to stale Sheets. No dual writes. See ADR-0015 and `docs/expense-cutover.md`.
- **Expense write pause** — the owner-coordinated stop of all personal API/UI/agent writers and direct source-sheet edits, with admitted requests drained before the one-time switch. There is no application-level 503 barrier.
- **Cutover development checkpoint** — an owner-disposed start gate, distinct from production execution and performed rehearsal evidence. Dated acceptance and unperformed checks live in [the cutover record](docs/expense-cutover.md); no glossary entry proves current deployed authority.
- **Smoke scope** — the datastore override requested by the `X-Sheet-Scope: smoke` header: writes and list reads always resolve to the Smoke Test tab in Sheets, including when Postgres is personal Expense authority. `/Smoke/{id}` cleanup likewise uses only Sheets; Postgres has no smoke records. Intended only for deployed-endpoint integration tests; any authenticated caller may send it (it grants less than the agent key already does). See ADR 0009.
- **Smoke Test tab** — a bare, formula-free tab (anchor `B5`) in each environment's spreadsheet where the deployed integration suite exercises the write path. Real tabs are never written by tests. _Avoid_: dummy data, test sheet.
- **Category** — label grouping expenses for analysis. Lives in its own sheet.
- **Match** — the act of pairing a bank **Transaction** with an Expense (or creating an Expense from it).
- **Transaction** — a bank movement retrieved from a PSD2 provider. Not the same as an Expense until matched.
- **Bank Transactions** — the aggregate of bank movements synced from Enable Banking into Postgres (`bank_transactions`), the former "Match Transactions" sheet idea. Income-direction (CRDT) and non-BOOK rows are not stored. See ADR 0013.
- **bank_accounts** — the Postgres table holding one row per bank account linked through Enable Banking: EB uid, session id, IBAN, name, currency, ASPSP, and consent status/expiry. Populated by the auth flow; consumed by sync.
- **sync_key** — the UNIQUE dedup key of a Bank Transaction: a hash of the EB recommended composite (account uid + booking_date + amount + credit_debit_indicator + entry_reference + counterparty), since EB rows carry no unique transaction id. Re-syncs are idempotent via `ON CONFLICT DO NOTHING`.
- **Data API** — the RDS Data API: HTTPS access to Aurora Postgres, letting the Lambda stay VPC-less (no NAT/subnets). The only database access path; faked in unit tests via `IPostgresDataApiClient`. See ADR 0013.
- **Extract** — AI parsing of free-form input (text/speech) into a proposed Expense.
- **PSD2 provider** — Enable Banking (free Restricted Production tier), supplying Transactions behind `IListTransactionsService`; provider selection is configuration (`BankProvider`).
- **Design tokens** — the daisyUI semantic values (`design/tokens.json` in guito-ui) compiled by `npm run tokens` into the theme. Single source: the "Design Tokens"/SDS variables of the Guito Figma file; code never invents colors or radii ad hoc.
- **Figma MCP sync** — the UI token acquisition mechanism; the UI repository owns its design procedures and approval evidence, not this domain glossary.
- **Local dev** — running the API on a development machine (`dotnet run --project src/guito-api`). Targets the dev/staging spreadsheet via base `appsettings.json`; never points at the prod sheet.
- **Staging** — the separately deployed non-production stack with full auth parity and its own agent key/application parameter. It shares the dev/staging spreadsheet. Shared deployment provenance must be reverified; see [operations](docs/operations.md) and ADR-0007.
- **Production** — the owner-operated live stack containing personal financial data. Its installed authority is not inferred from committed defaults; see [dated cutover evidence](docs/expense-cutover.md). Local dev must never target its sheet or credentials.
- **SecretsPayload** — unchanged runtime JSON payload shared by the API and authorizer. Deployed application secrets use SSM SecureString under `/guito-api/*` (`AwsSsm`); local files remain supported. Legacy application Secrets Manager backends are retired; authorizer configuration requires explicit `AwsSsm` selection. Database credential secrets stay in Secrets Manager for RDS Data API (ADR 0008).

## Repos

- `guito-api` — .NET 10 API (this repo)
- `guito-ui` — Angular UI (new; replaces archived `guito-web-app`, Next.js)
