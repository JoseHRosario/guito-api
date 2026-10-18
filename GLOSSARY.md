# Guito — Context

Personal expense-tracking system (API + webapp) used to control expenses and maximize savings. Single user: José.

## Glossary

Authority terms describe the supported #150 architecture, not verified deployed
state. Live acceptance and credential retirement require separate evidence in
[the approval-gated runbook](docs/sheets-persistence-retirement.md).

- **Expense** — a spending record. Stored only in Postgres; created manually, extracted by AI, or matched from a bank transaction.
- **Expense Amount** — the Expense's exact decimal value; the API accepts positive, zero and negative values without rounding. Manual-entry UI conventions remain positive. See ADR-0015 (supersedes ADR-0010's API validation).
- **Expense Id** — an Int32 JSON number passed unchanged by callers. In Postgres it is an immutable database-generated integer identity key, independent of content and row position. Historical Sheets row IDs were transient; the adapter is retired and those IDs are not supported API identity. There is no `storedOrder`; a slice ordinal is never identity.
- **Expense occurrence** — when spending happened, submitted through the sole create `bookingDate` JSON string bound to DateTimeOffset (date-only or explicit-offset ISO8601), returned as `bookingDate` in Europe/Lisbon and stored in `booking_date` as a timezone-aware instant. Legacy date-only records use midnight on their original Lisbon day, not UTC midnight.
- **Expense audit timestamps** — `create_date` and nullable `update_date` (wire `creationDate` / `updateDate`) describe actual record lifecycle events, not purchase time. `update_date` has no default and remains null until an explicit user update. Historical source-sheet audit values were absent; historical values are not fabricated.
- **Frozen Expense snapshot** — an archived complete bounded B:H source capture, including formatted and underlying cell tokens, source coordinates and EUR/decimal conventions. Its hash identifies the batch, never an Expense or duplicate purchase.
- **Import provenance** — temporary `(snapshot_id, source_row)` → Expense Id/exclusion evidence used for the completed one-off import (#129). José dropped its tables after verified reruns; private archives/reports retain the evidence. The importer is retired. See ADR-0016 and `docs/historical-expense-import.md`.
- **Expense history page** — a live, offset-indexed read directly from Postgres expenses, with current total count and exact stored amount text. Ordering is occurrence descending then integer ID descending. Writes between requests can shift rows; it is not a frozen export or cursor snapshot. See `docs/expense-history-export.md`.
- **Sheets reporting identity** — the private standalone Apps Script installer’s short-lived Google OIDC identity, pinned by distinct audience and exact subject at both gates. It may only GET `/Expense/history`; it is neither a human UI session nor an agent API key. No long-lived credential is stored in Google. See ADR-0017.
- **Expense authority** — Postgres alone, in every environment. The former Expense Store selector is retired under #150; no fallback or dual writes. See amended ADR-0015 and `docs/sheets-persistence-retirement.md`.
- **Historical cutover writer hold** — the owner-coordinated stop/drain described in #132; not a reusable store-switch mechanism or application-level 503 barrier. Historical evidence is retained in `docs/expense-cutover.md`.
- **Live acceptance** — evidence pinned to a deployed revision: read-only business verification and retained auth/reporting checks, tracked separately from credential destruction approval. Hermetic tests alone cannot establish it.
- **Retired Sheets smoke scope** — historical ADR-0009 quarantine routing. The legacy `X-Sheet-Scope` header is ignored; `/Smoke/{id}` is removed, not personal cleanup.
- **Replacement write acceptance** — deferred beyond this removal-only scope; no isolated round-trip or guard framework is supplied. Legacy Smoke Test tabs are historical artifacts, not test targets.
- **Category** — authoritative Postgres label grouping Expenses. Existing Int64 IDs/FKs remain internal; GET `/Category` exposes names only, alphabetically ordered, and AI extraction uses the same reader. No category CRUD, automatic creation or reseeding. Unknown names retain 422 rejection.
- **Cutover development checkpoint** — separate from production execution approval and rehearsal evidence. José accepted #132 operational cutover and approved #133 development; this owner disposition does not claim the distinct abort/recovery rehearsal was performed. See `docs/expense-cutover.md` and ADR-0018.
- **Match** — the explicit act of pairing a bank **Transaction** with an Expense, persisted one-to-one. The Expense may be an eligible existing record or a newly created editable record. Suggestions and their count never establish a Match. Unmatch requires the expected Expense Id, removes only that link and returns Needs review. Create-and-match creates a new editable Expense and that link atomically; see [the #134 contract](docs/bank-manual-matching-contract.md) and [#135 contract](docs/bank-create-and-match-contract.md).
- **Create-and-match recovery** — read the current Bank inbox through GET `/BankTransaction` after an uncertain Save. This is current link state, not historical creation evidence; there is no operation ID, receipt or replay guarantee.
- **Transaction** — a bank movement retrieved from a PSD2 provider. Not the same as an Expense until matched.
- **Bank booking status** — provider finality (`status=BOOK`), separate from Guito review decisions.
- **Transaction review status** — persisted `needsReview`, `matched` or `ignored`; Matched iff a valid one-to-one Expense link exists. Ignore/Return to review only change unlinked rows, never a Match or a category suggestion. See ADR-0018 for the HTTP contract.
- **Bank Transactions** — the aggregate of bank movements synced from Enable Banking into Postgres (`bank_transactions`), the former "Match Transactions" sheet idea. Income-direction (CRDT) and non-BOOK rows are not stored. See ADR 0013.
- **bank_accounts** — the Postgres table holding one row per bank account linked through Enable Banking: EB uid, session id, IBAN, name, currency, ASPSP, and consent status/expiry. Populated by the auth flow; consumed by sync.
- **sync_key** — the UNIQUE dedup key of a Bank Transaction: a hash of the EB recommended composite (account uid + booking_date + amount + credit_debit_indicator + entry_reference + counterparty), since EB rows carry no unique transaction id. Re-syncs are idempotent via `ON CONFLICT DO NOTHING`.
- **Data API** — the RDS Data API: HTTPS access to Aurora Postgres, letting the Lambda stay VPC-less (no NAT/subnets). The only database access path; faked in unit tests via `IPostgresDataApiClient`. See ADR 0013.
- **Extract** — AI parsing of free-form input (text/speech) into a proposed Expense.
- **PSD2 provider** — Enable Banking (free Restricted Production tier), supplying Transactions behind `IListTransactionsService`; provider selection is configuration (`BankProvider`).
- **Design tokens** — the daisyUI semantic values (`design/tokens.json` in guito-ui) compiled by `npm run tokens` into the theme. Single source: the "Design Tokens"/SDS variables of the Guito Figma file; code never invents colors or radii ad hoc.
- **Figma MCP sync** — how Design tokens reach the repo: Hermes reads the Figma file's variables over the Figma MCP and regenerates `design/tokens.json`; the PAT-based `tools/tokens/pull-figma.mjs` is dead because the REST scope `file_variables:read` is Enterprise-only. Design file key `UoIK5MnIqDrgfHqMmBZoYk` (José's duplicate; "Guito App" page holds the designs). See guito-ui/docs/adr/0010-figma-token-source-mcp-sync.
- **Local dev** — running the API on a development machine (`dotnet run --project src/guito-api`). Uses explicitly configured non-production Data API references and retained secrets; no Sheets key prerequisite. Never targets production.
- **Staging** — the deployed test environment: a full parallel AWS stack (`guito-api-staging` Lambda pair, its own API Gateway and `/guito-api/staging` parameter; see ADR 0007). Runs with full auth parity with prod but datastore isolation must be verified independently before test writes. Target of the default deploy (`deploy/deploy.sh` with no `ENV`) and home of deployed-endpoint integration tests.
- **Production** — the live environment: `guito-api` Lambda pair, `/guito-api/prod` parameter, Postgres with real financial data and API-fed presentation Sheets. Requires the explicit `ENV=production` deploy flag. Local dev must never target production database or secrets. Verification is read-only.
- **Human session** — independent browser/device cookie capability, server-enforced seven-day idle expiry without an absolute lifetime, backed by encrypted Google refresh permission. It renews short-lived Google tokens but never authorizes business requests. See ADR-0019.
- **SecretsPayload** — runtime JSON payload used by the API and authorizer; the obsolete GoogleServiceAccount field is retired, with deployed removal separately approval-gated. Deployed application secrets use SSM SecureString under `/guito-api/*` (`AwsSsm`); local files remain supported. Legacy application Secrets Manager backends are retired; authorizer configuration requires explicit `AwsSsm` selection. Database credential secrets stay in Secrets Manager for RDS Data API (ADR 0008).

## Repos

- `guito-api` — .NET 10 API (this repo)
- `guito-ui` — Angular UI (new; replaces archived `guito-web-app`, Next.js)
