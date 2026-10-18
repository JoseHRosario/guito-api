# Reconciliation staging acceptance (#136)

## Status and authority

**Staging acceptance with explicit owner dispositions; #136 closes through the evidence PR on owner merge. Parent #126 remains open.**
This is a repeatable manual procedure and an evidence index, not a deployed write
runner, new CI project, retry framework, production approval or cleanup mechanism.
Routine tests stay hermetic. Read the current [manual Match contract](bank-manual-matching-contract.md),
[create-and-Match contract](bank-create-and-match-contract.md),
[history contract](expense-history-export.md) and [reporting runbook](sheets-refresh.md).

The later owner decisions in those contracts supersede the original #126 prose:

- PostgreSQL is the sole Expense and Category authority. Sheets is downstream
  presentation, not a fallback. `X-Sheet-Scope` does not isolate writes and `/Smoke`
  is removed. Never use either for fixture cleanup.
- No receipt table, operation ID, successful replay or automatic POST retry. After
  an uncertain Save, read current Bank state. A matched state is not proof of which
  client committed it; an unlinked read cannot exclude an in-flight commit.
- Dedicated real concurrency acceptance is **owner-deferred**, not passed. Ordinary
  one-to-one constraints, stale-tab guards and duplicate-click checks remain.
- Approved presentation removed rule/reason commentary; do not restore it just
  to satisfy older wording. Visual evidence is separate from functional evidence.

## Before every live run

1. Obtain fresh owner permission for the exact database, synthetic record budget,
   retention and writer/deployment hold. The 18 October approval is not standing
   permission for later runs. A database name, aggregate counts or description
   marker cannot establish disposable contents. If any records are personal or
   classification is unknown, stop and agree a genuinely isolated target; do not
   seed or reset the shared database.
2. Verify the approved `MinervaAIAgent` identity, account `497087877832` and region
   `eu-west-1`. Never modify `~/.aws`; renewal is José's step. Keep decrypted agent
   credentials only in process memory. No credentials in arguments, files, output
   or evidence. Use the current environment's SSM binding, not a historical key.
3. Read the custom-domain API mapping and Lambda integration, deployed Lambda
   revision/code SHA, packaged staging settings and environment overrides. Bind the
   *actual* API database/secret, not merely local appsettings. Read the served
   `X-Api-Version` before and after; stop on deployment drift. Inspect current
   schema, migration ledger, constraints and credential privileges read-only.
   Staging and production can share an Aurora cluster: pin the database as well.
4. Hold other UI/API writers, Bank syncs and deployments for the agreed window;
   drain outstanding work. This is an owner-coordinated hold, **not** a server-side
   write barrier. Do not invoke `/BankTransaction/sync` on fake account fixtures.
5. Record counts and deterministic fingerprints of all pre-existing Expenses,
   Transactions, accounts and categories before any mutation. Keep payloads and
   credentials out of public logs. A fingerprint is a before/after drift detector,
   not an authorization or isolation boundary.
6. Use one unique run marker and an explicit manifest of returned account,
   Transaction and Expense identities. Stop on ambiguous mutation responses;
   reconcile exact current records before another action. Never rerun a seeding
   script blindly. HTTP clients must reject redirects and cross-origin targets.

## Fixture plan

The completed run retained one fake account, three Transactions and eight Expenses.
Use a fresh marker for an independently approved repeat; IDs below are evidence,
not reusable mutation targets. **A new marker alone does not isolate candidates.**
Before seeding, select a positive exact-cent amount `M` and check read-only that no
existing, unlinked Expense with that amount falls in the inclusive Lisbon day
window `2026-10-22`–`2026-10-28`. For example, using bound `:amount`:

```sql
SELECT count(*) FROM public.expenses e
WHERE e.amount = CAST(:amount AS numeric)
  AND (e.booking_date AT TIME ZONE 'Europe/Lisbon')::date
      BETWEEN DATE '2026-10-22' AND DATE '2026-10-28'
  AND NOT EXISTS (
      SELECT 1 FROM public.bank_transactions b WHERE b.expense_id = e.id);
```

Require zero; otherwise choose a different exact-cent amount and repeat the
read-only check under the writer hold. Record the selected amount in the new run
manifest and use it consistently for both EUR Transactions, the USD negative
Transaction, the six same-amount Expenses and the final create-and-Match. The
amount-negative Expense is `M + 0.001`, calculated using exact decimal arithmetic,
never float. This prevents previously retained fixtures entering the expected
four-candidate set without deleting them or changing production eligibility.
The table below records the **first run's** `M = 12.34`; later runs substitute
their collision-checked amount, preserving the equivalent trailing-zero case.
No production or pre-existing records are touched.

