# Guito Revival Plan (2026-09)

Result of a grill session. Decisions are recorded as ADRs in `docs/adr/`; this file is the operational summary. The current delivery target is an end-to-end MVP: deployed API + authenticated Angular UI, with the UI reading and creating expenses through the API and Google Sheets.

## What Guito is

Personal expense-tracking API + webapp (built ~2023-2024) to control expenses and maximize savings. The API does CRUD on a Google Spreadsheet; the UI presents the data. Old stack: Next.js 14 UI (on Vercel) + .NET API with Nordigen (PSD2) bank sync and Azure OpenAI expense extraction. Google Sheets remains the datastore.

## Decisions (see docs/adr/)

1. **ADR-0001** — Deploy to AWS: API Gateway HTTP API + Lambda (.NET 10, arm64), S3+CloudFront for the UI, CloudWatch logs (Azure sink dropped). Near-zero cost. AWS operations assume `arn:aws:iam::497087877832:role/MinervaAIAgent`.
2. **ADR-0002** — UI rewritten in **Angular 22** (signals, standalone) in new repo **`guito-ui`**; `guito-web-app` archived.
3. **ADR-0003** — Dual auth: Google OAuth PKCE for the UI (ID token validated by a Lambda authorizer) + long-lived `X-Api-Key` for CLI/AI-agent access. Google auth is required for the MVP; the API-key path remains independent but CLI/agent tooling is deferred.
4. **ADR-0004** — PSD2 behind `IListTransactionsService`; try grandfathered GoCardless account first, fallback Enable Banking free tier. Manual consent re-auth accepted. **This is not MVP scope; re-check provider availability/pricing when Phase 2/3 is scheduled.**
5. **ADR-0005** — Google Sheets stays the datastore; no DynamoDB migration. Keep the existing spreadsheet schema unchanged.
6. **ADR-0006** — UI design driven by Figma design tokens (Variables → tokens.json → style-dictionary → Angular theme) + Figma MCP for per-screen implementation.

## MVP scope and acceptance outcome

The MVP is complete when the app is deployed and usable end-to-end:

- A user signs in to the Angular UI with Google (PKCE).
- The UI sends the Google ID token to the deployed API; the API validates it and rejects missing/invalid credentials.
- The authenticated UI reads the latest expenses from the deployed API and can create an expense manually through that API.
- The API persists and reads expense data through the existing Google Sheets datastore.
- The UI is served from its CloudFront URL; the API is deployed behind API Gateway/Lambda. CI checks and deployment/smoke checks cover the connected application.

**Explicitly excluded from MVP:** PSD2/bank synchronization, transaction-to-expense matching, AI extraction, CLI/agent tooling, reporting, budgets, custom domain, and spreadsheet redesign. The API-key authorizer already deployed for agents is not a substitute for the Google human-auth path required by the MVP.

## Implementation decisions (round 4)

- **Runtime**: API bumped to **.NET 10** on the managed dotnet10 Lambda runtime (matches Minerva), done in the same pass as the auth refactor.
- **URL**: default `*.cloudfront.net` domain for the MVP; custom domain is a later add.
- **Workflow**: **feature branches + PRs**; José reviews and merges. Revisit once CI/CD hard gates exist. Agent commits authored as `Meireles`.
- **Secrets**: runtime secrets (Google service account and provider secrets if/when needed) in **AWS Secrets Manager**; GitHub Actions deploys via **OIDC role assumption** — no long-lived keys in GitHub.
- **CI**: replace the legacy `master_guito-api.yml` with API deployment workflow (Amazon.Lambda.Tools + OIDC); strip Azure packages (Serilog Azure sink, Azure.Storage) as part of API modernization.
- **AI extraction**: `AIController`/`ExtractMethodService` stubbed (501) until a separate OpenRouter implementation is planned; not part of the MVP.

## Implementation decisions (round 5)

- **AWS layout**: same account (`497087877832`) and region **eu-west-1** as Minerva; all resources prefixed `guito-`, tagged `Project=Guito`.
- **UI styling**: Tailwind + **daisyUI** themes mapped from the Figma tokens for the MVP; revisit if it fights the tokens.
- **Sheets schema**: unchanged; redesign deferred beyond the MVP.
- **Local dev**: plain `dotnet run` (Kestrel) against Sheets; Lambda packaging only in CI via Amazon.Lambda.Tools.
- **CLI (later phase, outline only)**: consumed by AI agents and must be installable on hosts **without** the repo cloned (e.g. published `dotnet tool` / GitHub release) — final shape explicitly out of scope for now.
- **Google Cloud**: fresh OAuth client (Web, PKCE, redirects to CloudFront domain + localhost) and a fresh service account with only the Guito spreadsheet shared to it.

## Phases

0. **Verify the ground**: confirm Google Sheets service-account access and local API behavior; keep the existing spreadsheet data/schema intact. Bank-provider account verification is not a gate for the MVP.
1. **Deploy the API and human auth**: API Gateway + Lambda + CloudWatch, Google ID-token authorizer, preserve the separate `X-Api-Key` agent path, and establish CI/CD with OIDC, tests, and smoke checks.
2. **Build and deploy the connected UI — MVP**: Angular 22 rewrite (ADR-0002, ADR-0006), Google PKCE sign-in, authenticated latest-expenses and manual-create flows against the deployed API, S3+CloudFront hosting, and UI CI/CD/e2e checks. Verify the complete deployed user journey against real Sheets data.
3. **Post-MVP / Phase 2 or 3**: PSD2 bank synchronization behind the provider interface and UI transaction matching (T4, issue #5); then prioritize other deferred features such as OpenRouter extraction and the installable CLI. Confirm the bank provider's current availability, consent model, and pricing before implementation.

## Relevant GitHub issues

- MVP API/auth foundation: #2, #3, #13; API CI/CD: #6.
- MVP UI deployment/auth/data flow: #7, #8, #9, #10.
- Deferred PSD2 work: #5. It is intentionally not a blocker for #6–#10 or MVP completion.
