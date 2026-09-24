# Guito API

Personal expense-tracking API. Guito helps José control expenses and maximize savings: expenses are created manually, extracted by AI from free-form input, or matched from bank transactions. The API performs CRUD on a Google Spreadsheet, which is the system's datastore, and exposes an HTTP API consumed by the Guito web UI and by CLI/AI agents.

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
   │        raw Authorization → agent-key validator ────────────│──▶ AWS Secrets Manager
   │          │ (allowed)                                      │
   │          ▼                                                │
   │  Lambda — guito-api (.NET 10, arm64)                      │──▶ AWS Secrets Manager
   │          │ (GoogleIdTokenMiddleware / ApiKeyMiddleware)   │
   └──────────┼────────────────────────────────────────────────┘
              ▼
     Google Sheets (datastore)          PSD2 provider (GoCardless / Enable Banking)
```

- **Runtime**: .NET 10 on AWS Lambda (managed dotnet10 runtime, arm64, eu-west-1) behind API Gateway HTTP API; logs in CloudWatch. Two functions: `guito-api` (the app) and `guito-api-authorizer` (the edge auth gate).
- **Datastore**: a Google Spreadsheet, accessed with a dedicated service account — no database, no migration.
- **Auth**: dual scheme — a personal `X-Api-Key` for CLI/AI agents and Google ID tokens for the UI (issue #13/#8). API Gateway allows one CUSTOM authorizer per route, so the `guito-key-authorizer` REQUEST authorizer is a single function dispatching on the `Authorization` header (its sole identity source): `Bearer <jwt>` → Google ID-token validation (RS256 against Google's JWKS, issuer/audience/expiry/allowlisted-email checks), raw value → agent-key validation. The two validators are independent — neither path falls back into the other. The same checks repeat inside the API (`ApiKeyMiddleware` / `GoogleIdTokenMiddleware`) as defense-in-depth, and each middleware is path-aware: agent requests carry the key in **both** `X-Api-Key` and `Authorization`; human requests carry the ID token in **both** `Authorization: Bearer <token>` and `x-google-idtoken` (see the sample requests in `src/guito-api/Rest/guito-api.http`). Human-path config (`GOOGLE_CLIENT_ID`, `GOOGLE_ALLOWED_EMAILS`) arrives via Lambda env vars set in `deploy/deploy.sh` §3b; unset → human path deny-closed.
- **Human-token smoke**: the OAuth consent happens once (OAuth Playground, `openid email` scope); the refresh token lives in Secrets Manager `guito-api/human-auth`, and fresh ID tokens are minted unattended via the Google token endpoint — so the live positive smoke needs no human in the loop.
- **Bank sync**: PSD2 transaction retrieval behind `IListTransactionsService` — GoCardless Bank Account Data first, Enable Banking free tier as fallback; consents are re-authenticated manually (~90 days).
- **AI extraction**: endpoint stubbed (501) during the revival; a new implementation over OpenRouter is planned.

## Getting started

Prerequisites: .NET 10 SDK, a Google service account key, and the Guito spreadsheet shared with that service account.

```bash
dotnet restore
dotnet run                       # Kestrel on the dev profile
```

Configuration comes from `appsettings.{Environment}.json` plus environment variables (Google service account credentials, Sheets ids, bank-provider credentials). Secrets are stored in AWS Secrets Manager in deployed environments — never commit them.

## Deployment

Deployed by GitHub Actions to Lambda via OIDC role assumption (no long-lived AWS keys in GitHub). Two functions deploy independently from their own project folders: `src/guito-api` → `guito-api` (handler `guito-api::GuitoApi.LambdaEntryPoint::FunctionHandlerAsync`) and `src/guito-api-authorizer` → `guito-api-authorizer` (REQUEST authorizer for `guito-key-authorizer`, IAM-policy responses, TTL 0). Resources are prefixed `guito-` and tagged `Project=Guito`, account 497087877832, region eu-west-1. `deploy/deploy.sh` reproduces the full stack locally (secret, IAM, functions, HTTP API, routes, access logs).

## Contributing

Feature branches + pull requests; PRs are reviewed and merged by the owner. Commits by the AI agent are authored as `Meireles`. Decisions are recorded as ADRs in [`docs/adr/`](docs/adr) — read them before proposing architectural changes.

## Docs

- [Revival plan](docs/guito-revival.md) — phases and grill-session decisions
- [ADRs](docs/adr/) — 0001–0006 (AWS target, Angular rewrite, dual auth, PSD2 provider, Sheets datastore, Figma tokens)
- [CONTEXT.md](CONTEXT.md) — domain glossary (Expense, Transaction, Match, Extract, PSD2 provider)

## Related repos

- [`guito-ui`](https://github.com/JoseHRosario/guito-ui) — Angular UI (planned; replaces the archived Next.js `guito-web-app`)