Seed a new account and exactly three Transactions through a bounded Data API
transaction, obtaining generated IDs with `RETURNING id`. The account has its run
marker as `uid`, `session_id` and `name`, `EUR`, `PT`, `ACCEPTANCE ONLY` as ASPSP name,
`EXPIRED` consent and no IBAN. It is not a live bank connection. Transactions use
that exact account, independent unique sync keys, booking day `2026-10-25`, positive
`12.34`, `DBIT`, `BOOK`, `needsReview`, and no Expense link. A and B use EUR; C uses
USD solely to verify currency exclusion. Do not call any bank provider.

Create the following seven Expenses via authenticated `POST /Expense`. Resolve an
existing category without creating/changing categories. Include the marker in each
description; select A's description so `near` ranks above unrelated text.

| Suffix | `bookingDate` | Exact input amount | Expected candidate for A/B |
| --- | --- | --- | --- |
| near | `2026-10-25T10:15:30.123456+00:00` | `12.3400` | Yes |
| minus3 | `2026-10-22T23:30:00+01:00` | `12.34` | Yes |
| plus3 | `2026-10-28T00:15:00+00:00` | `12.34` | Yes |
| unrelated | `2026-10-25T11:00:00+00:00` | `12.34` | Yes; description is not an eligibility gate |
| minus4 | `2026-10-21T23:30:00+01:00` | `12.34` | No |
| plus4 | `2026-10-29T00:15:00+00:00` | `12.34` | No |
| different-amount | `2026-10-25T12:00:00+00:00` | `12.341` | No |

These dates straddle the Europe/Lisbon DST transition. Seed data alone does not
establish candidate eligibility: assert exact returned identities, not just counts.
For the eighth Expense use `2026-10-25T13:14:15.123456+00:00`, `12.3400`, EUR, the
same existing category and a new marker-prefixed description in create-and-Match.
Request amount strings remain strings throughout; never compare through float.

## HTTP journey and independent readbacks

Use the established agent headers (`Authorization: <key>` and `X-Api-Key: <key>`)
in memory. Human-route verification uses a real fresh Google ID token separately;
agent success is not human-auth acceptance. All mutation routes below operate only
on manifest-owned IDs. Read back exact targets after successful mutations.

1. GET `/Expense/{id}` for each created Expense. Assert returned identity, exact
   numeric value, supplied Lisbon occurrence and initially null `updateDate`.
2. GET `/BankTransaction` without a filter; assert the three new unlinked
   `needsReview` rows appear. GET `/{A}/suggestions` below `/BankTransaction` must
   return exactly the four eligible Expense IDs and A's count must be four.
   GET C's suggestions must return `[]`. Suggestions must not create a link.
3. POST `/BankTransaction/{A}/match` with each amount/day-ineligible Expense ID and
   POST C's Match with an otherwise eligible Expense: require 409 and unchanged
   review states. Match bodies use `{"expenseId": <numeric ID>}`.
4. Match A to `near`: require 200/matched with the correct durable linked Expense.
   Fresh B suggestions exclude it. B matching that same Expense and A repeating
   Match return 409. Independently read PostgreSQL: only A has that link.
5. Unmatch A with an incorrect expected Expense ID: require 409. Unmatch with the
   actual expected ID: require 200/needsReview/null link. GET the Expense again:
   it must still exist unchanged. Unmatch bodies have the same `expenseId` shape.
6. POST `/BankTransaction/{A}/create-and-match` with a nonexistent category: require
   422, unchanged Expense count and no Bank link. This validates a rejected Save;
   **it is not evidence of rollback after an Expense insert**.
7. Submit the eighth Expense once using the flat ExpenseCreate body, deliberately
   discard the successful response body, then GET `/BankTransaction`. A must be
   matched to the new marker-prefixed Expense. Repeat Save while still matched:
   require 409 and exactly one additional Expense. Verify the persisted link with
   an independent SQL read. This proves current-state recovery after a discarded
   success, **not** actual timeout/502 or unknown database commit behavior.
8. Unmatch the created Expense by its expected ID. POST `/BankTransaction/{A}/ignore`
   and read `/BankTransaction?reviewStatus=ignored`; A must be ignored. POST
   `/BankTransaction/{A}/review` and read `?reviewStatus=needsReview`; A must return
   to review with no link. Retain both Expenses from the two Unmatch operations.
9. GET `/Expense/latest/100`: assert all eight created IDs are present. Fetch
   `/Expense/history?pageSize=7&pageIndex=0` and all subsequent zero-based pages
   until `hasMore=false`. Check page metadata, invariant total, unique IDs and
   count equality. Compare **every** exported record independently with ordered
   PostgreSQL rows: ID, amount text including scale, currency, occurrence, category,
   description, creator and audit timestamps. Validate Europe/Lisbon offsets as
   well as instant equality. Compare the source again after export for drift.
