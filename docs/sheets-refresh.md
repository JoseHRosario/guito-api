# Private Sheets Expense reporting loader (#131)

## Security and scope

`Code.js` and `appsscript.json` in `scripts/sheets-refresh/` are a **standalone,
private Apps Script project**, not spreadsheet-bound code and not a web app or
add-on. Do not deploy a web endpoint. Only the trusted installer and explicitly
trusted project editors may modify it: their code executes with the installer's
Google authority. Spreadsheet edit access must not grant script edit access.
Keep the project private for the single-owner rollout. If another spreadsheet
editor must activate the standalone trigger, Google's documented restrictions
require at least script **view** access, never edit access. Do not broaden project
sharing without review. Viewer/commenter spreadsheet opens are unsupported.

Guito requests mint `ScriptApp.getIdentityToken()` at execution time and send
that short-lived ID token in **both** `Authorization: Bearer <ID token>` and
`x-google-idtoken`. No API key, OAuth refresh token, service-account key or
persisted ID/access token is used. Google Sheets REST requests separately use
`ScriptApp.getOAuthToken()`; that access token is never sent to Guito. Redirects
are disabled on both transports. The installer must review the configured HTTPS
Guito URL; a trusted project editor could otherwise redirect identity-token use.
Never add token logging, provider-response logging, token cells, caches, properties
or repository fixtures containing real credentials. Discovery logs only public
`aud`, `sub`, and `email_verified`, never the whole JWT, email or financial data.

The dedicated reporting credential authorizes only **GET /Expense/history**,
not human-login audience reuse or an agent-key fallback. The API and edge
validator must verify Google's signature, issuer, expiry, exact audience,
installer subject and `email_verified: true`, with deny-closed configuration:

| Layer | Public pinned claim configuration |
| --- | --- |
| API | `AppConfiguration:SheetsReportingAuthentication:Audience` and `:Subject` (environment variables use `__` separators) |
| Authorizer | `SHEETS_REPORTING_GOOGLE_AUDIENCE` and `SHEETS_REPORTING_GOOGLE_SUBJECT` |

These values must come from the actual chosen standalone project and installer,
not assumed client IDs, script IDs or the UI OAuth client. Moving/recreating the
project or changing installer requires rediscovery and reviewed repinning. Local
JWT decoding is **discovery only**, not authentication verification.

No writes to Guito, no database snapshots, no spreadsheet-to-Guito synchronization.
The loader is a downstream reporting view. Production Postgres cutover remains
separately gated; this loader does not bypass that guard.

## Approval gate and manual setup

**Not installed or live-accepted by this change.** Auth design approval is not
permission to create a Google project, tabs or triggers. There is no signed-in
Apps Script browser session available to the agent. Actual minted audience/subject
and staging reopen acceptance are still pending the owner's login/consent and
explicit approval. Never fabricate those values or treat Node tests as live proof.

After approval, the owner does the following, initially against **staging only**:

1. Preserve the original workbook/archive and review sharing. Create a standalone
   project from `script.google.com`, not **Extensions → Apps Script** in Sheets.
   Keep it private. Copy `Code.js` as `Code.gs`; do not copy `refresh.test.js`.
   Enable manifest visibility in Project Settings and copy `appsscript.json`.
   Use V8 and the explicit scopes `openid`, `userinfo.email`, `external_request`,
   `spreadsheets`, and `script.scriptapp` exactly as supplied. Enable Google Sheets
   API for the associated Cloud project if needed; no advanced Apps Script service
   or additional npm dependencies are required.
