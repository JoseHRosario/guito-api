# One-time Expense cutover (#132)

## Owner disposition recorded 13 October 2026

[Closure evidence](https://github.com/JoseHRosario/guito-api/issues/132#issuecomment-6135613997)
records José's acceptance: production Postgres authority, working saves, private
reporting installation (98 records), editor reopen refresh and UI reads. It cites
UI merged ref `744797e6335875850559eae2261d4dacd7fa4278` and
[deployment run](https://github.com/JoseHRosario/guito-ui/actions/runs/38328944997).
This is dated owner/agent-reported evidence, **not a fresh live configuration audit**.
Distinct pre-switch abort/post-switch recovery rehearsal was **not performed**;
latest new-Expense appearance in Sheets was not independently verified by the agent.
Accepted import/parity was not repeated. Source-sheet edits do not resume as authority.

[Subsequent owner approval](https://github.com/JoseHRosario/guito-api/issues/133#issuecomment-6157279425)
allows #133 design/development without retroactively claiming those rehearsals.
Its newer documentation supplied this correction; API Bank-review PR #147 at
`44bc637af59a5cda10e2291446164af227a39026` is open/unmerged at the #149 baseline.
No proposed ADR-0018 or Bank-review behavior is treated as merged here; staging
persistence/constraint acceptance remains separate. Reverify live authority/ref and
obtain explicit scope approval before any new operation.

## Historical #132 preparation record (retained below)

The original BLOCKED/not-approved instructions and pending checklist below describe
pre-execution preparation. The dated owner disposition above supersedes their status,
not their safety/recovery rationale. They do not reopen #132 or authorize repeat
execution; unperformed steps remain unperformed.

## Status and evidence boundary

**Staging readiness: BLOCKED. Production execution: NOT APPROVED / NOT RUN.**
Code and hermetic tests are not a source freeze, live rehearsal or cutover. Keep
#132 open; no closing keyword until approved execution and live acceptance.
This PR does not authorize merge/deploy, configuration changes or production writes.

Historical import/parity and verified reruns are complete under #129. José retired
the importer and provenance tables. Do not recreate, reimport or request another
comparison exercise. The [later owner decision](https://github.com/JoseHRosario/guito-api/issues/132#issuecomment-6134696570)
supersedes repeated-import acceptance. Use existing private evidence and José's
source-verified/unchanged assertion. If subsequent edits occurred, stop for an
explicit drift/recovery decision rather than infer current parity.

Staging's private Apps Script setup is owner-complete (#131). José reported signed
identity verification PASS for the separate production project, including verified
email. Exact public audience/subject remain in owner evidence, not committed
configuration (ADR-0017). No token was supplied or stored. This is not deployed
reporting-route, full refresh or spreadsheet-open acceptance.

## Permanent behavior, not a cutover interface

- The existing `AppConfiguration:ExpenseStore` selects personal reads and writes
  together. Explicit `Postgres` now works in Production; no second approval flag,
  pause middleware, dual writes or fallback. Production appsettings remains `Sheets`
  until the approved one-time Lambda override.
- `deploy/deploy.sh` accepts the existing optional `EXPENSE_STORE=Sheets|Postgres`.
  Omission preserves installed authority through the existing Lambda environment
  merge. Production Actions omits it: a merge/deploy alone does not switch authority,
  and routine deployments after the switch do not pin stale Sheets.
- Categories and isolated Sheets smoke CRUD are unchanged. Smoke is not proof of
  personal Postgres persistence; `/Smoke/{id}` cannot clean up personal data.
- Reporting uses existing `deploy/sheets-reporting-config.py`, exact signed identity
  validation and GET `/Expense/history` confinement at both gates. There are no new
  workflow-dispatch choices. Production environment variables wire the existing
  selector/approval mechanism; routine deployment defaults to preserve.

## Coordinated manual pause and staging readiness

José stops personal API/UI use, scheduled or agent writers, and direct source-sheet
edits for the agreed window. Wait for all admitted requests to finish; record how
writers were stopped and drain was established. **There is no server-side 503 write
barrier**: configuration and deployments do not enforce this pause. If any writer
cannot be held, do not switch until an explicitly approved operational stop exists.
No new generic pause/control framework is part of this PR.

Staging readiness stays BLOCKED until an approved real coordinated hold/abort,
Postgres read/write and reporting refresh/failure/recovery exercise, and José's
editor reopen evidence are recorded. Staging already uses Postgres: abort keeps
that authority, never invents a Sheets rollback. Use existing data; a bounded
personal acceptance write and retention decision needs approval. No import/reset.
This development readiness checkpoint is distinct from production execution approval.

## Future approved one-time operator checklist

1. Obtain José's explicit approval for the merge/deploy window, writer/source hold,
   production selector change and reporting enablement separately. Identity setup
   is not execution approval. Review the exact ref/build. Merge and code deployment
   remain through existing `ci-cd` Actions; merging master triggers production
   deployment, so do not merge before approval. That deployment still serves Sheets
   until the deliberate override. Verify deployed build and installed selector.
2. Hold all writers and direct source edits, drain admitted requests, and obtain the
   source-verified/unchanged assertion. Capture current selector/build and private
   recovery evidence. Do not claim a freeze based on this document.
3. Make the one-time approved configuration edit on API Lambda `guito-api`:
   `AppConfiguration__ExpenseStore=Postgres`. Use the operator's existing AWS console
   or CLI configuration mechanism, reading current environment first, preserving
   **all other variables**, and guarding CLI updates with the current revision ID.
   Wait for completion and read back the exact target/selector. Do not run a local
   production deployment or add a pipeline switch input.
4. Configure production reporting once through existing Actions **environment**
   variables: `SHEETS_REPORTING_AUTHENTICATION=enabled`, verified exact
   `SHEETS_REPORTING_GOOGLE_AUDIENCE` and `SHEETS_REPORTING_GOOGLE_SUBJECT`,
   `SHEETS_REPORTING_IDENTITY_VERIFIED=true`, and separately approved
   `ALLOW_PRODUCTION_SHEETS_REPORTING=true`. Dispatch existing `ci-cd` with only
   `environment=production` on reviewed master. The existing selector writes both
   gates; no credentials/selectors go in git or chat. This session sets none.
   After readback, clear temporary claims/approval environment variables and set
   reporting mode to `preserve`; later deploys retain installed identity.
5. Read back **both** Lambdas; verify actual authenticated personal API/UI latest,
   category/month reads and paginated history against Postgres. Confirm exact IDs,
   amounts and Lisbon timestamps, unchanged human/agent auth and reporting-only
   access/rejection on writes and other routes. Install the separate production
   private loader per [its runbook](sheets-refresh.md); exercise all-page refresh
   and recovery. José reopens as an editor and confirms trigger/status/API-fed data.
   Viewer-only open, healthz and Sheets smoke do not establish these outcomes.
6. Reopen only with José's confirmation. An explicitly approved bounded personal
   API/UI create must persist in Postgres, appear in history and next complete open
   refresh. Retain the record unless separate cleanup is approved. Coordinate the
   time-enabled UI; do not send intraday writes while Sheets remains authoritative.
   Categories remain Sheets-authoritative with their existing mirror. The API-fed
   tab is replaceable reporting data, never reverse-sync authority; direct
   authoritative Expense edits in Sheets do not resume.

Two-Lambda configuration changes are not atomic. On failure inspect actual state
and retain the hold; workflow success alone is not a coherent identity or switch.

**Pre-switch abort:** while Sheets remains authority and no Postgres personal writes
were admitted, abandon the switch and keep Sheets. Reopen only with José's approval;
disable any newly enabled reporting identity coherently if necessary.

**Post-switch recovery:** hold all writers/source edits again, retain Postgres and
its new records/IDs/audit dates, and repair API/UI/reporting forward. A compatible
previous build must still honor Postgres selection. Never deploy an older script
that pins Sheets or flip the selector back blindly: explicit Sheets is technically
possible but requires independently approved reconciliation of every new Postgres
write and coherent read/write restoration, not an ordinary rollback.

## Completion evidence (pending)

Record approvals/window, merge/deploy refs and runs, writer hold/drain, selector
and two-gate readbacks, authenticated personal API/UI Postgres acceptance,
reporting security/full-history refresh and owner editor reopen, plus distinct
abort/recovery outcomes. No financial rows, tokens or private archives in git.
None of these live outcomes is asserted here; #132 stays open until complete.
