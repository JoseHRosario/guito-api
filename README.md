# Guito API

Personal expense-tracking API. Guito helps José control expenses and maximize savings: expenses are created manually, extracted by AI from free-form input, or matched from bank transactions. The API performs CRUD on a Google Spreadsheet, which is the system's datastore, and exposes an HTTP API consumed by the Guito web UI and by CLI/AI agents.

> Currently under revival (2026): migrating to AWS, modernizing the stack, and fixing bank sync. See the [revival plan](docs/guito-revival.md) and [issue #1](https://github.com/JoseHRosario/guito-api/issues/1).

## Architecture

```
Google (OAuth PKCE)          CLI / AI agents (X-Api-Key)
        │                              │
        ▼                              ▼
   ┌──────────────────────────────────────────┐
   │  API Gateway HTTP API (authorizers)      │
   │          │                               │
   │          ▼                               │
   │  Lambda — Guito API (.NET 10, arm64)     │──▶ AWS Secrets Manager
   │          │                               │
   └──────────┼───────────────────────────────┘
              ▼
     Google Sheets (datastore)          PSD2 provider (GoCardless / Enable Banking)
```

- **Runtime**: .NET 10 on AWS Lambda (managed dotnet10 runtime, arm64, eu-west-1) behind API Gateway HTTP API; logs in CloudWatch.
- **Datastore**: a Google Spreadsheet, accessed with a dedicated service account — no database, no migration.
- **Auth**: dual scheme — Google ID tokens for the UI (OAuth PKCE), a personal `X-Api-Key` for CLI/AI agents; both validated by separate API Gateway authorizers.
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

Deployed by GitHub Actions to Lambda via OIDC role assumption (no long-lived AWS keys in GitHub). Resources are prefixed `guito-` and tagged `Project=Guito`, account 497087877832, region eu-west-1.

## Contributing

Feature branches + pull requests; PRs are reviewed and merged by the owner. Commits by the AI agent are authored as `Meireles`. Decisions are recorded as ADRs in [`docs/adr/`](docs/adr) — read them before proposing architectural changes.

## Docs

- [Revival plan](docs/guito-revival.md) — phases and grill-session decisions
- [ADRs](docs/adr/) — 0001–0006 (AWS target, Angular rewrite, dual auth, PSD2 provider, Sheets datastore, Figma tokens)
- [CONTEXT.md](CONTEXT.md) — domain glossary (Expense, Transaction, Match, Extract, PSD2 provider)

## Related repos

- [`guito-ui`](https://github.com/JoseHRosario/guito-ui) — Angular UI (planned; replaces the archived Next.js `guito-web-app`)
