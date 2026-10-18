# AGENTS.md

Guidance for AI coding agents working in this repo. Keep it small and current — details live in the linked docs, not here.

## Where the code lives

**Canonical working tree on the host filesystem: `/d/srv/projects/guito-api`** — agents must use this path for all work (build, test, commit, push, pull). Don't clone elsewhere. For parallel sessions working separate issues, see the worktree rule under **Rules** — branch work happens in a `.worktrees/` worktree, not by switching the main tree's branch.

## What this project is

Guito: personal expense-tracking API (.NET 10, AWS Lambda + API Gateway HTTP API) using Aurora PostgreSQL via RDS Data API for Expense, Category and Bank Transactions. Sheets is an API-fed presentation consumer. Glossary in [GLOSSARY.md](GLOSSARY.md); decisions in [docs/adr/](docs/adr/); coding conventions in [docs/CONVENTIONS.md](docs/CONVENTIONS.md) (read before committing — its rules are enforced on every PR); current work plan in [docs/guito-revival.md](docs/guito-revival.md); active spec in [issue #1](https://github.com/JoseHRosario/guito-api/issues/1).

## Layout

- `src/guito-api/` — API source (one project, `src/guito-api/guito-api.csproj`)
- `src/guito-api/Repositories/` — per-aggregate data-access INTERFACES only (`IExpenseRepository`, `ICategoriesRepository`, `IExpenseExtractionRepository`)
- `src/guito-api/Infrastructure/` — datastore/provider implementations behind those interfaces (`Postgres/`, `Secrets/`, `AI/`); Services never see a concrete datastore or provider type (ADR 0011)
- `src/guito-api-authorizer/` — X-Api-Key Lambda authorizer (separate project/function)
- `tst/guito-api.Tests` — API tests
- `tst/guito-api-authorizer.Tests` — authorizer tests
- `sit/guito-api.IntegrationTests` — business SIT: black-box tests against the DEPLOYED staging stack (NOT hermetic; requires AWS-resolved secrets — run via `scripts/run-deployed-tests.sh`, never plain `dotnet test`)
- `sit/guito-api-auth.IntegrationTests` — auth-contract SIT (same runner; layer attribution from response bodies)
- `sit/guito-api.IntegrationTests.Common` — shared SIT plumbing (staging fixture); not a test project
- `docs/` — ADRs, revival plan

## Commands

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"   # .NET SDK is NOT on PATH by default — prepend before ANY dotnet command
dotnet build          # build
dotnet run --project src/guito-api  # local dev server (Kestrel) — plain dotnet run, no Lambda emulation
dotnet test --filter "Category!=Integration"   # hermetic unit tests (bare `dotnet test` FAILS without env vars — sit/ suites are not hermetic)
scripts/run-deployed-tests.sh staging read-only  # approval-gated; business reads plus established auth-contract checks
dotnet tool restore   # if Lambda tools are needed for packaging checks
# Migrations and deployment run through GitHub Actions; production requires explicit owner approval.
```

Lambda packaging happens in CI (Amazon.Lambda.Tools). Do not add local Lambda emulation tooling.

## Rules

- **Secrets never enter the repo.** Bank-provider credentials, API keys — application secrets live in SSM Parameter Store SecureString; database credentials remain in Secrets Manager for RDS Data API (ADR 0008); local dev uses `appsettings.*.json` files that are gitignored. API startup has no Sheets service-account/key-file prerequisite.
- **Respect the ADRs.** Postgres is the sole Expense and Category authority (#150, amended ADR-0014/0015); auth is dual (Google PKCE for humans, `X-Api-Key` for agents); bank sync goes through the provider interface — do not bypass these without a new ADR.
- **Reporting boundary**: Apps Script reads the API and writes presentation tabs only; preserve archives, installer OAuth and reporting identity. For deployed acceptance or credential retirement, read [the approval-gated runbook](docs/sheets-persistence-retirement.md). The legacy `X-Sheet-Scope` header is ignored; `/Smoke` is removed (404), never personal cleanup.
- **Auth paths are separate.** Human (Google ID token) and agent (`X-Api-Key`) authorization are distinct authorizers; never merge or weaken them.
- **The extraction endpoint (`POST /AI/extract`) calls OpenRouter**: a chat model extracts date/amount/merchant, Jev gates the note and picks the category (PR #73).
- **Workflow**: feature or bug branch → PR → reviewed and merged by José. One branch per issue, named `feature/<issue#>-<slug>` or `bug/<issue#>-<slug>` (issue number first); the PR body **must end with `Closes #N`** so GitHub closes the issue on merge into `master`. Feature issues use the Feature issue template; bug issues use the Bug issue template (bug = something that worked as intended is broken — code regression, wrong behavior, broken config; environment/CI/docs work stays under existing informal prefixes). **Version (ADR 0012)**: every feature/bug issue carries a mandatory *Proposed version*; the agent bumps the declared version (`<Version>` in `src/guito-api/guito-api.csproj`) on the branch as part of the work — no git tags, every build stamps `<version>+<UTCts>.<shortsha>`. Never push directly to `master`.
- **Parallel sessions use git worktrees.** The main tree (`/d/srv/projects/guito-api`) stays on `master` and is shared (José's Windows sync + any agent session) — never check a feature branch out there. For work on a separate issue while another session works, create a worktree **inside the repo** under the gitignored `.worktrees/` dir: `git worktree add .worktrees/<issue#>-<slug> feature/<issue#>-<slug>`. Branches are exclusive per worktree, so sessions cannot collide. Hermetic build/tests need no copied secrets. Approved local runs need explicit non-production Data API and retained secret-provider configuration; never copy credentials merely to build/test.
- **Before any blanket `git add -A`, verify `.worktrees/` is ignored on the current branch** (`git check-ignore .worktrees/` — branch history may predate the ignore entry); otherwise prefer adding explicit paths. A branch without the ignore entry would stage an entire nested repo copy.
- **Always run `git branch --show-current` before starting work** — the shared tree may have been switched by another session since you last looked.
- Remove the worktree when its PR merges: `git worktree remove .worktrees/<name> && git worktree prune`.
- **Commit attribution**: agent commits are authored as `Meireles (Hermes Agent) <332697001+xungameireles@users.noreply.github.com>` (or amend with `--author`); José's commits stay under his name.
- **AWS operations** assume the role `arn:aws:iam::497087877832:role/MinervaAIAgent` — never use the user identity directly.

## Architecture patterns

Request path: **Controller → Service → Data access**. Each layer has one job; follow it when adding endpoints.

### Controller (`src/guito-api/Controllers/`)
- Thin: route + DTO in, DTO out, one line calling the service. No business logic, no Sheets calls, no mapping beyond what the DTO does.
- `[ApiController]`, `[Route("[controller]")]` — URL is the lowercase controller name (`/expense`, `/category`, `/ai`, `/account`).
- One operation per action; async all the way.

### Service (`src/guito-api/Services/<Domain>/`)
- **One interface per operation**: `I<Action>Service` + `<Action>Service` in the same folder (e.g. `Services/Expense/ICreateExpenseService.cs` + `CreateExpenseService.cs`). Never grow a god-interface; a new operation is a new pair.
- Services own the business logic and produce/consume `DataTransferObjects/Output` and `Input` DTOs. `Input/` = request bodies, `Output/` = response payloads.
- Data-access implementations live in `Infrastructure/` behind repository interfaces and are named by technology (`DataApiPostgresExpenseRepository`, `OpenRouterExpenseExtractionRepository`); the interface is the contract and the implementation is swappable (Nordigen ↔ dummy keeps PSD2 behind a seam, ADR-0004). The `Service` suffix stays reserved for application-layer collaborators.
- Register the pair in `src/guito-api/Startup.cs` `ConfigureServices`. Environment-specific overrides are explicit `if (environment == ...)` blocks with a comment saying why.

### Data access (`src/guito-api/Infrastructure/`)
- Expense and Category consumers use Postgres repositories through storage-agnostic ports. `/Category` and AI candidates use `DataApiPostgresCategoriesRepository`; preserve name-only wire shape, alphabetical names and internal Int64 IDs/FKs. No category CRUD, runtime seeding or schema changes.
- Data API transport and transaction concerns stay in Infrastructure; database references come from Configuration. There is no Expense store selector, Sheets fallback or dual write.

### Errors
- Services throw `Exceptions/ProblemException(statusCode, message)` for expected failures. `Exceptions/ExceptionToProblemDetailsHandler` converts them to RFC 7807 ProblemDetails — controllers never build error responses by hand. Deliberate exception (José-approved 2026-10-02): the `AuthController` Google actions (`/Auth/token`, `/Auth/logout`) map `GoogleTokenExchangeException` through a shared `CallGoogleAsync`/`GoogleError` helper to the verbatim RFC 6749 `{error, error_description}` body, because guito-ui's `tokenFailureMessage` parses THAT shape, not ProblemDetails; the status is Google's upstream status (or 502 on provider failure). Services classify the failure (throw the typed exception); the controller translates it to the wire contract — this translation is the only hand-built response in the codebase.

### Auth (`src/guito-api/Middleware/`)
- **Two independent paths — never merge or weaken them (ADR-0003):**
  - **Agent path**: the API Gateway REQUEST authorizer (`guito-key-authorizer` → `src/guito-api-authorizer`) validates the agent key from the raw `Authorization` value at the edge and returns an IAM Allow/Deny policy, fail-closed, TTL 0. `ApiKeyMiddleware` (`src/guito-api/Middleware/`) repeats the check inside the API as defense-in-depth: valid key sets `HttpContext.Items[ApiKeyMiddleware.AgentAuthedKey]`, which makes `GoogleIdTokenMiddleware` skip only the Google-token check. Run order in `Startup`: `ApiKeyMiddleware` → `GoogleIdTokenMiddleware`.
  - **Human path (issue #13)**: the gateway identity source is a single header, `Authorization`. The edge authorizer dispatches: `Authorization: Bearer <jwt>` → `GoogleTokenValidator` (`src/guito-api-authorizer/GoogleToken/`) verifies the RS256 signature against Google's JWKS, then checks issuer, audience (`GOOGLE_CLIENT_ID` env), expiry, and the allowed-email allowlist (`GOOGLE_ALLOWED_EMAILS` env). A raw `Authorization` value → agent-key path. `GoogleIdTokenMiddleware` (`src/guito-api/Middleware/`) repeats the token check inside the API as defense-in-depth, configured via `AppConfiguration:Authentication` (set in production through `AppConfiguration__Authentication__*` Lambda env vars). Both env sets come from `deploy/deploy.sh` section 3b; unset → human path is deny-closed, agent key path unaffected. **UI contract (issue #8): send the Google ID token as `Authorization: Bearer <idtoken>`.** Agents keep sending `X-Api-Key` and now also `Authorization: <key>` — API Gateway invokes a REQUEST authorizer only when ALL identity sources are present, so multiple source headers would 401 single-header requests (hit live, 2026-09-24).
    - API Gateway allows one CUSTOM authorizer per route, so the edge is a single function with two independent validators dispatching on its sole identity source, `Authorization` (raw value → agent logic, `Bearer <jwt>` → Google logic, nothing → Deny). Other headers (`X-Api-Key`, `x-google-idtoken`) are never forwarded to REQUEST authorizers and must not be dispatched on at the edge. Independence is per-validator and per in-app middleware, not per function; never let one path fall back into the other.
    - `/healthz` is public in both gates; its single definition is `ApiKeyMiddleware.PublicPathKey`.
- Both paths read the same environment parameter (`/guito-api/prod` or `/guito-api/staging`). The contract between the sides is the secret's JSON shape (`ApiKeys` array and retained feature credentials; no Sheets service-account object); the API binds it as `SecretsPayload` (`src/guito-api/Configuration/Secrets.cs`), the authorizer parses the raw `ApiKeys` property — no shared type. Changing the payload shape requires deploying both functions.

### Persistent human sessions (#156)
- Exact POST `/Auth/session/token`, `/Auth/session/refresh` and `/Auth/session/logout` are public at the edge/in-app bearer gates but enforce their own explicit Origin, custom header, JSON and PKCE/Google identity or cookie authentication. The session cookie is **never business authorization**. Legacy `/Auth/token` and `/Auth/logout` remain unchanged; device logout must not call legacy Google revoke.
- The RFC 6749 controller exception also applies to `HumanSessionController`: sanitized session errors use `error`/`error_description`; provider/configuration/storage failures are retryable 503 and preserve cookies. Permanent session failures are 401. No raw provider/secret exceptions may escape into logging handlers.
- Sessions use existing Postgres/Data API with AES-GCM refresh credentials and an owner-provisioned **per-environment** key through `ISecretsProvider`. No fallback key or credential provisioning in deployment. Read [ADR-0019](docs/adr/0019-persistent-human-sessions.md) and [the gated runbook](docs/persistent-human-login.md) before changes or rollout. Push to `feature/**` auto-deploys shared staging: obtain its replacement approval first.

### Authorizer (`src/guito-api-authorizer/`)
- Separate Lambda project/function, invoked by API Gateway before any route. Deploys independently from `src/guito-api/` (separate GitHub Actions job/zip).
- Two validator directories, mirroring each other: `AgentKey/` (agent key: `IKeysLoader`/`SsmParameterStoreKeysLoader` (explicit `AwsSsm` backend and parameter name required), `AgentKeyValidator` fixed-time compare, any load failure → Deny) and `GoogleToken/` (RS256/JWKS + iss/aud/exp/allowlist). `Function.cs` only dispatches on the `Authorization` header and turns validator results into IAM policies (`EnableSimpleResponses=false`).
- Keep it small: no Sheets, no business logic, no API dependencies. Shared crypto/key logic between API and authorizer is duplicated on purpose (separate assemblies); note it if you change one.

### Testing (`tst/guito-api.Tests/`)
- **Unit tests only — they run in CI.** No network, no credentials, no live datastore access; the suite must pass without Sheets keys or persistence settings. Anything needing the real spreadsheet or a bank provider is out of scope for this project.
- External behavior only: HTTP boundary via `WebApplicationFactory<Program>` (`CustomWebApplicationFactory`), never internals. Explicit issue #95 exception (José-approved test seams): secret-provider contract tests may call `ISecretsProvider`, `IHumanAuthSecretProvider`, and `IEnableBankingCredentialsProvider` directly with hermetic AWS fakes to verify decryption, caching, cancellation, and backend selection. Authorizer key-loading tests likewise exercise `IKeysLoader`. Live auth and business behavior remains covered by the deployed SIT suites.
- Expense/Category tests use fake Data API transport through HTTP/service/repository seams; retain unrelated human/agent/reporting auth coverage. Replacement deployed write acceptance is deferred; deployed business tests perform reads only.
- Compositions get replaced via DI in the test factory (`IListTransactionsService` → dummy), mirroring the service-seam pattern.

### Configuration
- All runtime knobs live in `Configuration/` options classes bound to the `AppConfiguration` section; environment files layer on top (`appsettings.Development.json` overrides locally, `appsettings.Production.json` for Lambda). No `IConfiguration` reads scattered in services.

## Style

- Follow the existing code layout: controllers thin, one service interface per operation (`I<Action>Service` + `<Action>Service`), services as thin adapters over repository interfaces (ADR 0011), configuration via `Configuration/` options classes.
- Keep the API surface backward-compatible with supported Expense/Category contracts and the Angular UI.

## Living doc

Prune stale guidance with the code change that invalidates it; preserve dated
evidence behind explicit retirement notes. Broader routing/restructuring is #149.

## Agent skills

### Issue tracker

Issues live in GitHub Issues on this repo (`gh` CLI); one branch per issue, PR closes #N. See `docs/agents/issue-tracker.md`.

### Triage labels

Five canonical roles used as-is: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` + `docs/adr/` at repo root. See `docs/agents/domain.md`.
