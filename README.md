# Guito API

Personal expense-tracking API. Guito helps José control expenses and maximize savings: expenses are created manually, extracted by AI from free-form input, or matched from bank transactions. The API exposes HTTP endpoints consumed by the Guito web UI and CLI/AI agents. Sheets remains the default Expense authority; staged Postgres migration uses the existing Aurora Data API infrastructure.

> Currently under revival (2026): migrating to AWS, modernizing the stack, and fixing bank sync. See the [revival plan](docs/guito-revival.md) and [issue #1](https://github.com/JoseHRosario/guito-api/issues/1).

## Architecture

```
Google (ID token)            CLI / AI agents (X-Api-Key)
        │                              │
        ▼                              ▼
   ┌───────────────────────────────────────────────────────────┐
   │  API Gateway HTTP API  (identity source: Authorization)   │
   │  guito-key-authorizer (REQUEST, one function)             │
   │      dispatches on header:                                │
   │        Bearer <jwt>  → Google ID-token validator  ────────│──▶ Google JWKS
   │        raw Authorization → agent-key validator ────────────│──▶ SSM Parameter Store
   │          │ (allowed)                                      │
   │          ▼                                                │
   │  Lambda — guito-api (.NET 10, arm64)                      │──▶ SSM Parameter Store
   │          │ (GoogleIdTokenMiddleware / ApiKeyMiddleware)   │
   └──────────┼────────────────────────────────────────────────┘
              ▼
     Google Sheets (datastore)          PSD2 provider (Enable Banking)
```

- **Runtime**: .NET 10 on AWS Lambda (managed dotnet10 runtime, arm64, eu-west-1) behind API Gateway HTTP API; logs in CloudWatch. Two functions: `guito-api` (the app) and `guito-api-authorizer` (the edge auth gate).
- **Datastore**: Sheets remains the default/production Expense and Category authority. Aurora PostgreSQL via RDS Data API stores Bank Transactions and the Categories mirror; Expense reads/writes can opt into Postgres outside Production (ADR-0015).
- **Auth**: dual scheme — a personal `X-Api-Key` for CLI/AI agents and Google ID tokens for the UI (issue #13/#8). API Gateway allows one CUSTOM authorizer per route, so the `guito-key-authorizer` REQUEST authorizer is a single function dispatching on the `Authorization` header (its sole identity source): `Bearer <jwt>` → Google ID-token validation (RS256 against Google's JWKS, issuer/audience/expiry/allowlisted-email checks), raw value → agent-key validation. The two validators are independent — neither path falls back into the other. The same checks repeat inside the API (`ApiKeyMiddleware` / `GoogleIdTokenMiddleware`) as defense-in-depth, and each middleware is path-aware: agent requests carry the key in **both** `X-Api-Key` and `Authorization`; human requests carry the ID token in **both** `Authorization: Bearer <token>` and `x-google-idtoken` (see the sample requests in `src/guito-api/Rest/guito-api.http`). Human-path config (`GOOGLE_CLIENT_ID`, `GOOGLE_ALLOWED_EMAILS`) arrives via Lambda env vars set in `deploy/deploy.sh` §3b; unset → human path deny-closed.
- **Human-token smoke**: the OAuth consent happens once (OAuth Playground, `openid email` scope); the refresh token lives in SSM SecureString `/guito-api/human-auth`, and fresh ID tokens are minted unattended via the Google token endpoint — so the live positive smoke needs no human in the loop.
- **Bank sync**: PSD2 transaction retrieval behind `IListTransactionsService` — Enable Banking (free "Restricted Production" tier) is the provider, selected by `AppConfiguration:BankProvider`; consents are re-authenticated manually (~90 days). Provider adapter ships with the T4 Phase-2 work (issue #5 / ADR-0004).
- **AI extraction**: endpoint stubbed (501) during the revival; a new implementation over OpenRouter is planned.

## Getting started

Prerequisites: .NET 10 SDK, a Google service account key, and the Guito spreadsheet shared with that service account.

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"   # if the SDK is not already on PATH
dotnet restore
dotnet run                       # Kestrel on the dev profile
```

```bash
dotnet test --filter "Category!=Integration"   # hermetic unit tests (CI gate)
scripts/run-deployed-tests.sh                 # deployed-staging integration tests (reads secrets from AWS)
```

Configuration comes from `appsettings.{Environment}.json` plus environment variables (Google service account credentials, Sheets ids, bank-provider credentials). Application secrets are stored as SSM Parameter Store SecureStrings in deployed environments; database credentials remain in Secrets Manager because RDS Data API requires it (ADR 0008) — never commit either. Base/`appsettings.json` targets the DEV spreadsheet; `appsettings.Production.json` targets prod.

## Expense contract and staged Postgres rollout

`POST /Expense` accepts the existing `date` (`yyyy-MM-dd` or compatible datetime),
positive exact-decimal `amount` (a JSON number, including exponent notation, or an
invariant decimal string), description and category. Amounts must be exactly
representable by .NET decimal: a 96-bit coefficient and scale 0–28 after removing
trailing zeros. Inputs requiring rounding, underflow or overflow return 400; equivalent
trailing-zero representations are accepted. Responses keep numeric `amount` and add
`amountExact` text for clients that cannot preserve decimal precision in JSON numbers.
Optional `occurredAt` is ISO8601 with any explicit offset (including UTC `Z`). The
server preserves the instant and normalizes it to Europe/Lisbon; its Lisbon day must
match the supplied `date`. Naive local times (including unresolved DST gaps or
repetitions) are rejected. Timestamp precision is at most microseconds (finer inputs
return 400, not rounding).
Without occurrence time, the original date means Lisbon midnight. Currency defaults
to EUR; non-EUR values are rejected. Create returns `{ "id": "opaque-string" }`.

`GET /Expense/latest/{count}` (1–100) returns `expenses` with `id`, compatible `date`,
`occurredAt`, `currency`, `creationDate`, `updateDate`, amount, description, category
and creatorEmail. Postgres orders occurrence then ID descending; audit timestamps
are actual events rendered in Lisbon. `storedOrder` is optional/deprecated, not ID.
`DELETE /Smoke/{id}` accepts the opaque string and never deletes normal-scope records.
Use `X-Sheet-Scope: smoke` for isolated create/list verification.

`AppConfiguration:ExpenseStore` selects the complete adapter (default `Sheets`).
After coordinated staging API/UI readiness, deploy with:

```bash
ENV=staging EXPENSE_STORE=Postgres deploy/deploy.sh
```

The existing migration runner applies resumable `005_expenses.sql` before deployment.
Omitting `EXPENSE_STORE` preserves any existing staging Lambda override. Production
is pinned to Sheets and runtime rejects Postgres there until a later cutover approval.
No import, export, matching or production migration is included in #128. Reverting
to stale Sheets after Postgres writes is not a valid rollback without reconciliation.

The deployed-test runner reads the Lambda Expense Store override and exercises a
timestamped smoke payload when Postgres is selected; cleanup always uses the returned
ID and runs even if the first list assertion fails.

**Compatibility gate:** the frozen Sheets schema retains date-only precision and
transient absolute row IDs; audit timestamps are null. Legacy creates still work.
Intraday creates return 409 rather than silently losing time. Roll the time-enabled
UI out only to Postgres-enabled staging, not production yet. Real persistence,
constraints and resumable migration execution remain separate deployed acceptance;
hermetic HTTP fakes cannot prove them. See [ADR-0015](docs/adr/0015-postgres-expenses-and-lisbon-occurrence.md).

## Deployment

Deployed by GitHub Actions to Lambda via OIDC role assumption (no long-lived AWS keys in GitHub). Two functions deploy independently from their own project folders: `src/guito-api` → `guito-api` (handler `guito-api::GuitoApi.LambdaEntryPoint::FunctionHandlerAsync`) and `src/guito-api-authorizer` → `guito-api-authorizer` (REQUEST authorizer for `guito-key-authorizer`, IAM-policy responses, TTL 0). Resources are prefixed `guito-` and tagged `Project=Guito`, account 497087877832, region eu-west-1. `deploy/deploy.sh` reproduces the stack locally (parameter preflight, IAM, functions, HTTP API, routes, access logs). Application parameters are already provisioned; deployment validates their existence and never creates replacement credentials. SSM is the only deployed application-secret backend; local JSON files remain supported. The authorizer requires `SECRETS_LOCATION=AwsSsm` and an explicit `SECRETS_SECRET_NAME` parameter path. See [ADR 0008](docs/adr/0008-per-environment-secrets-payload.md) for completed retirement and the retained database-secret exception.

### Post-merge IAM reconciliation

After this cleanup is merged, production deploy succeeds, and deployed suites pass
in both environments, reconcile the shared Guito runtime and deployment-role
inline policies:

```bash
python3 deploy/reconcile-iam.py                         # dry-run plan, no writes
python3 deploy/reconcile-iam.py --apply --rollout-verified
python3 deploy/reconcile-iam.py                         # expect zero changes
```

The script uses the `minerva-agent` profile and verifies the MinervaAIAgent role,
SSM consumer configuration and policy read-back. `--rollout-verified` is an
explicit attestation of the merge/deployment/SIT gate; it does not run those
checks. Database, SSM and unrelated permissions are preserved. Changes are not
transactional: an error aborts; inspect any partial changes before retrying.
Never run `--apply` before production rollout, and never change Minerva policies.

## Contributing

Feature branches + pull requests; PRs are reviewed and merged by the owner. Commits by the AI agent are authored as `Meireles`. Decisions are recorded as ADRs in [`docs/adr/`](docs/adr) — read them before proposing architectural changes.

## Docs

- [Revival plan](docs/guito-revival.md) — phases and grill-session decisions
- [ADRs](docs/adr/) — 0001–0006 (AWS target, Angular rewrite, dual auth, PSD2 provider, Sheets datastore, Figma tokens)
- [CONTEXT.md](CONTEXT.md) — domain glossary (Expense, Transaction, Match, Extract, PSD2 provider)

## Related repos

- [`guito-ui`](https://github.com/JoseHRosario/guito-ui) — Angular UI (planned; replaces the archived Next.js `guito-web-app`)
