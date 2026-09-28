# Guito — Context

Personal expense-tracking system (API + webapp) used to control expenses and maximize savings. Single user: José.

## Glossary

- **Expense** — a spending record. Stored as a row in a Google Sheet; created manually, extracted by AI, or matched from a bank transaction.
- **Expense Id** — the opaque identifier of an Expense, returned by the API and never interpreted by callers. Today the row index in the spreadsheet; later a database key. _Avoid_: row, row index (in API payloads).
- **Smoke scope** — the datastore override requested by the `X-Sheet-Scope: smoke` header: writes and list reads resolve to the Smoke Test tab instead of the real tabs. Intended only for deployed-endpoint integration tests; any authenticated caller may send it (it grants less than the agent key already does). See ADR 0009.
- **Smoke Test tab** — a bare, formula-free tab (anchor `B5`) in each environment's spreadsheet where the deployed integration suite exercises the write path. Real tabs are never written by tests. _Avoid_: dummy data, test sheet.
- **Category** — label grouping expenses for analysis. Lives in its own sheet.
- **Match** — the act of pairing a bank **Transaction** with an Expense (or creating an Expense from it).
- **Transaction** — a bank movement retrieved from a PSD2 provider. Not the same as an Expense until matched.
- **Extract** — AI parsing of free-form input (text/speech) into a proposed Expense.
- **PSD2 provider** — the open-banking service supplying Transactions (GoCardless Bank Account Data or Enable Banking), always behind `IListTransactionsService`.
- **Local dev** — running the API on a development machine (`dotnet run --project src`). Targets the dev/staging spreadsheet via base `appsettings.json`; never points at the prod sheet.
- **Staging** — the deployed test environment: a full parallel AWS stack (`guito-api-staging` Lambda pair, its own API Gateway and `guito-api/staging` secret; see ADR 0007). Runs with full auth parity with prod but shares the dev/staging spreadsheet — the only non-prod sheet allowed for local dev. Target of the default deploy (`deploy/deploy.sh` with no `ENV`) and home of deployed-endpoint integration tests.
- **Production** — the live environment: `guito-api` Lambda pair, `guito-api/prod-*` secret, its own spreadsheet with real financial data. Requires the explicit `ENV=production` deploy flag. Local dev must never target the prod sheet or secret.
- **SecretsPayload** — the JSON shape of each environment's Secrets Manager secret (`guito-api/prod`, `guito-api/staging`): `{ GoogleServiceAccount, ApiKeys }`. The Google service account inside must be the SA of that environment's own spreadsheet — prod carries the prod SA, staging the dev/staging SA (see ADR 0008).
- **Guito design file** — José's duplicate of the "Simple Design System (Community)" Figma file, key `UoIK5MnIqDrgfHqMmBZoYk` (owner José). The Community original is read-only source and never edited. App designs live on its dedicated **Guito App** page. See ADR 0010.
- **Design loop** — the UI workflow: Hermes drafts a screen on the Guito App page via the Figma MCP → José validates/edits in Figma → approval becomes the implement source. Design changes happen in Figma, never as code-side drift.
- **Token sync (MCP)** — the process writing `design/tokens.json` in guito-ui from the design file's 'Design Tokens' variables, read via the Figma MCP (see ADR 0010). The REST/pull path is impossible on José's plan: `file_variables:read` is Enterprise-only.

## Repos

- `guito-api` — .NET 8 API (this repo)
- `guito-ui` — Angular UI (new; replaces archived `guito-web-app`, Next.js)
