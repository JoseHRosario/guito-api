# Guito — Context

Personal expense-tracking system (API + webapp) used to control expenses and maximize savings. Single user: José.

## Glossary

- **Expense** — a spending record. Stored as a row in a Google Sheet; created manually, extracted by AI, or matched from a bank transaction.
- **Category** — label grouping expenses for analysis. Lives in its own sheet.
- **Match** — the act of pairing a bank **Transaction** with an Expense (or creating an Expense from it).
- **Transaction** — a bank movement retrieved from a PSD2 provider. Not the same as an Expense until matched.
- **Extract** — AI parsing of free-form input (text/speech) into a proposed Expense.
- **PSD2 provider** — the open-banking service supplying Transactions (GoCardless Bank Account Data or Enable Banking), always behind `IListTransactionsService`.

## Repos

- `guito-api` — .NET 8 API (this repo)
- `guito-ui` — Angular UI (new; replaces archived `guito-web-app`, Next.js)