10. Recompute the pre-existing-record fingerprints and require equality. Verify
    retained fixture identities, final review/link states and aggregate counts.
    Record failures and partial outcomes; never transform unperformed checks into
    passes. Recheck the served build and Lambda revision at the end.

For SQL readback use only the deployed staging credential/database verified above.
Existing `UNIQUE(expense_id)`, restrictive FK and review-state CHECK metadata prove
constraint presence; only a separately performed experiment proves its behavior.
Do not inject a trigger, weaken a constraint, alter a category or invent destructive
failure controls just to manufacture a live post-insert rollback. The existing
USD fixture offers a natural test: after fresh owner authorization, POST valid
EUR Expense input with a unique description to C's create-and-Match route. At
served build `ea41d9c`, the Expense INSERT precedes the guarded link UPDATE, which
rejects USD. Verify 409, zero rows for that description, unchanged application-row
fingerprints and unchanged C state/link. Inspect the actual generated identity
sequence before/after to corroborate allocation; a consumed ID without a retained
Expense is expected after rollback. The tested single attempt advanced it once,
with no application-row drift observed. Never reset the sequence. If any Expense remains,
retain it and report failed rollback instead of deleting evidence. Recheck source
order on later builds: moving eligibility validation before INSERT would turn
this into a pre-insert rejection, no longer a rollback experiment. The dated
follow-up records this test passing against the deployed API and real Postgres.

## Retention, not cleanup

**No DELETE, reset, TRUNCATE, DROP, clear-all or cleanup endpoint is part of this
procedure.** Fixtures are deliberately retained with owner approval. No deletion
request can reach personal data because this run issues no deletion request at all;
that is not a claim that the staging credential is restricted to fixtures.

The marker does not authorize cleanup. Any later deletion is a new owner decision,
with exact identities and then-current state/revision checks. Never reinterpret
this retention agreement as permission to delete by marker, ID range, old manifest,
`X-Sheet-Scope` or `/Smoke`. Missing/uncertain first readback is not deletion intent.

## Separate browser, visual and reporting gates

Capture browser functionality and visual fidelity as distinct evidence sets, pinned
to UI/API served versions, viewport, route, Figma file/frame and run-owned records.
Do not use a local API stub to claim deployed auth, CORS or real persistence.

- On mobile and desktop verify All initially selected and rows collapsed; one
  expanded row; four candidates with three initially visible and Show more;
  explicit Match, linked Expense display, Unmatch, Ignore and return-to-review.
  Verify exact `No suggested matches` wording, available Create expense, disabled
  in-flight actions, visible errors, cancel-without-save, editable time, and filter/
  scroll return after Save. Compare the approved time-entry and Bank screenshots
  separately. Owner removal of rule/reason commentary remains authoritative.
- The approved live budget has already created eight Expenses. Further browser
  Saves need a new record budget/hold agreement; use read-only inspection or
  cancellation until then. Unknown-response simulation must not resend automatically.
- Reopening the staging workbook triggers downstream spreadsheet writes and needs
  separate owner approval. Follow `sheets-refresh.md`: verify current signed
  reporting identity/security checks, correct staging URL, complete refresh and
  editor-open trigger. Confirm new Expense IDs in the API-fed tab, refreshed status
  and preserved archive. Never replace authentication with the agent key or copy
  tokens into chat. Agent history parity does not prove Apps Script auth/open.
- Dedicated concurrent sessions remain owner-deferred. Record actual rollback and
  genuinely uncertain-response experiments as pending unless safely authorized and
  performed. Hermetic fault injection is not live PostgreSQL evidence.

## Production checkpoint

Historical #132 owner acceptance established the operational cutover disposition;
it did not execute every original abort/recovery rehearsal. #150 retired selectors
and Sheets persistence. Neither historical owner acceptance nor this staging run
is a new production rollout, reporting-refresh or write approval.

No production mutation, deployment, credential change, migration or reporting
refresh is authorized by this procedure. Keep #126 open. Live post-insert rollback
passed; José accepted the recorded visual differences and explicitly waived the
live lost-response experiment for this release. Dedicated concurrency remains
owner-deferred. Neither waiver is a test pass. #136 is ready for closure through
the evidence PR on owner merge, not by claiming production readiness.
Real human/agent history parity, read-only staging browser checks, local browser
return-context tests and owner reporting security/reopen confirmation are recorded
in the dated evidence. Owner manual Save/return confirmation remains distinct
from agent-driven live browser read-only checks.

Dated results: [staging](evidence/136-staging-2026-10-18.md) and
[separate UI/visual evidence](evidence/136-ui-2026-10-18.md).
