# AGENTS.md

Guidance for AI coding agents working in this repo. Keep it small and current — details live in the linked docs, not here.

## What this project is

Guito: personal expense-tracking API (.NET 10, AWS Lambda + API Gateway HTTP API) doing CRUD on a Google Spreadsheet. Glossary in [CONTEXT.md](CONTEXT.md); decisions in [docs/adr/](docs/adr/); current work plan in [docs/guito-revival.md](docs/guito-revival.md); active spec in [issue #1](https://github.com/JoseHRosario/guito-api/issues/1).

## Layout

- `src/` — API source (one project, `src/guito-api.csproj`)
- `tst/` — test projects (`tst/guito-api.Tests`)
- `docs/` — ADRs, revival plan

## Commands

```bash
dotnet build          # build
dotnet run            # local dev server (Kestrel) — plain dotnet run, no Lambda emulation
dotnet test           # tests (external behavior only: HTTP boundary, provider seam)
dotnet tool restore   # if Lambda tools are needed for packaging checks
```

Lambda packaging happens in CI (Amazon.Lambda.Tools). Do not add local Lambda emulation tooling.

## Rules

- **Secrets never enter the repo.** Google service-account keys, bank-provider credentials, API keys — they live in AWS Secrets Manager; local dev uses `appsettings.*.json` files that are gitignored.
- **Respect the ADRs.** Google Sheets stays the datastore; auth is dual (Google PKCE for humans, `X-Api-Key` for agents); bank sync goes through the provider interface — do not bypass these without a new ADR.
- **Sheets schema is frozen.** Never change spreadsheet layout, column order, or header names — the UI and existing data depend on them.
- **Auth paths are separate.** Human (Google ID token) and agent (`X-Api-Key`) authorization are distinct authorizers; never merge or weaken them.
- **The extraction endpoint is intentionally a 501 stub** until the OpenRouter implementation lands — do not "fix" it.
- **Workflow**: feature branch → PR → reviewed and merged by José. Never push directly to `master`.
- **Commit attribution**: agent commits are authored as `Meireles <josehrosario@gmail.com>` (or amend with `--author`); José's commits stay under his name.
- **AWS operations** assume the role `arn:aws:iam::497087877832:role/MinervaAIAgent` — never use the user identity directly.

## Style

- Follow the existing code layout: controllers thin, one service interface per operation (`I<Action>Service` + `<Action>Service`), configuration via `Configuration/` options classes.
- Keep the API surface backward-compatible with the existing spreadsheet data and the planned Angular UI.
