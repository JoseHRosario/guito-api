# Google Sheets remains the data store

The API performs CRUD on a Google Spreadsheet (via a service account) and this stays: no migration to DynamoDB. There is no data migration needed for the revival, Sheets costs nothing, and the dataset is personal-scale.

**Consequences**: a future migration to DynamoDB is allowed if Sheets becomes the bottleneck (rate limits, query patterns), but is deliberately out of scope now. No missing features are in scope for the revival phase; features are discussed once the system is up and running.
