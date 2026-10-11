# Historical Expense import rehearsal (#129)

## Safety boundary

This release is API **0.14.0**, based on merged **0.13.0**. It does not switch Expense
authority, write to Sheets, deploy, import production, or clean up personal records.
Smoke still runs exclusively against the configured Sheets Smoke Test tab.

Live import is **manual GitHub Actions staging-only**. The tool pins `guito_staging`,
the existing cluster, the staging DB secret prefix, the pipeline role and the staging
source configured in base `appsettings.json`. It rejects production/local import.
The read-only capture command can run locally with an existing staging Google key;
never copy credentials into the repo or modify `~/.aws`. Agent AWS discovery requires
`arn:aws:iam::497087877832:role/MinervaAIAgent`; credential refresh is José's manual
`aws login`. The GitHub pipeline uses its existing `guito-api-deploy` OIDC role.

## Live prerequisites (not established by hermetic tests)

1. Parent review, approved staging pipeline deployment of this exact SHA, and successful
   application/read-back of migration **007_historical_expense_import.sql** through
   the existing deployment workflow. Never run migrations manually or reset migration
   ledgers/tables. Every 007 statement is independently resumable; the current runner
   splits on semicolons and does not support procedural SQL blocks.
2. Verify staging role Data API/Secrets Manager/SSM permissions and seeded category
   mirror parity. Rehearsal never seeds/categories or changes their authority.
3. Set a strong, random **`IMPORT_ARCHIVE_PASSWORD`** secret on the GitHub **staging**
   environment using the GitHub UI. Retain it securely for archive recovery; it is not
   a credential embedded in source. Do not share it in chat or rotate it until prior
   evidence is recovered/re-encrypted. Keep a restricted long-term encrypted backup;
   GitHub artifacts expire after 30 days. Public-repository artifacts must never carry
   plaintext financial history; this workflow uploads only authenticated ciphertext.
4. José verifies the staging source tab is the complete intended history, its final
   **grid** row (not latest Expense count), explicit EUR convention and decimal
   separator and date display convention. Pause manual sheet edits and API Expense writes throughout capture.
   Capture checks two repeated formatted/unformatted reads, but is not revision-atomic.
   Operator-confirmed bounds are essential: readiness proves no rows outside those
   bounds. Current bounded limit is 100001 rows starting at the configured anchor.
5. Explicitly select the source date convention (`yyyy-MM-dd` default, `dd/MM/yyyy`,
   `d/M/yyyy`, `MM/dd/yyyy` or `M/d/yyyy` via `-f date_format=...`). Unpadded month/day
   displays require `M/d/yyyy`; this is not interchangeable with `MM/dd/yyyy`. Numeric underlying dates must agree with
   that day and carry no hidden historical time. Currency may be absent under the
   explicitly declared EUR convention or a single explicit EUR/€ prefix or suffix.
   Dot decimals allow comma thousands grouping only with a 1–3 digit first group
   followed by exactly three digits per group (for example `€1,234.56`). Comma decimals
   remain ungrouped. Both views must be well-formed and exactly representable. A stored
   underlying **numeric** token is authoritative, without rounding, even when display
   formatting hides precision. Each unequal display/number pair carries a nonblocking
   `FormattingDifference` audit note and both original tokens in `RawJson`. Underlying
   text still requires exact displayed agreement; missing/invalid values never fall back
   to the display. This is José's approved source-precision policy, not a cent tolerance
   or a claim that every differing display is proven rounding (format metadata is not
   captured). Repeated capture reads and the operator write pause remain required.
   USD/conflicting currency markers, malformed grouping, unsupported date layouts/precision,
   missing category/creator and unknown mirror
   labels are exceptions, not repaired guesses. Do not reformat the live sheet to
   make an import pass. Investigate exceptions and approve any supported follow-up.
6. Inventory any pre-existing staging Expenses separately. Import provenance does not
   claim ownership of ordinary API-created Expenses and never content-deduplicates them.
   An identical source purchase may legitimately be new, or need operator reconciliation.

## Exact rehearsal command

Before merge, dispatch the **already registered** `ci-cd.yml` workflow against the
reviewed feature ref containing these changes. The new reusable workflow does not
need its own default-branch registration. This performs the usual staging deploy
and smoke first, then the explicitly requested import; normal pushes and default
manual deployments never import. Set all source conventions from operator verification:

