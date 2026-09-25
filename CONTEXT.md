# Guito — Context

Personal expense-tracking system (API + webapp) used to control expenses and maximize savings. Single user: José.

## Glossary

- **Expense** — a spending record. Stored as a row in a Google Sheet; created manually, extracted by AI, or matched from a bank transaction.
- **Category** — label grouping expenses for analysis. Lives in its own sheet.
- **Match** — the act of pairing a bank **Transaction** with an Expense (or creating an Expense from it).
- **Transaction** — a bank movement retrieved from a PSD2 provider. Not the same as an Expense until matched.
- **Extract** — AI parsing of free-form input (text/speech) into a proposed Expense.
- **PSD2 provider** — the open-banking service supplying Transactions (GoCardless Bank Account Data or Enable Banking), always behind `IListTransactionsService`.
- **Local dev** — running the API on a development machine (`dotnet run --project src`). Targets the dev/staging spreadsheet via base `appsettings.json`; never points at the prod sheet.
- **Staging** — the deployed test environment: a full parallel AWS stack (`guito-api-staging` Lambda pair, its own API Gateway and `guito-api/staging` secret; see ADR 0007). Runs with full auth parity with prod but shares the dev/staging spreadsheet — the only non-prod sheet allowed for local dev. Target of the default deploy (`deploy/deploy.sh` with no `ENV`) and home of deployed-endpoint integration tests.
- **Production** — the live environment: `guito-api` Lambda pair, `guito-api/prod-*` secret, its own spreadsheet with real financial data. Requires the explicit `ENV=production` deploy flag. Local dev must never target the prod sheet or secret.

## Repos

- `guito-api` — .NET 8 API (this repo)
- `guito-ui` — Angular UI (new; replaces archived `guito-web-app`, Next.js)
