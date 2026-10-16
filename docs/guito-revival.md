# Guito Revival Plan (2026-09)

## Current supported architecture (#150)

Expense and Category authority is solely Aurora PostgreSQL through RDS Data API.
The UI and agents use the authenticated API; Apps Script consumes API history and
writes replaceable presentation data into Sheets. There is no API-side Sheets
persistence, store selector, reverse synchronization or legacy smoke cleanup.
GET `/Category` and AI extraction use the existing Postgres category reader with
alphabetical names; retain name-only wire shape and existing IDs/FKs. No category
CRUD, automatic creation, schema changes or reseeding.

Build/hermetic tests need no Sheets key. Local Kestrel uses
`dotnet run --project src/guito-api` with approved non-production configuration.
Deployment/migrations remain through GitHub Actions. Replacement write acceptance
is deferred; deployed business tests perform reads only. Human Google login, agent auth, OpenRouter/bank/database secrets,
Apps Script installer OAuth and reporting identity remain supported.

[Retirement/live acceptance](sheets-persistence-retirement.md) separates code,
hermetic tests, approved rollout, deferred write acceptance, production reads,
reporting verification and destructive credential approval. No new live gate or
credential destruction is asserted here. Completed #129 imports remain retired.
#149 owns broader docs routing/restructuring; this update changes authority and
operational safety only, preserving its future file-move/entry-point work.

## Historical revival decisions and phase plan

The following grill-session plan and migration notes record their original scope.
Their Sheets datastore/service-account, selector and smoke assumptions are
superseded by #150 and amended ADR-0014/0015; they are not current instructions.
Original MVP exclusions and pending gates do not prove present live acceptance.

## What Guito is

Personal expense-tracking API + webapp (built ~2023-2024) to control expenses and maximize savings. The API does CRUD on a Google Spreadsheet; the UI presents the data. Old stack: Next.js 14 UI (on Vercel) + .NET API with Nordigen (PSD2) bank sync and Azure OpenAI expense extraction. Google Sheets remains the datastore.

## Decisions (see docs/adr/)

1. **ADR-0001** — Deploy to AWS: API Gateway HTTP API + Lambda (.NET 10, arm64), S3+CloudFront for the UI, CloudWatch logs (Azure sink dropped). Near-zero cost. AWS operations assume `arn:aws:iam::497087877832:role/MinervaAIAgent`.
2. **ADR-0002** — UI rewritten in **Angular 22** (signals, standalone) in new repo **`guito-ui`**; `guito-web-app` archived.
3. **ADR-0003** — Dual auth: Google OAuth PKCE for the UI (ID token validated by a Lambda authorizer) + long-lived `X-Api-Key` for CLI/AI-agent access. Google auth is required for the MVP; the API-key path remains independent but CLI/agent tooling is deferred.
4. **ADR-0004** — PSD2 behind `IListTransactionsService`; Enable Banking free "Restricted Production" tier is the provider (old Nordigen/GoCardless code deleted); consent re-auth every ~90 days accepted, on-demand sync, suggest-only matching into a Match Transactions sheet.
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
- **Secrets**: application secrets (Google service account, API keys and provider credentials) in **SSM Parameter Store SecureString**; database credentials stay in **Secrets Manager** for RDS Data API (ADR-0008); GitHub Actions deploys via **OIDC role assumption** — no long-lived keys in GitHub.
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

## Expense migration after MVP

ADR-0015 and issues #126/#128 supersede the Expense-only Sheets assumptions above.
Categories and smoke remain Sheets-authoritative. Issue #129 completed the one-off
historical import and verified identical reruns in staging and production. José
removed the temporary provenance tables after verification; import tooling and its
DDL are retired. Production storage cutover remains separate under #132. See
[the historical import record](historical-expense-import.md) and ADR-0016.
Issue #132 prepares a one-time owner-coordinated production switch, not reusable
pipeline approval/pause controls. [The runbook](expense-cutover.md) keeps staging
readiness BLOCKED pending real rehearsal/reopen evidence and production execution
subject to separate approval. The existing selector now works in production;
routine deployments preserve installed authority/reporting instead of pinning Sheets.
Production retains its Sheets default until the approved explicit override.

## Current owner disposition after cutover

José accepted #132 production Postgres cutover and working Expense saves/reporting,
then approved #133 Bank review development. The preparation-stage BLOCKED and
Sheets-default statements above remain historical, not the current installed
production authority. Distinct abort/recovery rehearsal was not performed. See
[expense-cutover.md](expense-cutover.md) and ADR-0018 for evidence boundaries and
the separate Bank-review contract. Initial Bank shows All with every row collapsed.

## Relevant GitHub issues

- MVP API/auth foundation: #2, #3, #13; API CI/CD: #6.
- MVP UI deployment/auth/data flow: #7, #8, #9, #10.
- Deferred PSD2 work: #5. It is intentionally not a blocker for #6–#10 or MVP completion.
