# Guito Revival Plan (2026-09)

Result of a grill session. Decisions are recorded as ADRs in `docs/adr/`; this file is the operational summary.
All Phase-1/2 implementation decisions below were settled in grill rounds 3-4.

## What Guito is

Personal expense-tracking API + webapp (built ~2023-2024) to control expenses and maximize savings. API does CRUD on a Google Spreadsheet; the UI presents the data. Old stack: Next.js 14 UI (on Vercel) + .NET 8 API with Nordigen (PSD2) bank sync and Azure OpenAI expense extraction.

## Decisions (see docs/adr/)

1. **ADR-0001** — Deploy to AWS: API Gateway HTTP API + Lambda (.NET 8, arm64), S3+CloudFront for the UI, CloudWatch logs (Azure sink dropped). Near-zero cost. AWS operations assume `arn:aws:iam::497087877832:role/MinervaAIAgent`.
2. **ADR-0002** — UI rewritten in **Angular 22** (signals, standalone) in new repo **`guito-ui`**; `guito-web-app` archived.
3. **ADR-0003** — Dual auth: Google OAuth PKCE for the UI (ID token validated by a Lambda authorizer) + long-lived `X-Api-Key` for CLI/AI-agent access.
4. **ADR-0004** — PSD2 behind `IListTransactionsService`; try grandfathered GoCardless account first, fallback Enable Banking free tier. Manual consent re-auth accepted. **Check pricing before committing.**
5. **ADR-0005** — Google Sheets stays the datastore; no DynamoDB migration. No new features during revival.
6. **ADR-0006** — UI design driven by Figma design tokens (Variables → tokens.json → style-dictionary → Angular theme) + Figma MCP for per-screen implementation.

## Out of scope (until running)

- New features (budgets, reports, …) — discussed after the system is live.
- Choice of OpenRouter model/config for the new `IExtractMethodService` (replacing the deprecated Azure OpenAI Assistants beta SDK) — to be discussed.

## Implementation decisions (round 4)

- **Runtime**: API bumped to **.NET 10** on the managed dotnet10 Lambda runtime (matches Minerva), done in the same pass as the auth/PSD2 refactor.
- **URL**: default `*.cloudfront.net` domain for the demo milestone; custom domain is a trivial later add.
- **Workflow**: **feature branches + PRs**; José reviews and merges. Revisit once CI/CD hard gates exist. Agent commits authored as `Meireles`.
- **Secrets**: all runtime secrets (Google service account, bank-provider, OpenRouter) in **AWS Secrets Manager**; GitHub Actions deploys via **OIDC role assumption** — no long-lived keys in GitHub.
- **CI**: replace the legacy `master_guito-api.yml` with `deploy-api.yml` (Amazon.Lambda.Tools + OIDC); strip Azure packages (Serilog Azure sink, Azure.Storage) in Phase 1.
- **AI extraction**: `AIController`/`ExtractMethodService` stubbed (501) in Phase 1 — the Azure OpenAI Assistants beta SDK is deprecated and won't survive the .NET 10 bump; OpenRouter port stays in Phase 3.

## Phases

0. **Verify the ground**: old GoCardless/Nordigen portal account still usable; Google Sheets service account works; API runs locally against Sheets.
1. **API to AWS**: Lambda + API Gateway + CloudWatch, dual auth (ADR-0003), PSD2 endpoints fixed to current GoCardless contract; GitHub Actions deploy pipeline.
2. **UI**: Angular 22 rewrite (ADR-0002, ADR-0006), S3+CloudFront hosting, CI/CD, demo-able URL. Harden afterwards.
3. **Agents**: CLI wrapper over the API (API-key auth), new `IExtractMethodService` via OpenRouter.
