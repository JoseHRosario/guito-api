# Historical Expense import (#129): completed and retired

José executed the one-off SQL import and identical-script reruns in staging and
production. Read-only per-record comparisons against frozen sources, then against
saved IDs/audit timestamps, returned zero exceptions in both environments.
At the time of this import evidence, no production storage-authority switch occurred;
that was separate under #132. This record does not describe current live authority.
Issue #150 establishes sole-Postgres Expense/Category architecture; acceptance and
credential retirement remain separately gated in [the runbook](sheets-persistence-retirement.md).

## Verified evidence

| Environment | Source Expenses | Imported | Approved exclusions | Blanks | Imported/persisted EUR total |
|---|---:|---:|---:|---:|---:|
| staging | 43 | 27 | 16 | 940 | 9011.171 |
| production | 98 | 98 | 0 | 932 | 5063.20 |

Staging source total was EUR 9011.331; the difference is the explicitly approved
historical Integration Tests exclusions. Row 41 retained exact 0.011 despite its
0.01 currency display. Production Startup/Wellness labels were preserved.

Comparisons verified original raw cells, exact amounts, description/category/creator,
original Europe/Lisbon midnight occurrence, actual creation timestamps and null update
timestamps. Identical reruns retained IDs and audit dates without duplicate imports.
Interrupted-write recovery and changed-snapshot reconciliation were not live exercised;
changed snapshots were blocked, not silently treated as retries. José confirmed that
this was a one-off operation, not a reusable importer.

## Retirement

José dropped expense_import_rows, expense_import_snapshots and expense_import_sources
in both environments after the successful rerun comparisons. Read-only checks confirmed
all absent and Expenses retained: staging 29 (27 imported + 2 pre-existing), production 98.
No further database cleanup is required.

Capture/import scripts, their temporary tests/workflow and provenance migration 007
are retired from the final branch. Future deploys cannot recreate these tables.
Number 007 is retired, not reused; existing ledger entries need not be removed.
Migration 008 remains an idempotent Startup/Wellness mirror seed, not a history import.
It neither creates auxiliary tables nor changes category authority.

Private frozen snapshots, per-row exclusions, generated SQL, full comparison reports
and verified baseline reports are retained under the protected operator evidence
folder. Never commit financial rows or credentials. Historical commits/downloaded SQL
are evidence only: do not execute again after removal of duplicate protection.

Final source drift, if any, belongs to #132's cutover checks; this completion is not
permission to overwrite Expenses, repeat imports or silently deduplicate new sources.
