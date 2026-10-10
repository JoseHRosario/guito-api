# PSD2 bank sync behind a swappable provider interface

Date: 2026-10-03 (supersedes the GoCardless-first decision of the original 0004); amended 2026-10-05 per ADR-0013 (Postgres store, auth); amended 2026-10-08 per issues #89–#103 (adapter shipped — RS256 verified over PS256; "provider interface" = the Repositories/ ports)

The old Nordigen integration (used until 2024) was never reimplemented; Nordigen is now GoCardless Bank Account Data, which is closed to new signups and being wound down as a data-only product. Bank sync returns in Phase 2 behind the existing provider interface (`IListTransactionsService`), with **Enable Banking as the only real provider** — its free "Restricted Production" tier gives real transactions from self-linked EU banks (Portugal covered: CGD, Millennium BCP, Santander Totta, Novo Banco, BPI, Montepio) without a contract or paid tier. `ListTransactionsNordigenService` and the `Nordigen` configuration block are deleted; no GoCardless adapter will be built.

Provider selection is configuration-only (`AppConfiguration:BankProvider: "EnableBanking" | "Dummy"`), wired in `Startup.cs`; swapping or adding providers must not touch expense logic. `Dummy` serves canned transactions for local dev and CI.

## Decisions (grilling session, 2026-10-03)

**Amendment (2026-10-08, issues #89–#103):** the "provider interface" of this ADR means the application-layer ports `IBankTransactionProvider` and `IBankConsentProvider` in `src/guito-api/Repositories/` (ADR-0011 shape) — services reference only those and `Model/*` types; the Enable Banking wire vocabulary (JSON, exceptions, records) is internal to `Infrastructure/EnableBanking/`. `IListTransactionsService` remains the application-facing service seam above them.

- **Auth**: every Enable Banking request carries a fresh JWT signed by the application private key (`System.IdentityModel.Tokens.Jwt`, no SDK) — RS256, **empirically verified against the EB sandbox 2026-10-08** (`GET /application`: PS256 → 401 "Wrong signature", RS256 → 200; the earlier PS256 reading was wrong). The private key lives in SSM SecureString deployed (staging: dedicated `/guito-api/eb-staging-pk`; production: `/guito-api/eb-prod-pk`) and the application id (the JWT `kid`) in config, using the ADR-0008 per-environment secrets mechanism.
- **Consent flow**: UI-driven — the API returns the Enable Banking auth redirect URL, the SPA navigates the bank SCA, the callback hits an API "finish auth" endpoint that calls `POST /sessions`. Minimal intermediate state (auth/session ids) persists with the `bank_accounts` rows (Postgres, ADR-0013). The consent state's long-term home is the Settings UI page.
- **Consent inside the adapter**: sessions/consent lifecycle is an implementation detail of the Enable Banking provider. The interface stays `ListAsync(dateFrom, dateTo)`; a needed re-auth surfaces as a typed exception mapped to 409 ("reconnect needed"). The interface evolves only if a second real provider forces it.
- **Sync trigger**: on demand only ("sync now"). No scheduler — per-bank PSD2 rate limits are respected by the user's own cadence.
- **Persistence**: fetched transactions are stored in Postgres `bank_transactions`, dedup keyed by a `sync_key` hash (idempotency anchor — re-syncs never duplicate, `ON CONFLICT DO NOTHING`). **Amended 2026-10-05**: the originally planned `Match Transactions` sheet is gone — the aggregate is **Bank Transactions** and lives in Postgres per ADR-0013, not in Sheets. Sheets stays the Expense/Category store.
- **Matching**: suggest-only, confirmed by the user in a dedicated UI screen; the Expenses sheet gains a nullable bank-transaction-id column populated only by the import path. The interface name stays `IListTransactionsService` for now (application-layer collaborator naming rule).
- **Amounts**: normalized to positive at the provider boundary (ADR-0010). Income-direction transactions are filtered out for now; income handling is a separate future decision.
- **Testing**: the Enable Banking adapter is unit-tested only (HTTP faked at the transport level, like the Sheets fake); no integration tests call real banks, per the hermetic-CI constraint.

## Consequences

- `/Expense/match` in Production/staging returns an error until the Enable Banking adapter's first PR lands (`BankProvider: "EnableBanking"` with no adapter registered); `Dummy` is the only registered provider meanwhile.
- Enable Banking consents expire (90–180 days depending on bank); re-linking is manual and surfaced through the reconnect flow.
- Provider/bank rate limits live at the ASPSP level (e.g. the ~4-accesses/day rule); documentation of the re-auth and limit behavior ships with the adapter.
- If Enable Banking's free tier ever becomes limiting, open-banking.io (€3/mo, self-serve, production from day one) is the documented paid alternative — noted here rather than built as a third adapter.
