# Cross-repository documentation restructuring (#149)

## Disposition

Documentation and active **meireles** skill changes are implemented for review. **Issue #149 is not complete:** enforced read-only Hermes + Sol discovery trials are blocked. Baseline **0/4**, revised **0/4**; no model transcripts or behavioral passes are claimed. José owns cross-repository disposition; neither component PR should use a closing keyword for #149.

This is documentation-only work. Application versions, source, deployment workflows, datastore authority, credentials, Figma and financial records are unchanged. The default Hermes profile is outside scope. Local profile changes are not shipped by a repository merge and have not been rolled out to another machine.

## Immutable inputs and ownership

- API base: `49ffc0242fa5d6b138f8cb0a79060f13e84bd5a2`.
- UI base: `744797e6335875850559eae2261d4dacd7fa4278`.
- Each uses its own `feature/149-docs-router` branch and issue worktree. Shared-root HEAD and full porcelain status matched the saved initial state after implementation/build checks, including other sessions' staged/untracked changes.
- API #148 is merged; API Bank-review #147 and UI #71/#72 were open/unmerged at the recorded baseline. Branch-specific contracts remain distinct from master implementation. Recheck live PR state before subsequent work.
- Skill snapshots include a newer concurrent UI lesson than the original parent snapshot and preserve a concurrent API Expense/Category authority lesson. Restoration must not overwrite them.

## Measurements

Units are Unicode characters, UTF-8 bytes, line counts and **ceil(characters/4)** estimates per file—not validated Sol token counts, inference telemetry or performance results.

| Mandatory entry point | Before | After | Unit |
|---|---:|---:|---|
| API AGENTS | 3,841 | 1,594 | estimated tokens |
| UI AGENTS | 1,895 | 1,373 | estimated tokens |
| guito main skill | 99,881 | 6,734 | characters |
| guito-ui main skill (immediate mutation baseline) | 96,307 | 5,642 | characters |

The original parent UI snapshot measured 95,465 characters; the immediate mutation snapshot includes intervening lessons. API root and skill modestly exceed the editorial 1,500 estimate to retain directly visible safety and explicit routing. There is no hard model-size claim.

Repository Markdown inventory before adding this evidence report: API 33 files/184,099 bytes → 35 files/177,470 bytes; UI 11 files/43,890 bytes → 13 files/54,030 bytes. These are **archive totals, not mandatory loading**. Added design/operations depth can grow an archive while the entry point shrinks.

[Machine-readable inventory and link evidence](149-docs-inventory.json) records every measured file, immutable base, revised hash and static representative chain. Evidence artifacts themselves are excluded from those frozen archive measurements; later edits require regeneration or a new snapshot. Ordinary links do not recursively load their targets.

Representative revised explicit-read prerequisite chains (not observed model reads or whole-task budgets): API contract 61,255 characters/15,318 estimated tokens; UI visual 49,362/12,343; deployment/financial review 54,393/13,601. Each lists selected required references, not all conditional branches. Cross-repo work requires both roots explicitly; further task-specific contracts add to its budget. Historical baseline read-first chains and skill comparisons are retained in the local evidence bundle; no runtime before/after context comparison was possible.

## Static verification and execution

- 135 local repository Markdown targets checked across the measured archives: no missing targets. External URLs were classified but not exhaustively fetched; anchors are not validated. Profile routing verification separately passed 54 checks.
- Root commands were checked against `.slnx`/csproj, actual npm scripts, Angular/Playwright configuration and workflow definitions. `npm run build` does not generate tokens; the deploy workflow's label does not change that fact.
- Both `git diff --check` gates passed; only owned documentation/evidence paths are staged. No application/version/workflow diff.
- API: `dotnet build guito-api.slnx` passed with five nullable warnings in unchanged tests. `dotnet test guito-api.slnx --filter "Category!=Integration"` passed: **545 API + 79 authorizer tests**. Integration suites had no matching tests and were not executed. Used `DOTNET_USE_POLLING_FILE_WATCHER=1` for the host's file-watch limits.
- API ancillary hermetic gates: Apps Script syntax and **28 Node tests**, plus **24 Python deployment/IAM-policy tests**, passed. Their simulated write/readback output is fake-transport test output, not AWS/Sheets operations.
- UI: explicit tokens → TypeScript `--noEmit` → production build → **263 unit tests (32 files)** → **27 Playwright passes, one skipped deployed smoke**. `SMOKE_URL` was not supplied; no deployed/live acceptance is claimed. Temporary dependency symlink remains untracked and is excluded from staging.
- Actual revised skill main/reference loads returned meireles paths without spill. Full result-envelope estimates remain bounded; archived originals and recovered spillover are available locally.

