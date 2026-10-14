# Sheets persistence retirement (#150)

## Removal scope

Postgres through RDS Data API is the sole Expense and Category authority in every
environment. Category listing and AI extraction reuse the existing Postgres reader,
alphabetical names and name-only response. Existing Int64 IDs/FKs, unknown-category
rejection, exact Expense values, revisions and transactions are unchanged. No schema
changes, category CRUD, reseeding or automatic creation are included.

API-side Sheets repositories/client, store selector, persistence configuration and
service-account loading are removed. The legacy `X-Sheet-Scope` header is ignored;
it selects no alternate datastore. `/Smoke/{id}` is removed (404), never personal
cleanup. Supported Expense routes retain their normal authentication and validation.

Deployed business tests perform reads only. The runner retains established auth
coverage, including POSTs with invalid authorization codes and garbage logout tokens;
these are not valid financial writes. Replacement deployed write acceptance is
**deferred**: this removal adds no round-trip option, isolation gates, mutation guard
framework or replacement write machinery. Staging naming alone authorizes no writes.

## Retained consumers and owner gates

Preserve human Google login/token exchange/logout, agent authentication,
`Google.Apis.Auth`, OpenRouter/bank/database secrets and runtime secret providers.
Apps Script remains an API-fed presentation consumer: preserve its reporting identity,
installer OAuth, Sheets API access, spreadsheet-write permissions and archives. There
is no reverse synchronization. See [the existing loader runbook](sheets-refresh.md).

Credential inventory/retirement is separate and owner-gated. Establish exclusive
ownership and remaining consumers read-only before proposing exact API-side key,
secret-field or permission removals. Preserve unrelated fields in shared payloads
and all retained auth/reporting/database credentials. No credential mutation is
performed or authorized by this code removal. Never modify `~/.aws` or log secrets.
Approved AWS operations use `MinervaAIAgent`, not the owner's identity.

## Evidence boundaries

Record reviewed source revision, hermetic tests, CI/deployment, production read-only
acceptance, retained reporting/auth checks and credential retirement separately.
Prior staging deployment/read-only success at `51520d703415cdeba2209e196927ca092cb9e2ad`
does not verify subsequent edits, replacement write acceptance or credential removal.
No live verification or credential retirement is claimed for this simplification.
Routine deployment/migrations remain pipeline-owned; production rollout and live
reporting refresh require their respective owner approvals.

Historical ADRs, migration SQL, completed #129 import and #132 cutover evidence
remain history, not runnable Sheets persistence/smoke instructions. Supported
reporting/auth references remain intentional. Broader documentation restructuring
belongs to #149; this change does not absorb it or future reconciliation work.
