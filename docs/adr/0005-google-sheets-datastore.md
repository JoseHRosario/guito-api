# Google Sheets remains the data store

> **Status amendment — #150:** Superseded for persistence by ADR-0015 and issue #150. Expense and Category authority is solely Postgres; Sheets is an Apps Script/API-fed presentation consumer. The original no-migration rationale below is historical, not current architecture. See [the approval-gated retirement/live-acceptance runbook](../sheets-persistence-retirement.md).

> **Note (ADR 0011)**: the datastore access now sits behind per-aggregate repository interfaces (`IExpenseRepository`, `ICategoryRepository`) with a single Sheets implementation each; Postgres is the anticipated future store behind those interfaces. This ADR's "no migration" statement still governs: the migration itself remains undecided and out of scope.

The API performs CRUD on a Google Spreadsheet (via a service account) and this stays: no migration to DynamoDB. There is no data migration needed for the revival, Sheets costs nothing, and the dataset is personal-scale.

**Consequences**: a future migration to DynamoDB is allowed if Sheets becomes the bottleneck (rate limits, query patterns), but is deliberately out of scope now. No missing features are in scope for the revival phase; features are discussed once the system is up and running.