No TDD production seam was introduced: this task changes documentation/procedures, not application behavior. Static regression checks and existing hermetic suites provide the applicable gates.

## Safety and convention retention

| Essential requirement | Authoritative retained location | Load trigger |
|---|---|---|
| One top-level type per file, layer/model/DTO ownership, coding/testing conventions | Both roots; each `docs/CONVENTIONS.md`; API ADR-0011 | Before applicable source edits/review |
| Hermetic versus separately approved live checks; public/transport seams | Both roots/conventions; API operations; UI operations | Test planning/execution |
| Dedicated worktree even sequential; branch/index ownership; explicit staging; attribution; owner merge | Both roots and tracker guides | Every issue/commit/PR |
| Complete body files, explicit existing labels, readback, cross-repo closure | Each tracker/label guide | Tracker operations |
| No secret printing/committing; no `~/.aws` modifications; approved role only | Both roots and operations; skill invariants | Always visible; detail before operations |
| Routine pipeline deployment/migrations; exceptional owner-scoped operator actions | API `operations.md`, UI `agents/operations.md` | Deployment/configuration review |
| Explicit staging, last-deployment-wins and independent UI/API provenance | Both roots/operations; UI staging-provenance skill reference | Approved review/deployed checks |
| Financial write/delete/reset/import approval; Sheets-only smoke cleanup, never personal/Postgres | Both roots/operations; API cutover and smoke ADRs | Financial/deployed operation planning |
| Exact amounts, durable numeric Expense identity, Lisbon occurrence, null/legacy boundaries | Glossaries/domain guides; API ADR-0015; UI ADR-0014; exact-contract skill procedures | Domain/DTO/storage/prefill edits |
| Revision preconditions and uncertain outcomes, no blind retries | API mutation/delete-security contracts; exact-contract skill procedures | Expense edit/delete/reconciliation |
| Storage configuration versus actual authority, historical decisions and unperformed rehearsals | API cutover evidence, superseding ADR notes, operations | Authority/cutover/import review |
| Reporting is authenticated/restricted, never public financial data | API auth/reporting contracts, ADR-0017; reporting skill recipe | Reporting/auth changes |
| Design-first owner approval; token mapping/generation; Community original read-only | UI root/design/conventions/ADR-0010; Figma procedures | Visual/interaction/token work |
| Repo facts versus reusable procedures; conditional references and full spill recovery | Both profile routers and scoped references | Relevant skill task only |

Full procedure-level retention, skill diff, input/after snapshots, manifest, result-envelope estimates and concurrent-safe restoration are in the owner-local evidence bundle delivered with this task. Restore by comparing current hashes and applying reviewed operations/three-way merges; never blindly copy snapshots over active skills. API main always requires a manual merge to preserve concurrent authority guidance.

## Blocked discovery trials

Inspected clean Hermes runtime `3637c512fc3211bdccb61b7140d09ac1018508ba` and current documentation. Recorded selection: meireles, `openai-codex`, `gpt-6.1-sol-900k`, medium reasoning. These are selected settings, not a verified resolved inference model/window.

The stock CLI `file` toolset includes write/patch; `skills` includes skill mutation. `--safe-mode` discards the tested profile/rules. Available namespace primitives alone do not establish restricted file access, inference-only egress, runtime-only credentials or isolated state. No verified containment launcher was found, so no unsafe trial agent ran.

The local bundle preserves four frozen prompts and essential-rule checklists, installed-source excerpts, namespace probes and the precise blocker. Trials remain unchecked. A new reviewed containment mechanism and actual baseline/revised executions are needed; static checks do not substitute for them. No claim of Sol speed, cost or task-quality improvement is made.

## Publication gate

José explicitly approved `[skip ci]` in documentation commit messages because both feature-branch workflows otherwise deploy shared staging. Publish owner-reviewed component PRs without deployment; GitHub checks may remain absent/pending. Do not remove the directive or dispatch workflows to manufacture green checks. Keep this report's blocked criterion visible during review and leave issue #149 open.

## Independent code review

Standards found one hard contradictory test-seam instruction and two editorial judgement calls; Spec found that same contradiction plus missing API-only root discovery. Corrected both actionable findings: real composition uses fake external transports, and API-only existing/home-rooted tasks explicitly obtain current root instructions. Grouped UI essential conventions into bullets and made decimal edge-case verification derive expected acceptance from repo authority. Final links/hashes were regenerated after corrections. Blocked trials are unchanged.