```bash
# Set LAST_ROW, DECIMAL_SEPARATOR and DATE_FORMAT only after José verifies the
# complete source bounds/conventions and pauses source/API Expense writes.
REF=feature/129-historical-import
gh workflow run ci-cd.yml \
  --repo JoseHRosario/guito-api --ref "$REF" \
  -f environment=staging -f rehearse_history=true \
  -f last_row="$LAST_ROW" -f decimal_separator="$DECIMAL_SEPARATOR" \
  -f date_format="$DATE_FORMAT"
```

`IMPORT_ARCHIVE_PASSWORD` must already exist on the staging environment. The reusable
job binds `environment: staging` and resolves this secret only in the three consuming
steps (preflight, retry recovery, encryption), not job-level `env`. Environment secrets
are supplied by that job's environment, not forwarded by the caller; adding
`secrets: inherit`/a required `workflow_call` secret is not a substitute for environment
binding. Do not copy the password to a repository secret or `$GITHUB_ENV`.
See [GitHub's environment-secret rules for reusable workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows#using-inputs-and-secrets-in-a-reusable-workflow).
Production
plus rehearsal is rejected before deployment. The caller owns `ci-cd-staging` across
deploy, smoke and rehearsal; the reusable child has a distinct per-run concurrency
key so it cannot deadlock waiting for its parent. Standalone dispatch of
`historical-import-rehearsal.yml` remains available after default-branch registration
and shares the staging lane, but does not deploy/apply DDL itself.

The workflow captures the source, archives it before import, commits bounded rows
individually, reads every persisted Expense back, reruns the same snapshot for
idempotency, and encrypts evidence on successful and blocked runs. Exit **0** means
report readiness; **2** means exceptions/failed parity/blocking; **1** is operational
failure. Watch the exact run, not merely the dispatch response. No financial cells
or provider error details are printed into public CI logs.

Run **38171349557** failed at the protected-evidence preflight with an empty resolved
password, before capture/import; no snapshot/artifact or imported rows were produced.
The staging environment secret's existence was operator-verified. The step-scoped
resolution change is hermetically checked, but live secret resolution still needs
verification in the next approved run. That next run must **capture anew**, not use
`retry_run_id=38171349557`. For the operator-verified 987-row grid, EUR dot decimals
and `M/d/yyyy` display, run the reviewed branch head:

```bash
gh workflow run ci-cd.yml \
  --repo JoseHRosario/guito-api --ref feature/129-historical-import \
  -f environment=staging -f rehearse_history=true \
  -f last_row=987 -f decimal_separator=. -f date_format=M/d/yyyy
```

Offline validation of the protected actual capture accounts for 43 valid Expenses,
940 blanks and zero exceptions, with the row 41 formatting-difference note. This
does not establish live persistence or authorize production cutover.

## Retry, inspect, and reconcile

Download `historical-import-evidence` from the exact run. It contains `evidence.tar.enc`
and `evidence.mac`. Recover only in a restricted local directory, with the archive
password supplied through a protected environment/UI, not command arguments:

```bash
# ARCHIVE_PASSWORD already provided securely; do not print it.
python3 -c 'import os,hmac,hashlib,pathlib; p=pathlib.Path("."); assert hmac.compare_digest((p/"evidence.mac").read_text().strip(),hmac.new(os.environ["ARCHIVE_PASSWORD"].encode(),(p/"evidence.tar.enc").read_bytes(),hashlib.sha256).hexdigest())'
openssl enc -d -aes-256-cbc -pbkdf2 -in evidence.tar.enc \
  -pass env:ARCHIVE_PASSWORD | tar -xz
```

Inspect `report.json`, `retry-verification.json`, the hash-named `*.snapshot.json`
and any `approval.json`. Expected amounts serialize as exact strings, actual amounts
and all totals are exact text. Compare **every row**, not just aggregates. The row's
raw evidence includes both formatted and underlying cells. The report separates
nonblocking `FormattingDifference` notes from `Exception`; parity and totals use the
exact underlying numeric amount, not the display. An equal-value trailing-zero or
grouping change does not produce a difference note. Snapshot identity depends only on
immutable raw capture and conventions, not parser policy or notes, so the approved
policy can retry an older blocked snapshot without recapture or duplicate inserts.
The report separates
Blank, NonExpense, Malformed, Failed, parity mismatches, imported and excluded rows.
`SourceTotal` includes valid source amounts, even failures/exclusions. `PersistedTotal`
includes all read-back amounts, `ImportedTotal` only parity-verified rows, and
`ExcludedSourceTotal` excludes malformed amounts from its numeric total. Unparseable
amounts have no invented value and always need explicit resolution/exclusion.

To retry an interrupted import, use the prior run ID; the workflow retrieves the
same frozen archive without Google access/recapture:

```bash
gh workflow run ci-cd.yml \
  --repo JoseHRosario/guito-api --ref "$REF" \
  -f environment=staging -f rehearse_history=true \
  -f last_row="$LAST_ROW" -f decimal_separator="$DECIMAL_SEPARATOR" \
  -f date_format="$DATE_FORMAT" -f retry_run_id="$PRIOR_RUN_ID"
```

Only use a prior run with a downloadable authenticated artifact containing a captured
snapshot. A pre-capture failure (including 38171349557) cannot be retried this way.

Fix transport/category-mirror prerequisites through approved pipelines, then retry.
A failed or lost-acknowledgement row may already have committed: never manually
reinsert it. Safe reruns recover provenance. Earlier successful rows are retained.
If a reconciliation plan was used, pass its same approved manifest on every retry.
Durable exclusions remain recognized even without resubmitting their manifest.

Approvals are explicit reviewed JSON files, containing no credentials. They MUST name
the report's exact snapshot hash. Commit them only when their metadata is safe to
publish; source files and reports themselves must never be committed. Example shape:

```json
{
  "SnapshotId": "<exact new snapshot SHA-256>",
  "Exclusions": {
    "23": { "ApprovedBy": "José", "Reason": "Reviewed non-expense original row" }
  },
  "Reconciliation": {
    "PreviousSnapshotId": "<exact current predecessor SHA-256>",
    "ApprovedBy": "José",
    "Reason": "Reviewed row movements and new purchases",
    "ReuseRows": { "6": 5 },
    "NewRows": [7],
    "RetainPreviousRows": [23]
  }
}
```

`ReuseRows` maps NEW source row → PREVIOUS row, one-to-one, and requires both raw
views to match exactly. `NewRows` explicitly approves distinct new purchases,
including identical content. All new relevant rows must appear exactly once across
ReuseRows/NewRows/Exclusions. Every predecessor relevant row must be resolved and
accounted for. `RetainPreviousRows` is for previously approved **excluded** rows only;
retaining removed imported Expenses blocks the new snapshot before inserts. Changed
values/removal of an already imported row requires restoring/reusing the original or
a separately approved data correction; this tool never overwrites/deletes financial
history. Reconciliation cannot hide unresolved predecessor exceptions. The source
head is locked transactionally; concurrent obsolete runs cannot keep importing it.

Pass a reviewed manifest with `-f approval_file=docs/import-approvals/<file>.json`.
For a new changed capture, omit retry_run_id and provide its newly reviewed manifest;
the first unapproved run safely archives/reports the change without Expense inserts.

## Readiness and abort/recovery

`ReadyForCutover` means every relevant **bounded** source row is imported with exact
amount/day/description/category/creator parity and valid audit fields, or explicitly
approved for exclusion. It does not establish full-source bounds, absence of unrelated
staging rows, real DB constraints, or authorization to switch production. Parent must
verify live DDL/FKs/uniqueness, persistence, actual rerun IDs/audit timestamps and
protected evidence, and José must approve the eventual production cutover separately.

Before switching authority, a failed rehearsal is a safe **abort**: stop and keep
Sheets as current authority. Do not erase source/archive/provenance or successful
rows. After any Postgres authoritative writes, returning to stale Sheets is NOT a
rollback; pause writes and reconcile new authoritative records through a separately
approved recovery plan. This release contains no production write/switch mechanism.

## Hermetic verification and offline tool

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build
dotnet test tst/guito-api.Tests --filter FullyQualifiedName~HistoricalExpenseImportTests
dotnet test --filter 'Category!=Integration'
dotnet run --project tools/Guito.HistoricalImport -- --help
dotnet run --project tools/Guito.HistoricalImport -- validate \
  /restricted/<hash>.snapshot.json /restricted/validation.json
```

Local `capture` uses `GUITO_SOURCE_DATE_FORMAT` for the same explicit date allowlist
(default `yyyy-MM-dd`); select `GUITO_SOURCE_DATE_FORMAT=M/d/yyyy` for unpadded
month/day/year. Its final positional decimal-separator argument remains required.
Neither capture nor validation changes the sheet or normalizes archived raw tokens.

`validate` is local/offline and never touches a database. HTTP fake tests use the real
Sheets client plus fake Data API transport and actual repository/unit-of-work/service,
not per-layer mocks. Full history exceeds both latest and Data API paging boundaries.
CI has no credentials or provider network dependency. Linux full-suite watcher
exhaustion can be avoided with `DOTNET_USE_POLLING_FILE_WATCHER=1`.
