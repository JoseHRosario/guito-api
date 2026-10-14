# AGENTS.md

See [README.md](README.md) for the overview and onboarding. Links below are conditional reads, not eager archive loading.

## Commands

Run from the owned worktree with the .NET 10 SDK (`guito-api.slnx`, not `.sln`):

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build guito-api.slnx
dotnet test guito-api.slnx --filter "Category!=Integration"
dotnet run --project src/guito-api   # Kestrel; local dev configuration only
```

Build/tests need no credentials. Local requests may need ignored files; relative
paths resolve against `src/guito-api`. Never target production from local dev. Bare
`dotnet test` includes live `sit/` suites. Separately approved live checks use
`scripts/run-deployed-tests.sh staging` (AWS secret reads, isolated Sheets smoke CRUD).
Packaging belongs to CI; no Lambda emulator.

## Essential conventions

Before C# edits, read [conventions](docs/CONVENTIONS.md). One top-level type per
file (including records); thin controllers, one service interface per operation,
repository interfaces in `Repositories/`, technology adapters in `Infrastructure/`.
`DataTransferObjects/` owns HTTP payloads; `Model/` owns internal values. Repository
contracts never accept wire DTOs. Bind options in `Startup.cs`; no scattered
service configuration reads or hardcoded sheet ranges.
Use ProblemException/ProblemDetails for expected failures; preserve the Google
OAuth `{error, error_description}` exception described in the auth reference.

Tests in `tst/` stay hermetic: no network, credentials, live Sheets/Postgres or bank
provider. Prefer HTTP/public seams and real application composition with fake
external transports, rather than mocking internal layers. Secret-provider/key-loader
contract seams are allowed. Green fakes prove neither
live persistence/constraints nor visual approval or production acceptance.

## Git and ownership

- Canonical shared root: `/d/srv/projects/guito-api`. Every issue, even sequential
  work, uses its own gitignored `.worktrees/<issue#>-<slug>` worktree. Verify branch,
  status, staged/untracked ownership and `.worktrees/` ignore before work. Never
  switch, pull over, stash, reset or clean another session's dirty root/worktree.
  Refresh refs safely; start from the approved immutable master base.
- One issue branch (`feature/<issue#>-<slug>` or `bug/<issue#>-<slug>`) → PR → José
  reviews/merges. Stage explicit owned paths only; never push directly to `master`.
  Remove worktrees after merge only if no pending work remains.
- Agent commits: `Meireles (Hermes Agent) <332697001+xungameireles@users.noreply.github.com>`;
  PRs use `xungameireles`, not José's account. Preserve José's attribution.
- Follow [tracker rules](docs/agents/issue-tracker.md) for templates, closure and
  cross-repo references. Cross-repo #149: reference, never auto-close from
  a component PR; José owns final disposition.
- Runtime feature/bug versions follow [ADR-0012](docs/adr/0012-version-declared-no-tags.md):
  declared csproj version, stamped builds, no tags. #149 explicitly authorizes no
  runtime version bump for this documentation-only work.

## Boundaries

**Always:** preserve exact IDs, decimals and Lisbon dates; respect applicable ADRs
and frozen Sheets layout/headers/column order. Keep human and agent validators
independent and fail-closed; one edge dispatcher is not permission to merge paths.
Read both worktrees' root instructions for cross-repo work.

**Ask first:** architecture/contract/authority changes, credential lifecycle, live
checks, personal financial writes/deletes/imports/resets, production actions and
staging replacement. Routine deployments and migrations go through GitHub Actions,
not a local shell. Exceptional owner-approved operator configuration is allowed
only within its explicit scope and read-back gates. Explicitly select **staging**
for an approved review dispatch; shared staging is last-deployment-wins—verify exact
ref/build/store and sibling deployment provenance before acceptance or replacement.

**Never:** commit or print secrets; modify anything under `~/.aws` (José owns it);
use José's AWS identity instead of the approved agent role
`arn:aws:iam::497087877832:role/MinervaAIAgent`; run unapproved production or personal
financial mutations. Do not copy ignored secrets just to build/test. Smoke cleanup
is isolated Sheets-only, never personal/Postgres deletion. Never infer deployed
authority/approval from code, old notes, `/healthz` or a green fake suite.

## Read before the relevant task

| Task | Required conditional references |
| --- | --- |
| C# implementation/review | [CONVENTIONS](docs/CONVENTIONS.md); [repository decision](docs/adr/0011-repository-pattern.md) for layering changes |
| Domain/Expense contract | [GLOSSARY](GLOSSARY.md), [domain map](docs/agents/domain.md), [ADR-0015](docs/adr/0015-postgres-expenses-and-lisbon-occurrence.md) |
| Expense edit/delete or reconciliation | [mutation contract](docs/expense-edit-delete-contract.md), [delete security](docs/expense-delete-security.md); recheck open #147 before assuming Bank-review behavior is merged |
| Auth/secrets/public routes | [auth reference](docs/authentication.md), [ADR-0008](docs/adr/0008-per-environment-secrets-payload.md) |
| Storage/migration/import/cutover | [ADR-0013](docs/adr/0013-postgres-aurora-data-api.md), [ADR-0014](docs/adr/0014-categories-mirror-postgres.md), [cutover](docs/expense-cutover.md); [import record](docs/historical-expense-import.md) only for historical provenance |
| Reporting/query/history | [Sheets refresh](docs/sheets-refresh.md), [history contract](docs/expense-history-export.md), [category/month query](docs/expense-category-month.md) as applicable |
| Release/deployment/live verification | [operations](docs/operations.md), [cutover evidence](docs/expense-cutover.md), [version decision](docs/adr/0012-version-declared-no-tags.md) |
| Issues/PRs/triage | [tracker](docs/agents/issue-tracker.md); [labels](docs/agents/triage-labels.md) before triage |
| Revival history/planning | [dated revival plan](docs/guito-revival.md); only relevant ADRs |

## Living doc

Retain rules for recurring corrections; prune obsolete/duplicate rules in the
change that invalidates them. Keep detail in task references, check routing links
and size, and do not append incident logs or undated deployment claims here.