2. As the intended installer, manually run `discoverReportingIdentity` in the
   editor and complete Google's consent for every requested scope. Its execution
   log contains only `{aud, sub, email_verified}`. **Do not enable reporting yet.**
   Independently verify a fresh token using Google's maintained verifier on the
   owner's trusted workstation, as described below. Only after that verification
   succeeds, copy the public claims to the reviewed staging configuration in both
   validators; deploy via the normal
   pipeline with `SHEETS_REPORTING_AUTHENTICATION=enabled`,
   `SHEETS_REPORTING_IDENTITY_VERIFIED=true`, and the discovered
   `SHEETS_REPORTING_GOOGLE_AUDIENCE` / `SHEETS_REPORTING_GOOGLE_SUBJECT`.
   The verification flag is an operator assertion, not a substitute for the
   signed-token and restricted-route staging checks below. Deployment defaults
   to `preserve`: new stacks stay disabled, and existing selectors are retained
   only if both gates agree. `disabled` clears reporting authorization in both
   gates; partial, inconsistent or UI-overlapping selectors abort deployment.
   Production enablement additionally requires separately approved
   `ALLOW_PRODUCTION_SHEETS_REPORTING=true`.
   For the supplied staging workflow, configure the four public values above as
   **GitHub Actions variables on the `staging` environment**, then dispatch the
   feature branch's `ci-cd` workflow targeting staging. After successful live
   acceptance, change `SHEETS_REPORTING_AUTHENTICATION` back to `preserve`; the
   workflow passes audience/subject inputs only in `enabled` mode. `disabled`
   revokes reporting at the next staging deployment. No variables are provisioned
   by this change, and production workflow enablement remains a separately
   approved rollout.
   Do not copy the JWT or use an online JWT decoder/token-info URL.
   Confirm `email_verified` is true. No default audience or subject is supplied.
3. Set only these public Script Properties in Project Settings:
   - `SPREADSHEET_ID`: the staging workbook ID (not a full URL).
   - `GUITO_BASE_URL`: the reviewed staging HTTPS base URL, without trailing slash,
     query string, fragment, embedded credentials or bearer material. A stage path
     is supported. Page size is fixed at 100; there is no page-size property.
   Remove any legacy key/token properties. Do not paste credentials into this UI.
4. Explicitly run `installExpenses`. It locks the operation, performs a fully
   authenticated all-page read and validates it **before creating tabs**. It then
   creates or recovers its own metadata-tagged tabs, verifies ownership, publishes
   the complete dataset, and only after successful publication enables an
   installable `refreshExpenses` spreadsheet-open trigger. Auth/fetch/validation
   failure does not initialize tabs or enable a trigger. Inspect both the status
   tab and the project's Triggers panel; `OK` alone does not prove trigger creation
   succeeded. A trigger-creation failure can follow successful publication.
5. Verify exactly one own `refreshExpenses` open trigger for this workbook in the
   Triggers panel. Rerunning installation deduplicates only this user's matching
   project triggers, not unrelated triggers. Google does not expose another
   account's triggers; use a single installer, and have former installers remove
   theirs before transferring ownership.

The script creates **Guito Expenses**, **Guito Previous**, and **Guito Refresh
Status**. Existing title collisions without matching ownership metadata abort;
no pre-existing tab is adopted by title. A partial/duplicated ownership set also
aborts. Stable tab IDs are stored as `DATA_SHEET_ID`, `BACKUP_SHEET_ID`, and
`STATUS_SHEET_ID`; sheet developer metadata `guito-loader=<scriptId>:<role>` is
verified before writes. Renaming a tagged tab is supported; deleting/recreating
it with the same name is not ownership. Initialization recovers the complete
metadata set if a previous create succeeded but property persistence failed.
Do not hand-edit ownership properties or remove developer metadata.

The only additional property is `PENDING_COMMIT_MARKER`: public operation/tab
metadata, not a credential. It prevents blind retries after ambiguous writes.
Properties never contain financial data, email, tokens or secrets.

## Signed-identity prerequisite (before enabling either gate)

Use the owner workstation and an interactive terminal **without session recording**.
Create a private Python virtual environment outside the repository and install
`google-auth[requests]` there. Run the supplied helper with the public discovered
claims, never with a token in command arguments:

```bash
python scripts/sheets-refresh/verify_identity.py --audience '<discovered aud>' --subject '<discovered sub>'
```

In the private Apps Script editor, put a breakpoint immediately after
`ScriptApp.getIdentityToken()` in `discoverReportingIdentity` and **debug** that
function as the intended installer. Copy the fresh local `token` variable into the
helper's hidden prompt. Do not add a token log, save it to a file, put it in shell
history, send it through chat, or use an online decoder. Clear the clipboard after
verification; beware clipboard-history tools. The helper refuses noninteractive
input or an echoing prompt and never writes the token to disk or prints it.

