# ADR-0016: Frozen historical Expense import and durable row provenance

Status: Accepted for staging implementation; no production import or cutover authorization.
Issue: #129, parent #126. Extends ADR-0015; category authority and smoke remain unchanged.

## Decision

A separate operator tool calls `IHistoricalExpenseImportService.ImportAsync` with a
frozen bounded snapshot. It is not an HTTP endpoint, a latest-list export, a deployed
background job, or a second ongoing write authority. Source Sheets access uses a
read-only OAuth scope and the real Google HTTP client. Read B:H from the configured
first row through an explicitly verified final grid row; account for trailing blanks.
Capture both formatted values (day/currency convention) and unformatted values
(exact underlying numeric token). Read each twice and refuse changing source content.
The operator must pause edits; repeated reads do not provide Google revision isolation.
Parse streams as JSON tokens, not Google `object` cells or floating-point intermediates.

SHA-256 identifies the snapshot, including source, bounds, conventions, both views and
all row positions, but excluding capture time. It never identifies or deduplicates an
Expense. The archive is written atomically without overwrite before live import.
Durable DB metadata and every raw row are registered transactionally in resumable
migration 007 tables. Formula-only Year/Month rows are NonExpense, wholly empty rows
Blank; any business cell is relevant, including hidden underlying values. Malformed
rows have raw evidence and actionable exceptions. Exact numeric amounts accept
negative/zero values per the final PR #137 contract; unsupported precision is rejected.
Description, category case/spacing and creator text are not normalized. Categories
must have an exact mirror match. Booking day resolves to original Lisbon midnight;
`create_date` is the actual database write time and `update_date` stays null.

Each Expense insert and its `(snapshot_id, source_row)` provenance link commit in one
ambient Data API unit of work. A transaction advisory lock serializes source imports,
including concurrent processes and head supersession. UNIQUE snapshot/Expense mapping
prevents one predecessor Expense being reused twice. Source row identity is scoped to
that snapshot, never a permanent Expense identity. A lost commit acknowledgement
produces a blocked report; rerun the same archive and recover the committed link.
Successful earlier rows remain committed when another row fails. Never reset IDs,
delete personal records, reapply source writes, or roll back successful import rows.

Changed snapshots block before any insert until an explicit approved reconciliation
accounts for every new relevant row: reuse an exactly matching imported predecessor
once, explicitly approve a new purchase, or explicitly approve an exclusion. Every
previous relevant row must already be imported or excluded and accounted for. Previously
excluded rows can be retained as archived exclusions. Removal/correction of already
imported Expenses is deliberately blocked: this importer cannot authorize financial
edits or deletes. Restore/reuse those rows or obtain a separately approved correction.
No unexplained residual Expense may be hidden behind aggregate equality. Reconciliation
plans are durable and immutable for retries; exclusions are durable approver/reason
records. No content-only deduplication or silent repair occurs.

Reports contain source and actual record values, IDs, audit times, per-record parity,
row accounting, exact totals (arbitrary-size aggregate accumulator), exclusions and
exceptions. Readiness requires every relevant row imported with parity or an explicit
approved exclusion. It applies to this verified bounded snapshot only, not to automatic
production switch authorization. Staging pre-existing Expenses need separate accounting.

## Operations and verification

The operator runner refuses production source/DB credentials and local live import.
A manual GitHub Actions workflow uses the existing staging deployment role, staging
concurrency lane and staging environment approval. Schema application stays with the
existing deployment pipeline. Evidence artifacts are encrypted and HMAC-authenticated
using a separate Actions environment secret; no plaintext financial report is published.
Retries download the prior protected snapshot rather than capturing live Sheets again.

Hermetic tests exercise this entry point with the actual Sheets HTTP and Data API
repository/unit-of-work composition. Tests cannot establish PostgreSQL DDL correctness,
real FK/unique enforcement or actual persistence. Separate staging rehearsal and
retained encrypted evidence remain acceptance prerequisites. See
[the runbook](../historical-expense-import.md).
