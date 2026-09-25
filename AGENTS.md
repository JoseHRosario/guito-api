# AGENTS.md

Guidance for AI coding agents working in this repo. Keep it small and current — details live in the linked docs, not here.

## Where the code lives

**Canonical working tree on the host filesystem: `/d/srv/projects/guito-api`** — agents must use this path for all work (build, test, commit, push, pull). Don't clone elsewhere.

## What this project is

Guito: personal expense-tracking API (.NET 10, AWS Lambda + API Gateway HTTP API) doing CRUD on a Google Spreadsheet. Glossary in [CONTEXT.md](CONTEXT.md); decisions in [docs/adr/](docs/adr/); coding conventions in [docs/CONVENTIONS.md](docs/CONVENTIONS.md) (read before committing — its rules are enforced on every PR); current work plan in [docs/guito-revival.md](docs/guito-revival.md); active spec in [issue #1](https://github.com/JoseHRosario/guito-api/issues/1).

## Layout

- `src/guito-api/` — API source (one project, `src/guito-api/guito-api.csproj`)
- `src/guito-api-authorizer/` — X-Api-Key Lambda authorizer (separate project/function)
- `tst/guito-api.Tests` — API tests
- `tst/guito-api-authorizer.Tests` — authorizer tests
- `sit/guito-api.IntegrationTests` — business SIT: black-box tests against the DEPLOYED staging stack (NOT hermetic; requires AWS-resolved secrets — run via `scripts/run-deployed-tests.sh`, never plain `dotnet test`)
- `sit/guito-api-auth.IntegrationTests` — auth-contract SIT (same runner; layer attribution from response bodies)
- `sit/guito-api.IntegrationTests.Common` — shared SIT plumbing (staging fixture); not a test project
- `docs/` — ADRs, revival plan

## Commands

```bash
dotnet build          # build
dotnet run --project src/guito-api  # local dev server (Kestrel) — plain dotnet run, no Lambda emulation
dotnet test --filter "Category!=Integration"   # tests (external behavior only: HTTP boundary, provider seam)
dotnet tool restore   # if Lambda tools are needed for packaging checks
```

Lambda packaging happens in CI (Amazon.Lambda.Tools). Do not add local Lambda emulation tooling.

## Rules

- **Secrets never enter the repo.** Google service-account keys, bank-provider credentials, API keys — they live in AWS Secrets Manager; local dev uses `appsettings.*.json` files that are gitignored. The Google service-account key is `src/guito-api/google-spreadsheets.json` (gitignored; relative paths in config resolve against the project directory, since `dotnet run` runs from there).
- **Respect the ADRs.** Google Sheets stays the datastore; auth is dual (Google PKCE for humans, `X-Api-Key` for agents); bank sync goes through the provider interface — do not bypass these without a new ADR.
- **Sheets schema is frozen.** Never change spreadsheet layout, column order, or header names — the UI and existing data depend on them.
- **Auth paths are separate.** Human (Google ID token) and agent (`X-Api-Key`) authorization are distinct authorizers; never merge or weaken them.
- **The extraction endpoint is intentionally a 501 stub** until the OpenRouter implementation lands — do not "fix" it.
- **Workflow**: feature branch → PR → reviewed and merged by José. Never push directly to `master`.
- **Commit attribution**: agent commits are authored as `Meireles <josehrosario@gmail.com>` (or amend with `--author`); José's commits stay under his name.
- **AWS operations** assume the role `arn:aws:iam::497087877832:role/MinervaAIAgent` — never use the user identity directly.

## Architecture patterns

Request path: **Controller → Service → Data access**. Each layer has one job; follow it when adding endpoints.

### Controller (`src/guito-api/Controllers/`)
- Thin: route + DTO in, DTO out, one line calling the service. No business logic, no Sheets calls, no mapping beyond what the DTO does.
- `[ApiController]`, `[Route("[controller]")]` — URL is the lowercase controller name (`/expense`, `/category`, `/ai`, `/account`).
- One operation per action; async all the way.

### Service (`src/guito-api/Services/<Domain>/`)
- **One interface per operation**: `I<Action>Service` + `<Action>Service` in the same folder (e.g. `Services/Expense/ICreateExpenseService.cs` + `CreateExpenseGoogleApisSheetsService.cs`). Never grow a god-interface; a new operation is a new pair.
- Services own the business logic and produce/consume `DataTransferObjects/Output` and `Input` DTOs. `Input/` = request bodies, `Output/` = response payloads.
- Data-access backends are named by technology in the class name (`...GoogleApisSheetsService`, `...NordigenService`, `...DummyService`). The interface is the contract; the implementation is swappable (Nordigen ↔ dummy is how PSD2 stays behind a seam, ADR-0004).
- Register the pair in `src/guito-api/Startup.cs` `ConfigureServices`. Environment-specific overrides are explicit `if (environment == ...)` blocks with a comment saying why.

### Data access (`src/guito-api/Services/GooglesheetsService.cs`)
- `IGooglesheetsService.Get()` returns an authenticated Google `SheetsService` — the credential/secrets concern is isolated here; everything else just calls it.
- All ranges/spreadsheet ids come from `Configuration/` options (`AppConfigurationOptions`, populated from `appsettings*.json`); no hardcoded ids or ranges in services.

### Errors
- Services throw `Exceptions/ProblemException(statusCode, message)` for expected failures (e.g. the 501 extraction stub). `Exceptions/ExceptionToProblemDetailsHandler` converts them to RFC 7807 ProblemDetails — controllers never build error responses by hand.

### Auth (`src/guito-api/Middleware/`)
- **Two independent paths — never merge or weaken them (ADR-0003):**
  - **Agent path**: the API Gateway REQUEST authorizer (`guito-key-authorizer` → `src/guito-api-authorizer`) validates the agent key from the raw `Authorization` value at the edge and returns an IAM Allow/Deny policy, fail-closed, TTL 0. `ApiKeyMiddleware` (`src/guito-api/Middleware/`) repeats the check inside the API as defense-in-depth: valid key sets `HttpContext.Items[ApiKeyMiddleware.AgentAuthedKey]`, which makes `GoogleIdTokenMiddleware` skip only the Google-token check. Run order in `Startup`: `ApiKeyMiddleware` → `GoogleIdTokenMiddleware`.
  - **Human path (issue #13)**: the gateway identity source is a single header, `Authorization`. The edge authorizer dispatches: `Authorization: Bearer <jwt>` → `GoogleTokenValidator` (`src/guito-api-authorizer/GoogleToken/`) verifies the RS256 signature against Google's JWKS, then checks issuer, audience (`GOOGLE_CLIENT_ID` env), expiry, and the allowed-email allowlist (`GOOGLE_ALLOWED_EMAILS` env). A raw `Authorization` value → agent-key path. `GoogleIdTokenMiddleware` (`src/guito-api/Middleware/`) repeats the token check inside the API as defense-in-depth, configured via `AppConfiguration:Authentication` (set in production through `AppConfiguration__Authentication__*` Lambda env vars). Both env sets come from `deploy/deploy.sh` section 3b; unset → human path is deny-closed, agent key path unaffected. **UI contract (issue #8): send the Google ID token as `Authorization: Bearer <idtoken>`.** Agents keep sending `X-Api-Key` and now also `Authorization: <key>` — API Gateway invokes a REQUEST authorizer only when ALL identity sources are present, so multiple source headers would 401 single-header requests (hit live, 2026-09-24).
    - API Gateway allows one CUSTOM authorizer per route, so the edge is a single function with two independent validators dispatching on its sole identity source, `Authorization` (raw value → agent logic, `Bearer <jwt>` → Google logic, nothing → Deny). Other headers (`X-Api-Key`, `x-google-idtoken`) are never forwarded to REQUEST authorizers and must not be dispatched on at the edge. Independence is per-validator and per in-app middleware, not per function; never let one path fall back into the other.
    - `/healthz` is public in both gates; its single definition is `ApiKeyMiddleware.PublicPathKey`.
- Both paths read the same secret `guito-api/prod`. The contract between the sides is the secret's JSON shape (`ApiKeys` array, SA key object); the API binds it as `SecretsPayload` (`src/guito-api/Configuration/Secrets.cs`), the authorizer parses the raw `ApiKeys` property — no shared type. Changing the payload shape requires deploying both functions.

### Authorizer (`src/guito-api-authorizer/`)
- Separate Lambda project/function, invoked by API Gateway before any route. Deploys independently from `src/guito-api/` (separate GitHub Actions job/zip).
- Two validator directories, mirroring each other: `AgentKey/` (agent key: `IKeysLoader`/`SecretsManagerKeysLoader`, `AgentKeyValidator` fixed-time compare, any load failure → Deny) and `GoogleToken/` (RS256/JWKS + iss/aud/exp/allowlist). `Function.cs` only dispatches on the `Authorization` header and turns validator results into IAM policies (`EnableSimpleResponses=false`).
- Keep it small: no Sheets, no business logic, no API dependencies. Shared crypto/key logic between API and authorizer is duplicated on purpose (separate assemblies); note it if you change one.

### Testing (`tst/guito-api.Tests/`)
- **Unit tests only — they run in CI.** No network, no credentials, no real Sheets access; the suite must pass on a machine with zero Google setup (verified by removing the key file). Anything needing the real spreadsheet or a bank provider is out of scope for this project.
- External behavior only: HTTP boundary via `WebApplicationFactory<Program>` (`CustomWebApplicationFactory`), never internals.
- The Sheets seam is faked at the HTTP level: `FakeGooglesheetsService` builds a real Google client whose transport is `FakeSheetsHttpHandler` (canned Sheets JSON responses + recorded writes). Tests therefore cover controller → service → real Google-client serialization, without network or credentials.
- Compositions get replaced via DI in the test factory (`IListTransactionsService` → dummy), mirroring the service-seam pattern.

### Configuration
- All runtime knobs live in `Configuration/` options classes bound to the `AppConfiguration` section; environment files layer on top (`appsettings.Development.json` overrides locally, `appsettings.Production.json` for Lambda). No `IConfiguration` reads scattered in services.

## Style

- Follow the existing code layout: controllers thin, one service interface per operation (`I<Action>Service` + `<Action>Service`), configuration via `Configuration/` options classes.
- Keep the API surface backward-compatible with the existing spreadsheet data and the planned Angular UI.
