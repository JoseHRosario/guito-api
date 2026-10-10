# Guito — Context

Personal expense-tracking system (API + webapp) used to control expenses and maximize savings. Single user: José.

## Glossary

- **Expense** — a spending record. Stored as a row in a Google Sheet; created manually, extracted by AI, or matched from a bank transaction.
- **Expense Amount** — the Expense's value, stored and transmitted **positive**; that it is an outflow is implied by it being an Expense. See ADR 0010. _Avoid_: negative amounts, sign convention.
- **Expense Id** — the opaque identifier of an Expense, returned by the API and never interpreted by callers. Today the row index in the spreadsheet; later a database key. _Avoid_: row, row index (in API payloads).
- **Smoke scope** — the datastore override requested by the `X-Sheet-Scope: smoke` header: writes and list reads resolve to the Smoke Test tab instead of the real tabs. Intended only for deployed-endpoint integration tests; any authenticated caller may send it (it grants less than the agent key already does). See ADR 0009.
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
- **Figma MCP sync** — how Design tokens reach the repo: Hermes reads the Figma file's variables over the Figma MCP and regenerates `design/tokens.json`; the PAT-based `tools/tokens/pull-figma.mjs` is dead because the REST scope `file_variables:read` is Enterprise-only. Design file key `UoIK5MnIqDrgfHqMmBZoYk` (José's duplicate; "Guito App" page holds the designs). See guito-ui/docs/adr/0010-figma-token-source-mcp-sync.
- **Local dev** — running the API on a development machine (`dotnet run --project src`). Targets the dev/staging spreadsheet via base `appsettings.json`; never points at the prod sheet.
- **Staging** — the deployed test environment: a full parallel AWS stack (`guito-api-staging` Lambda pair, its own API Gateway and `/guito-api/staging` parameter; see ADR 0007). Runs with full auth parity with prod but shares the dev/staging spreadsheet — the only non-prod sheet allowed for local dev. Target of the default deploy (`deploy/deploy.sh` with no `ENV`) and home of deployed-endpoint integration tests.
- **Production** — the live environment: `guito-api` Lambda pair, `/guito-api/prod` parameter, its own spreadsheet with real financial data. Requires the explicit `ENV=production` deploy flag. Local dev must never target the prod sheet or secret.
- **SecretsPayload** — unchanged runtime JSON payload shared by the API and authorizer. Deployed application secrets use SSM SecureString under `/guito-api/*` (`AwsSsm`); local files remain supported. Database credential secrets stay in Secrets Manager for RDS Data API (ADR 0008).

## Repos

- `guito-api` — .NET 8 API (this repo)
- `guito-ui` — Angular UI (new; replaces archived `guito-web-app`, Next.js)