Google's library fetches Google's signing certificates over HTTPS and checks the
signature, issuer, audience and expiry. The helper additionally checks the pinned
installer subject, present email and boolean `email_verified=true`, and prints
only a public verification result. Failure leaves the deployment prerequisite
unsatisfied; do not set `SHEETS_REPORTING_IDENTITY_VERIFIED=true`. This is an
owner-run one-time prerequisite, not part of hermetic CI. It does not replace the
subsequent real checks through API Gateway and Guito, including rejected routes.

## Dataset, status and atomic publication

The dedicated stable data tab has row-one headers:

`id, amount, currency, bookingDate, category, description, creatorEmail, creationDate, updateDate`

Every cell is an explicit Sheets `userEnteredValue.stringValue`, including IDs,
exact amount text and timestamps. Amounts never pass through JavaScript Number;
zero and negative amounts are valid. Formula prefixes such as `=`, `+`, `-` and
`@` remain literal text, not formulas. Nullable DTO fields become empty strings;
missing/invalid fields are rejected. Occurrence ordering compares UTC whole
seconds and fractional text independently, retaining microseconds and handling
explicit offsets without Date's sub-millisecond truncation. Calendar dates and
time/offset bounds are validated. The loader does not reinterpret timestamps as
local calendar days or manufacture missing audit timestamps.

Complete fetching and validation precede replacement: exact page size/index,
fixed total across all pages, expected row counts and `hasMore`, valid DTOs,
unique integer IDs, and booking instant/ID descending ordering. See the
[live page contract](expense-history-export.md). These checks detect many kinds
of drift, **not all same-count concurrent changes**. Pages are live offset reads;
this is not a guaranteed point-in-time snapshot. Coordinate Guito writes during
an acceptance/archive capture if stable source parity is required.

A script-wide lock covers preparation, fetching, validation, publication and
installation. A concurrent loser returns without network, property or status
writes. This lock does not prevent manual workbook edits or another script's
writes. Keep loader-owned tabs read-only by convention/protection and avoid
collaborator writes during refresh/recovery. Spreadsheet protections supplement,
not replace, private-project access control.

One `spreadsheets.batchUpdate` grows grids if needed, clears stale backup values,
copies the **previous** dataset to Guito Previous, replaces A:I in Guito Expenses
(including stale tail removal), and sets all four status cells together:

- A1: `OK` or a sanitized failure reason.
- B1: last successful refresh in UTC ISO8601 (not the time of a failed attempt).
- C1: successful expense count.
- D1: public UUID commit marker.

Google validates the whole batch and applies its requests atomically; a rejected
batch preserves data, backup and last success. This does not guarantee isolation
from collaborators. No sequential clear-then-write assumption is used. Failure
status is a separate best-effort A1-only write; if that write fails, the returned
outcome is still sanitized and last success is untouched. Quota exhaustion or a
hard Apps Script execution kill may leave the old status; always check its time.
The initial backup is empty; it becomes useful after a subsequent successful
refresh. It retains only one prior dataset, not an immutable audit archive.

Original tabs and the original archive are never renamed, cleared or rewritten.
The owner explicitly repoints their reporting formulas to the new data tab after
acceptance. Put formulas/derived amounts on separate owner-controlled tabs:
formulas in loader A:I will be overwritten. Exact decimal text is the source of
truth; converting it with `VALUE`/numeric formulas may lose precision.

## Ambiguous commit and recovery

Before publication the script persists only the public pending marker. On a
network exception, timeout or server error it reads status D1: matching marker
confirms the entire batch committed and avoids a second write. If marker readback
fails or does not match, the outcome is **unknown**, not “previous data retained”.
No failure status write overwrites a possibly successful status. Subsequent
refreshes stop before fetching/writing while that pending marker is unresolved;
a later matching readback clears it and permits normal refresh. There is no
automatic blind retry or automatic rollback.

If an outcome remains unknown, disable this installer's open trigger, wait for
executions/provider processing to finish, and inspect status marker, dataset and
backup together. A mismatching immediate read is not proof the server did not
commit later. Do not clear `PENDING_COMMIT_MARKER` merely to retry. After the owner
has established the final outcome, they may clear only that public marker in
Project Settings and run a controlled refresh. No distributed transaction exists
between Script Properties and Google Sheets.

