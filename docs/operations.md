# Operations and verification gates

Read before release/deployment review, migration, live checks or financial operations.
This is not execution approval. [Cutover](expense-cutover.md) owns authority/recovery
and dated owner evidence; [ADR-0007](adr/0007-staging-environment.md) owns staging parity;
[ADR-0012](adr/0012-version-declared-no-tags.md) owns versioning.

## Pipeline and provenance

Routine application deployment, packaging and pending migrations run through
`.github/workflows/ci-cd.yml` (Amazon.Lambda.Tools, OIDC), not local deployment scripts.
Pushes to `feature/*` deploy shared staging after tests; pushes to `master` deploy
production. A docs-only feature push can therefore replace staging: obtain the
appropriate approval before pushing/dispatching. Explicitly choose `environment=staging`
for an approved review dispatch; a workflow's default is not sufficient target evidence.

Shared staging is last-deployment-wins. Inspect exact deployment SHA/stamp, installed
Expense selector and sibling routes/schema before replacing it or claiming acceptance.
A successful unrelated run, local HEAD or schema-compatible SQL proves neither that
this branch is served nor that sibling routes survived. Never reset its database to
work around another branch. Watch the exact approved head-SHA run and read back the
target build/store. `/healthz` exposes version in `X-Api-Version`, not its body.

`deploy/deploy.sh` supports the optional EXPENSE_STORE selector; omission preserves
installed authority. Staging Actions explicitly selects Postgres; production Actions
omits it. Runtime permits configured Postgres in all environments. Committed production
Sheets default does not prove installed authority. No dual writes or stale-Sheets
rollback after Postgres writes. See the cutover record before any authority decision.

Exceptional one-time operator configuration requires José's explicit scope: read
current variables, preserve unrelated values, guard RevisionId, wait and read back.
This exception is not permission for local code deploys, IAM changes or DB writes.
AWS agent operations use only `arn:aws:iam::497087877832:role/MinervaAIAgent`;
never modify `~/.aws`, use the owner's identity, expose secrets or invent approval.

## Tests and live acceptance

Hermetic solution tests use `--filter "Category!=Integration"`. CI additionally checks
Node 22 Apps Script syntax/tests and Python deployment-policy tests. No network,
credentials or live datastore is allowed in this gate. HTTP tests use
CustomWebApplicationFactory and fake Sheets/OpenRouter/Data API transport with actual
composition. Secret-provider/key-loader contract tests may use public provider seams;
SDK fakes must pin dummy credentials and region, not inherit AWS configuration.

`scripts/run-deployed-tests.sh staging` is a separately approved live check, not a
unit-test command. It resolves SSM credentials, mints a Google token and runs business
plus auth-contract SIT. It creates/lists/deletes only dedicated formula-free Sheets
Smoke Test records (`X-Sheet-Scope: smoke`, `DELETE /Smoke/{id}`), even when personal
Expenses use Postgres. Cleanup only the returned smoke ID. Never treat personal or
Postgres IDs as smoke IDs. Smoke proves that isolated transport path, not personal
Postgres persistence, database constraints, full reporting refresh or UI acceptance.

Personal create/update/delete, live Postgres checks, import, reset and cleanup need
explicit approval of target/data/retention. On uncertain mutation outcome, stop and
inspect read-only before retrying; do not blindly replay or delete to manufacture a
clean test. Preserve exact values and identity. Local dev must never target production.
Do not resurrect retired import/provenance tooling or repeat owner-accepted parity.

## Credential/IAM maintenance

Deployment preflights provisioned SSM parameters; it does not create replacement keys.
Database Secrets Manager credentials are retained. `deploy/reconcile-iam.py` defaults
to dry run. `--apply --rollout-verified` requires separate authorization and real
merged-production rollout plus both environments' deployed suites; the flag attests
those checks, it does not perform them. Preserve database/SSM/unrelated permissions,
never change Minerva policies, read back every target and stop on partial failure.
Do not run apply merely because an old cleanup paragraph says “after this merge”.

Record date, environment, exact ref/stamp/run, executed versus unperformed checks and
approval source. Code, CI, owner acceptance, visual approval and deployed state are
separate evidence. Reverify live facts before acting; no secrets or financial rows in git.
