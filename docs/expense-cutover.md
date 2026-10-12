# Controlled Expense cutover (#132)

## Status and evidence boundary

**Staging readiness checkpoint: BLOCKED. Production execution: NOT APPROVED / NOT RUN.**
Code and hermetic tests are not a live rehearsal. Keep #132 open; no closing keyword
in its PR until approved production execution and evidence are complete.

The source import, parity and identical reruns are already complete under #129.
José retired the importer and provenance tables. Do not recreate them, reimport,
request another snapshot/comparison exercise, or claim a fresh parity check.
The [recorded scope decision](https://github.com/JoseHRosario/guito-api/issues/132#issuecomment-6134696570)
supersedes the original repeated-import acceptance wording. Use the private existing
archive/evidence and José's **source already verified and unchanged** assertion.
If subsequent source edits occurred, stop and obtain an explicit drift/recovery
decision; do not infer parity or silently switch an out-of-date target.

Staging private Apps Script setup is owner-complete (#131). The separate production
project has an owner-reported signed-identity PASS. Its verified audience and subject
remain in owner evidence and are supplied through the production Actions environment's
`SHEETS_REPORTING_GOOGLE_AUDIENCE` and `SHEETS_REPORTING_GOOGLE_SUBJECT` variables;
no exact selectors are committed (ADR-0017). Both gates require `email_verified=true`.

No token was supplied or stored. This establishes identity setup only, not deployed
reporting authorization, full refresh, reopening acceptance or execution approval.
No credentials, financial rows or private archives belong in the repository.

## Controls (disabled by default)

- `AppConfiguration:ExpenseStore` stays `Sheets` by default. One selector switches
  personal reads and writes together; no dual writes.
- `AppConfiguration:ExpenseCutover:ProductionPostgresApproved` defaults `false`.
  Both repository and history guards require it for Production Postgres. Approval
  alone does not select Postgres. Unapproved Postgres fails closed, without fallback.
- `AppConfiguration:ExpenseCutover:WritesPaused` defaults `false`. After authentication,
  personal POST `/Expense` returns 503 ProblemDetails before repository resolution
  or transport writes. Reads, Categories and authentication remain unchanged.
- Smoke create/read/cleanup stays isolated in Sheets; the pause does not block it.
  Smoke success does **not** establish personal Postgres persistence. Never use
  `/Smoke/{id}` to clean up personal data.
- Production reporting identity has its own approval, independent of Expense
  authority: verified exact audience/subject plus `ALLOW_PRODUCTION_SHEETS_REPORTING=true`
  when explicitly enabling both gates. Its only permission remains GET `/Expense/history`.

Lambda equivalents are `AppConfiguration__ExpenseStore`,
`AppConfiguration__ExpenseCutover__ProductionPostgresApproved`, and
`AppConfiguration__ExpenseCutover__WritesPaused`.

Deploy only through `ci-cd` Actions. The shell selectors run before migrations or
resource mutations. `EXPENSE_STORE=preserve|Sheets|Postgres` and
`EXPENSE_WRITES_PAUSED=preserve|true|false` default to `preserve`.
Explicit production Postgres requires `ALLOW_PRODUCTION_EXPENSE_POSTGRES=true`;
the first production switch requires **both installed and resulting pause=true**.
A single dispatch cannot activate the pause and switch from an unpaused stack.
Installed pause alone does not prove requests drained or direct source edits stopped:
those confirmations remain operator evidence required before the switch.
New stacks stay Sheets/unapproved/unpaused; routine deploys preserve installed
Postgres, approval, pause and consistent reporting claims. Invalid selectors,
malformed installed state, denied discovery and unapproved production Postgres
abort. **Postgres → Sheets is refused even explicitly:** normal deployment is not
an emergency stale-source rollback tool. Production push never selects Postgres,
unpauses writes or enables a reporting identity by itself.

## Staging rehearsal — evidence needed to unblock readiness

Use existing staging and the owner-installed private script; no import or reset.
Run approved staging dispatches with `expense_writes_paused=true`, then `false`
(the staging job retains Postgres authority). Record exact workflow runs/builds,
installed selector readbacks, time window, owner source assertion and outcomes.

1. Arrange José's stop on **manual source-sheet edits**, as well as all personal API
   create paths (manual/favorite/voice/bank-prefilled). The API gate cannot stop
   direct Sheets edits. Activate the API pause, wait for deployment completion and
   drain requests admitted before the pause. Verify authenticated personal creates
   return 503, anonymous requests retain auth failures, reads and Categories work,
   and smoke stays confined to its configured tab.
2. Demonstrate pre-switch abort without changing data or importing: failed/missing
   approval refuses production selection in hermetic tests; in staging, exercise
   the paused hold/abort procedure. Since staging already uses Postgres, an abort
   **keeps that existing authority**; never invent a staging Sheets rollback.
3. Verify actual Postgres-backed personal API/UI reads and paginated history, exact
   IDs/amounts/timestamps, independent human/agent auth and reporting-only rejection
   on write/other routes. Use existing real records for read verification. Any real
   personal write requires its own approved bounded record and retention/recovery
   decision; Sheets smoke alone is not evidence. No destructive personal cleanup.
4. Exercise the private loader's all-page refresh and last-good-copy failure/recovery
   behavior using [the loader runbook](sheets-refresh.md). José reopens as an editor
   and confirms the installable trigger ran, status succeeded and API-fed data is
   current. Viewer-only open is not an acceptance claim.
5. Demonstrate post-switch recovery as **pause and repair forward on Postgres**, not
   stale Sheets fallback. Preserve any accepted new writes and IDs, show restored
   API/history/reporting and record the distinct recovery outcome.
6. Reopen only with José's confirmation; record successful API/UI write/read and
   subsequent full-history refresh for an approved record. If these fail, re-pause,
   preserve new Postgres writes and diagnose before reopening again.

Only after real rehearsal, distinct abort/recovery evidence and owner reopen evidence
may the checkpoint say READY. This checkpoint permits reconciliation development
independently of production execution approval; it is not authority to deploy or
switch production. No real rehearsal outcomes are asserted by this document.

## Approved production execution — future operator checklist

First merge/review the code with controls preserving current state, complete staging
rehearsal and obtain José's explicit approval for the **execution window**, API/UI
coordination and reporting enablement separately. Setup approval is insufficient.
Use the reviewed `master` ref and the `production` Actions environment. The operator
supplies the verified public reporting claims through that environment's existing
`SHEETS_REPORTING_GOOGLE_AUDIENCE`, `SHEETS_REPORTING_GOOGLE_SUBJECT` and
`SHEETS_REPORTING_IDENTITY_VERIFIED=true` variables only after approval. This change
does not set those variables or deploy anything.

| Dispatch stage | Relevant inputs | Required result before next stage |
| --- | --- | --- |
| Pause on existing Sheets | `environment=production`, `expense_store=preserve`, `expense_writes_paused=true`, reporting `preserve`, both approvals `false` | Owner manual-edit freeze; deployed pause readback, 503 and drained in-flight writes; existing source-verified/unchanged assertion |
| Switch while paused | `expense_store=Postgres`, `expense_writes_paused=true`, `allow_production_expense_postgres=true`; reporting `enabled`, `allow_production_sheets_reporting=true` | Execution approvals recorded; Postgres/approved/paused readback; same authority for personal API/UI reads and history; both exact reporting gates; actual complete loader refresh/security checks |
| Reopen | `expense_store=preserve`, `expense_writes_paused=false`, reporting `preserve`, both approvals `false` | José authorizes reopen after read/refresh checks; approved API/UI create persists in Postgres, appears in history and next open refresh; no direct authoritative Sheets edits resume |

All other omitted inputs use their safe defaults. Read back both exact Lambda targets
and actual responses after each dispatch; workflow success/healthz/Sheets smoke alone
is insufficient. Coordinate the already time-enabled UI with the API authority switch;
do not enable intraday UI writes while Sheets is authoritative. Categories retain
Sheets authority and their existing mirror; no category migration is part of this.
Keep the source archive and formulas under José's ownership; the API-fed tab becomes
replaceable read-only reporting data, never a reverse synchronization source.

**Pre-switch abort:** while Sheets is still the authority and no new Postgres writes
were admitted, keep Sheets; abandon the switch, disable any newly enabled reporting
identity if necessary, reopen the existing source only after José confirms. An
unapproved/failed selector aborts before mutations. If code/config rollout fails,
verify actual installed state and keep the pause until understood; do not assume an
atomic multi-Lambda deployment.

**Post-switch recovery:** from the authority switch onward, assume Postgres may have
accepted new writes until proven otherwise. Re-pause (`preserve`, `true`) and stop
manual source edits. Retain Postgres, its records/IDs/audit dates and private incident
evidence; repair API/UI/reporting forward. A compatible previous build may be used
only if it still honors Postgres authority and the pause. Never deploy an older
Sheets-pinning script or blindly revert configuration. Restoring Sheets would require
an independently approved reconciliation of **every new Postgres write** and coherent
read/write restoration; this selector intentionally cannot perform it.

Production completion requires real pause/switch/reopen evidence, authenticated
API/UI Postgres acceptance and full-history spreadsheet-open refresh/security
acceptance, not repeated historical import parity. Until then #132 remains open.
