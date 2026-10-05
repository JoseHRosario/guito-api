# PSD2 bank sync behind a swappable provider interface

Date: 2026-10-03 (supersedes the GoCardless-first decision of the original 0004)

The old Nordigen integration (used until 2024) was never reimplemented; Nordigen is now GoCardless Bank Account Data, which is closed to new signups and being wound down as a data-only product. Bank sync returns in Phase 2 behind the existing provider interface (`IListTransactionsService`), with **Enable Banking as the only real provider** — its free "Restricted Production" tier gives real transactions from self-linked EU banks (Portugal covered: CGD, Millennium BCP, Santander Totta, Novo Banco, BPI, Montepio) without a contract or paid tier. `ListTransactionsNordigenService` and the `Nordigen` configuration block are deleted; no GoCardless adapter will be built.

Provider selection is configuration-only (`AppConfiguration:BankProvider: "EnableBanking" | "Dummy"`), wired in `Startup.cs`; swapping or adding providers must not touch expense logic. `Dummy` serves canned transactions for local dev and CI.

## Decisions (grilling session, 2026-10-03)

- **Auth**: every Enable Banking request carries a fresh RS256 JWT signed by the application private key (`System.IdentityModel.Tokens.Jwt`, no SDK). The private key lives in dotnet user-secrets locally and AWS Secrets Manager in deployed environments, using the same per-environment secrets mechanism as the Sheets service account (ADR-0008).
- **Consent flow**: UI-driven — the API returns the Enable Banking auth redirect URL, the SPA navigates the bank SCA, the callback hits an API "finish auth" endpoint that calls `POST /sessions`. Minimal intermediate state (auth/session ids) persists in a Sheets tab. The consent state's long-term home is the Settings UI page.
- **Consent inside the adapter**: sessions/consent lifecycle is an implementation detail of the Enable Banking provider. The interface stays `ListAsync(dateFrom, dateTo)`; a needed re-auth surfaces as a typed exception mapped to 409 ("reconnect needed"). The interface evolves only if a second real provider forces it.
- **Sync trigger**: on demand only ("sync now"). No scheduler — per-bank PSD2 rate limits are respected by the user's own cadence.
- **Persistence**: fetched transactions are upserted into a `Match Transactions` sheet keyed by the bank transaction id (idempotency anchor — re-syncs never duplicate). Sheets is the presentation layer; storage moves to Postgres later.
- **Matching**: suggest-only, confirmed by the user in a dedicated UI screen; the Expenses sheet gains a nullable bank-transaction-id column populated only by the import path. The interface name stays `IListTransactionsService` for now (application-layer collaborator naming rule).
- **Amounts**: normalized to positive at the provider boundary (ADR-0010). Income-direction transactions are filtered out for now; income handling is a separate future decision.
- **Testing**: the Enable Banking adapter is unit-tested only (HTTP faked at the transport level, like the Sheets fake); no integration tests call real banks, per the hermetic-CI constraint.

## Consequences

- `/Expense/match` in Production/staging returns an error until the Enable Banking adapter's first PR lands (`BankProvider: "EnableBanking"` with no adapter registered); `Dummy` is the only registered provider meanwhile.
- Enable Banking consents expire (90–180 days depending on bank); re-linking is manual and surfaced through the reconnect flow.
- Provider/bank rate limits live at the ASPSP level (e.g. the ~4-accesses/day rule); documentation of the re-auth and limit behavior ships with the adapter.
- If Enable Banking's free tier ever becomes limiting, open-banking.io (€3/mo, self-serve, production from day one) is the documented paid alternative — noted here rather than built as a third adapter.
