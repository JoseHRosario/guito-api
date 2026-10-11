# Historical Expense import (#129): operator-run SQL

José runs the imports. The agent prepares frozen evidence and compares staging and
production read-only. **No import workflow, C# importer, IAM grant or automatic
production switch.** API remains version 0.14.0; normal production Expense authority
remains Sheets until a separately approved cutover.

## Files

- `scripts/capture-history.py`: read-only Google capture through MinervaAIAgent/SSM.
- `scripts/import-history.py convert`: frozen JSON -> `import.sql` plus source rows.
- `scripts/import-history.sql`: atomic Expense + provenance insert template.
- `scripts/import-history.py compare`: SELECT-only Data API parity report.
- Migrations 007 (provenance) and 008 (exact historical category mirror labels
  `Startup`, `Wellness`) are included in each generated SQL file, resumably.

Generated files contain personal financial data: keep them private, never commit them
or upload plaintext to public GitHub artifacts. Output directories/files use 0700/0600.
Retain the original snapshot, generated SQL, approvals, and reports together.

## Capture and generate

Keep API/manual source-sheet writes paused during a final capture. Repeated reads
catch observed drift but are not revision-atomic. Capture uses the entire allocated
B:H grid from the configured anchor, including trailing blank rows, never latest-N.
Date conventions are explicit and checked against underlying serial dates.

```bash
# Only capture needs requests + cryptography; conversion/comparison use stdlib.
python3 scripts/capture-history.py staging /private/staging-frozen.json \
  --date-format M/d/yyyy --decimal-separator .
python3 scripts/import-history.py convert /private/staging-frozen.json \
  /private/staging --database guito_staging --approvals /private/staging-approvals.json

python3 scripts/capture-history.py prod /private/prod-frozen.json \
  --date-format M/d/yyyy --decimal-separator .
python3 scripts/import-history.py convert /private/prod-frozen.json \
  /private/prod --database guito_prod
```

Do not recapture an interrupted import. SHA-256 of the exact archive bytes identifies
its batch; row position within that archive identifies its source record. Identical
purchases in different rows remain separate Expenses. Reformatting the JSON also
changes its identity. Preserve the exact original bytes for retries.

Exclusion approvals name that exact file SHA-256, source row, approver and reason:

```json
{"snapshotId":"<archive SHA-256>","exclusions":{"13":{"approvedBy":"José Rosário","reason":"Explicitly approved historical staging test exclusion"}}}
```

José approved excluding historical staging `Integration Tests` records; production
records are not excluded by that decision. The generated staging approval file
records each exact row separately. Retain/reuse the same approvals on every retry.

## Run in DBeaver (José)

1. Select **guito_staging**, open its generated `import.sql`, and execute the **whole
   script** (not a highlighted row or an individual statement). DBeaver's PostgreSQL
   connection is the execution path; this file is not an RDS Data API multi-statement
   request. SQL starts a transaction and ends with COMMIT.
2. If any statement fails, stop and execute ROLLBACK. Do not bypass guards. A wrong
   database, malformed unapproved source, missing category mirror, or changed snapshot
   aborts the transaction. Expense/provenance changes commit together, not partially.
3. Ask the agent to compare staging against its archived source. Run the **same file**
   again to demonstrate no new Expenses, IDs or audit dates on a retry, then compare.
4. Only after reviewing staging, repeat the same process with the production-specific
   file on **guito_prod**. José executes it; the agent remains read-only. This does not
   switch production reads/writes or authorize any source-sheet correction.

The script only inserts source Expenses/provenance and the approved category mirror
seeds. It does not delete or alter existing Expenses, normalize descriptions/creators,
content-deduplicate purchases, or reset identity sequences/migration ledgers. Existing
normal Expenses are not automatically considered imported or deduplicated.

A changed snapshot is blocked by the persisted source head. Do not delete that guard
or pretend the new file is a retry. First compare both archives and approve explicit
reconciliation SQL covering every predecessor record, reusing stable Expense IDs for
unchanged/moved rows and approving genuinely new rows or exclusions. This tool does
**not** automate changed-snapshot reconciliation or financial edits/deletes.

## Read-only verification (agent)

```bash
python3 scripts/import-history.py compare /private/staging-frozen.json \
  /private/staging --database guito_staging --approvals /private/staging-approvals.json
python3 scripts/import-history.py compare /private/prod-frozen.json \
  /private/prod --database guito_prod
```

Report each source row, classification, original formatted/underlying cells, expected
and persisted values, stable ID, occurrence and audit timestamps, exact totals and
exceptions. Currency display may hide precision: numeric stored values are authoritative
without rounding; display differences are audit notes (approved €0.011 vs €0.01 case).
Occurrence is the original day at midnight Europe/Lisbon, including DST. `create_date`
is the actual SQL write time; `update_date` stays null. No historical audit times are
invented. Unknown currency, malformed grouping/date/precision and missing attribution
are exceptions. Readiness stays blocked until every row has verified parity or explicit
approved exclusion. Before import, comparison is expected to be blocked—not evidence
of successful import. Full-source bounds and unrelated existing DB rows are checked
separately; readiness does not authorize a production switch.

Safe pre-switch abort: ROLLBACK or keep current Sheets authority. After Postgres accepts
authoritative writes, reverting to stale Sheets needs explicit reconciliation, not a
configuration flip. Never modify `~/.aws`; José handles `aws login` manually.

## Verification limits

Hermetic tests cover the converter/comparison entry point with frozen Sheets JSON and
fake database transport. PostgreSQL EXPLAIN (without ANALYZE) verifies insert syntax
read-only. Actual execution, constraints under writes, rollback and idempotent rerun
acceptance remain pending José's staging/production execution; do not report them passed.