For an intentional dataset rollback, first disable the open trigger, resolve any
unknown commit, and inspect/archive the prior dataset in Guito Previous. Run
`restorePreviousExpenses` as the trusted owner. It uses the same lock and verified
ownership, atomically clears/replaces data from the backup and publishes a new
marker with `RESTORED BACKUP`. It clears last-success/count rather than claiming a
fresh Guito read. The backup itself is not overwritten. Restoring the initially
empty backup empties the view: inspect it first. This is **Sheets-only recovery**;
it never writes back to Guito. Reenable via a controlled successful installation
only when current-source publication is again wanted.

## Acceptance checklist (owner session required)

Record deployed commit and public discovered claims, without tokens or financial
payloads in the PR. No items below have been claimed as executed here:

- Verified signed staging reporting identity is accepted on GET history; unrelated
  audience/subject and missing/unverified identity are rejected. The reporting
  credential cannot call create/latest/category/AI/smoke or other routes. Existing
  human and agent auth still work independently.
- Grant consent, run initial install, compare all rows/count/order/exact amounts,
  creator attribution, null fields and timestamps with a coordinated live source.
- Original archive/formulas remain intact. Verify loader data tab ID stays stable
  across refreshes; owner-controlled formula changes are explicit.
- Close and reopen staging workbook as the installer with **edit access**. Verify
  a fresh `OK`/B1 and the full dataset, not just trigger existence. Verify another
  editor only if separately approved/script view access is granted. Do not promise
  viewer/commenter reopen or opens caused by scripts/API calls.
- Exercise overlapping opens/manual refresh, auth failure, incomplete/drifting
  pages, rejected batch, ambiguous outcome and backup restore without destroying
  the archive. Remove consent/trigger when uninstalling; retain user-owned tabs
  unless the owner explicitly approves their deletion.

Installable triggers run with the **creator's authority**, not the opener's
identity. Opening a sheet does not mint the opener's token. Trigger authorization
can be revoked and quota limits apply; re-consent is manual, never a stored
refresh-token workaround.

## Hermetic checks and limits

```bash
node --check scripts/sheets-refresh/Code.js
node --test scripts/sheets-refresh/refresh.test.js
```

CI runs these with Node 22 and built-in `node:test`/`vm`; no packages, credentials,
network, Google account or AWS permissions. Tests call the public refresh entry
point with a fake API/sheet seam and with mocked Apps Script APIs exercising the
actual HTTP/runtime adapter and its serialized Sheets batch, not only a canned
successful commit. They cover exact text/formula defense, all-page validation,
drift/duplicates/microsecond ordering/calendar checks, empty data, separate token
transports, ownership, install ordering/deduplication, concurrency, rejection,
failure-status failure, marker readback/unknown blocking, restore and redacted
claim discovery. Synthetic `.invalid` identities are test-only, not discovered
installer claims.

Mocks do not prove real Google scopes/consent, minted audience/subject, trigger
activation, Sheets protections/provider execution, network-size/time limits,
backend configuration or a frozen multi-page source. One full history is retained
in memory and published in a single batch: large histories/descriptions may hit
Apps Script runtime/memory/URL Fetch or Sheets request limits. There is no split
publication fallback, because that would weaken all-or-nothing behavior.

## Official references

- [ScriptApp identity and OAuth token methods](https://developers.google.com/apps-script/reference/script/script-app): explicit `openid`, effective-user ID token, separate Google access token.
- [Installable triggers](https://developers.google.com/apps-script/guides/triggers/installable): standalone spreadsheet targeting, edit-mode opens, creator authority, script-view requirement and per-user trigger visibility.
- [Sheets batchUpdate](https://developers.google.com/workspace/sheets/api/reference/rest/v4/spreadsheets/batchUpdate): whole-request validation and atomic application, with collaborator caveat.
- [updateCells request](https://developers.google.com/workspace/sheets/api/reference/rest/v4/spreadsheets/request#UpdateCellsRequest): a range clears fields not covered by supplied rows.
