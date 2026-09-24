# Guito API

Personal expense-tracking API. Guito helps José control expenses and maximize savings: expenses are created manually, extracted by AI from free-form input, or matched from bank transactions. The API performs CRUD on a Google Spreadsheet, which is the system's datastore, and exposes an HTTP API consumed by the Guito web UI and by CLI/AI agents.

> Currently under revival (2026): migrating to AWS, modernizing the stack, and fixing bank sync. See the [revival plan](docs/guito-revival.md) and [issue #1](https://github.com/JoseHRosario/guito-api/issues/1).

## Architecture

```
Google (OAuth PKCE)          CLI / AI agents (X-Api-Key)
        │                              │
        ▼                              ▼
   ┌──────────────────────────────────────────┐
   │  API Gateway HTTP API                    │
   │  guito-key-authorizer (REQUEST, X-Api-Key)│──▶ AWS Secrets Manager
   │          │ (allowed)                     │
   │          ▼                               │
   │  Lambda — guito-api (.NET 10, arm64)     │──▶ AWS Secrets Manager
   │          │                               │
   └──────────┼───────────────────────────────┘
              ▼
     Google Sheets (datastore)          PSD2 provider (GoCardless / Enable Banking)
```

- **Runtime**: .NET 10 on AWS Lambda (managed dotnet10 runtime, arm64, eu-west-1) behind API Gateway HTTP API; logs in CloudWatch. Two functions: `guito-api` (the app) and `guito-api-authorizer` (the X-Api-Key gate).
- **Datastore**: a Google Spreadsheet, accessed with a dedicated service account — no database, no migration.
- **Auth**: dual scheme — a personal `X-Api-Key` for CLI/AI agents and Google ID tokens for the UI (OAuth PKCE, issue #13/#8). Both are enforced at the edge by the `guito-key-authorizer` REQUEST authorizer (single function, two independent validators — API Gateway allows one CUSTOM authorizer per route) and repeated inside the API by `ApiKeyMiddleware` / `GoogleIdTokenMiddleware` as defense-in-depth. Human-path config (`GOOGLE_CLIENT_ID`, `GOOGLE_ALLOWED_EMAILS`) arrives via Lambda env vars set in `deploy/deploy.sh` §3b; unset → human path deny-closed.
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
