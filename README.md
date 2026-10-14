# Guito API

Personal expense-tracking API consumed by the Angular [guito-ui](https://github.com/JoseHRosario/guito-ui)
and CLI/AI agents. Expenses can be entered manually or proposed by AI; bank consent,
transaction retrieval/sync and category suggestions use Enable Banking behind provider
interfaces. Proposed Bank-review work is not the same as merged matching capability.

## Architecture

.NET 10 (`src/guito-api`) runs on AWS Lambda behind API Gateway HTTP API. A separate
`src/guito-api-authorizer` REQUEST authorizer dispatches independent agent-key and
Google ID-token validation; checks repeat inside the API. Deployed agent requests
send the same key in raw `Authorization` and `X-Api-Key`; human requests send the same
ID token in Bearer `Authorization` and `x-google-idtoken`. Reporting has a distinct,
GET-history-only Google identity. See [authentication](docs/authentication.md).

Controllers delegate to per-operation services. Repository interfaces exchange
internal Model values; technology adapters under Infrastructure own Sheets, Postgres
Data API, OpenRouter, SSM and bank transports. HTTP DTOs are separate from internal
models. [Conventions](docs/CONVENTIONS.md) and [ADR-0011](docs/adr/0011-repository-pattern.md)
define the boundaries, including the merged #146 Expense aggregate extension.

`AppConfiguration:ExpenseStore` selects one personal read/write authority: Sheets
(default configuration) or Postgres via the existing Aurora Data API infrastructure.
It works in every environment; there are no dual writes. `/Category` remains
Sheets-backed with a seeded Postgres mirror. Dedicated smoke CRUD always uses Sheets.
Routine staging Actions selects Postgres; production Actions omits the selector and
preserves installed authority. A committed Sheets default is not deployed-state proof.

The [dated cutover record](docs/expense-cutover.md) records José's #132 operational
acceptance and its unperformed rehearsals, reconciled from newer #133 documentation.
It is not a fresh live audit or permission to change authority. Reverify the served
build/config before any operation; returning to stale Sheets after Postgres writes
requires approved reconciliation, not a config rollback.

## Getting started

Prerequisite: .NET 10 SDK. From an owned issue worktree:

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"   # if needed
dotnet restore guito-api.slnx
dotnet build guito-api.slnx
dotnet test guito-api.slnx --filter "Category!=Integration"
dotnet run --project src/guito-api   # local Kestrel, not Lambda emulation
```

Build and hermetic tests require no secrets. Real local requests need owner-provided,
gitignored local configuration/credential files and the appropriate dev spreadsheet
shared with its service account. Relative file paths resolve from `src/guito-api`.
Inspect committed appsettings and local overrides; never target production from local
dev. Configuration uses AppConfiguration options, environment JSON and environment
variables. Deployed application secrets use SSM SecureString; database credentials
remain Secrets Manager secrets for Data API ([ADR-0008](docs/adr/0008-per-environment-secrets-payload.md)).
Never print/commit credentials or modify `~/.aws`.

The solution includes `tst/` hermetic tests and `sit/` live suites: bare `dotnet test`
is not safe shorthand for the unit gate. Separately approved
`scripts/run-deployed-tests.sh staging` resolves AWS credentials and runs auth plus
isolated Sheets smoke round-trips. It does not prove personal Postgres persistence.
See [operations](docs/operations.md) for approval/provenance and test distinctions.

## Expense contracts and consumers

- [GLOSSARY](GLOSSARY.md) defines numeric Expense identity, exact amounts and Lisbon
  occurrence/audit vocabulary. [ADR-0015](docs/adr/0015-postgres-expenses-and-lisbon-occurrence.md)
  specifies create/latest: one `bookingDate`, exact-decimal amounts, immutable Int32
  IDs and frozen date-only Sheets compatibility. Amount input must fit .NET decimal
  exactly (96-bit coefficient, scale 0–28 after removing trailing zeros); no rounding,
  underflow or overflow. Intraday creates against Sheets return 409.
- [Detail/edit/delete](docs/expense-edit-delete-contract.md) uses ETag/If-Match revisions,
  exact stored amount text and atomic personal mutations; [delete security](docs/expense-delete-security.md)
  confines scope and uncertain-outcome recovery. `/Smoke/{id}` is unrelated Sheets cleanup.
- [Category/month query](docs/expense-category-month.md) returns all exact-category
  Expenses in a Lisbon booking month without silent partial success; Postgres only.
- [History](docs/expense-history-export.md) is live paginated Postgres data, not a frozen
  export. [Private Apps Script refresh](docs/sheets-refresh.md) reads complete history
  into dedicated reporting tabs with a short-lived identity, no stored API key or
  refresh token. Existing archive/formula tabs are not replaced; installation/reopen
  and production reporting require separate approval.

`POST /AI/extract` is implemented through OpenRouter, not a universal 501 stub.
Jev gates expense notes before a chat model extracts date/amount/merchant, then picks
from real categories. Models are configuration-selected, not inferred deployed choices.
Extraction normalizes a positive nonzero amount; this is distinct from create/update
accepting exact positive, zero or negative values. Its `bookingDate` is day-precision
midnight, not a known occurrence instant; submit the day as `yyyy-MM-dd`. Response
amounts use exact decimal text; preserve them without JavaScript Number conversion.

## Deployment and contributing

GitHub Actions packages/deploys the API and authorizer through OIDC. Routine migrations
are applied by that pipeline. Feature pushes can replace shared staging; master pushes
can deploy production. No deployment is authorized by this documentation task. Review
[operations](docs/operations.md) before release, IAM maintenance or live checks.

Each issue uses a dedicated worktree, branch and owner-reviewed PR. Root
[AGENTS](AGENTS.md) owns safety/worktree/attribution rules; [tracker](docs/agents/issue-tracker.md)
owns templates and cross-repo closure. #149 component PRs reference rather than
close the cross-repo issue; no runtime version change is authorized for this docs work.

## Documentation map

[Domain discovery](docs/agents/domain.md) routes relevant contracts/ADRs; read only the
applicable decisions before architectural changes. [Revival plan](docs/guito-revival.md)
retains the dated original MVP decisions, not an undated production status page.
[Historical import](docs/historical-expense-import.md) retains accepted provenance;
retired import tooling is not a new operational prerequisite.
