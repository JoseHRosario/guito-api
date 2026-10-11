# ADR-0016: Operator-run historical Expense SQL and frozen provenance

Status: Completed and retired at José's direction after verified one-off staging and production imports. The decision below records the historical execution approach, not a supported reusable importer.
Issue: #129 / parent #126. Extends ADR-0015; category authority and smoke stay unchanged.

## Decision

At José's direction, replace the C# importer, application interfaces and reusable
Actions import workflow with a small frozen-snapshot converter and plain SQL script.
No import endpoint, scheduled job, automatic import runner or new IAM grant.

Capture the complete configured B:H grid read-only, retaining original formatted
and unformatted tokens. Declare EUR/date/decimal conventions and compare repeated
reads while source writes are paused. SHA-256 of the exact archive bytes identifies
its batch; `(snapshot_id, source_row)` is durable provenance, never Expense identity
or content deduplication. Preserve legitimate identical purchases separately.

The generated database-specific SQL runs in one PostgreSQL transaction in José's
DBeaver connection. It checks the target DB, source validity, category mirror and
persisted snapshot head, locks that source, and atomically inserts each Expense and
its provenance. Retries of that exact script skip existing links; failed scripts
rollback. Provenance migration 007 and the explicitly requested one-time category
mirror seed 008 (`Startup`, `Wellness`) are resumable and included in the SQL.

José explicitly approved per-row exclusions for historical staging `Integration
Tests` records; preserve an archive-scoped approver/reason manifest. Never apply that
exclusion to production or invent other exclusions. Categories remain Sheets-owned;
adding exact historical mirror labels is not a category-authority migration.

Preserve exact signed/zero decimal values per ADR-0015. Stored numeric tokens are
authoritative even when currency formatting hides precision; retain both views and
a nonblocking display-difference note. Never round €0.011 to its €0.01 display.
Preserve description/category/creator strings unchanged. Occurrence is original
Lisbon midnight; `create_date` is actual import/write time and `update_date` remains
null. Do not fabricate historical audit times.

Changed snapshots stop at the persisted source head. Reconciliation is an explicit,
separately reviewed operator SQL operation against both archives, preserving IDs and
accounting for every predecessor row. This converter does not automate reconciliations,
financial corrections or deletes. Do not erase provenance to make a new batch pass.

The comparison entry point uses SELECT-only Data API requests for either environment.
It reports every row, raw evidence, classification, exact values/totals, attribution,
IDs, occurrence/audit dates and exceptions. Cutover readiness is blocked until all
relevant rows have parity or explicit approved exclusion. Unrelated existing Expenses
are inventoried separately. Successful import is not a production authority switch.

## Verification

Hermetic entry tests use frozen source files and fake database transport. Read-only
PostgreSQL EXPLAIN validates generated insert syntax; it is not live write acceptance.
José executed both imports and identical reruns; read-only comparisons passed with
zero exceptions and stable Expense IDs/audit dates. José then dropped the three
auxiliary tables in both databases; read-only checks confirmed absence and retained
Expenses. Scripts, temporary test/workflow and provenance migration 007 are retired
so deployments cannot recreate them. Private snapshots and reports retain evidence.
Live interrupted-write recovery and changed-snapshot reconciliation were not exercised;
José explicitly closed the scope as a completed one-off import. See the completion
record in docs/historical-expense-import.md. Cutover remains separately gated by #132.
