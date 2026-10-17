# Manual existing-Expense matching (#134)

API **0.24.0** extends [ADR-0018](adr/0018-bank-transaction-review.md).
Only existing Expense Match/Unmatch is included; atomic create-and-match remains
#135. Approved desktop/mobile designs remove visible rule/reason commentary,
not eligibility or the explicit Match decision.

## HTTP contract

All routes retain existing human/agent authentication. Reporting identities remain
confined to GET `/Expense/history`; matching adds no reporting permission.

- GET `/BankTransaction` keeps its bare array, existing fields, review filters and
  order. Each row adds `suggestedExpenseCount`, a JSON number; it is zero unless
  the row is unlinked Needs review. The count uses the same complete-history
  eligibility predicate as suggestions and Match.
- GET `/BankTransaction/{id}/suggestions` returns a bare array of
  `{id,bookingDate,amount,currency,description,category}`. Expense ID is an Int32
  JSON number, occurrence is ISO8601 with its Europe/Lisbon offset, amount is exact
  invariant decimal text, currency is EUR. Equivalent trailing zeros need not be
  retained by this existing Bank linked-Expense representation. No confidence or
  reason fields are introduced. Matched/Ignored rows return `[]`.
- POST `/BankTransaction/{id}/match` accepts `{"expenseId":7}` and returns the
  updated existing BankTransaction DTO including linkedExpense and count zero.
- POST `/BankTransaction/{id}/unmatch` accepts `{"expenseId":7}` as the **expected
  existing link**, not a request to clear any current link. It returns the updated
  BankTransaction in Needs review with linkedExpense null and recomputed count.
  It never deletes or edits an Expense or a Transaction.

Transaction IDs must be positive persisted Int64 values, never provider IDs.
Expense IDs must be positive Int32 JSON numbers, not strings, fractions or coerced
identifiers. Invalid/omitted/null IDs or bodies return 400; missing exact records
404; stale/ineligible/conflicting actions 409; provider/response-limit failures 502.
Errors are ProblemDetails. Repeated Match or Unmatch is a conflict, not a second
link decision. Ignore/Return keep their existing idempotent behavior and also
return the count. Ordinary MVC JSON serialization remains unchanged.

## Eligibility and deterministic ranking

Eligible Expenses are unlinked, have the **exact numeric amount** of the Bank
Transaction, and have a Lisbon occurrence day within three calendar days either
side of its bank booking day (inclusive). Expense currency is implicitly EUR under
the existing schema, so only EUR Transactions are eligible. No amount conversion,
double comparison, latest-history cap, manual search, description exclusion, AI,
automatic link or historical backfill is used.

Ranking (descending similarity, then ascending distance, then descending occurrence
instant, then descending **numeric** Expense ID):

1. Uppercase both descriptions invariantly; split into maximal Unicode letter/number
   tokens. Compare distinct token sets using Jaccard similarity: intersection count
   divided by union count, with empty union scored zero. Bank null description is
   an empty token set. Punctuation and casing do not affect tokens; accents remain.
2. Absolute Lisbon calendar-day distance from bank booking day.
3. Actual Expense occurrence instant descending.
4. Int32 Expense ID descending.

All eligible history is returned and counted. The UI alone chooses an initial
three and Show more. Description similarity ranks only; unrelated descriptions
remain eligible. Data API transport limits/outages fail the complete operation
rather than returning partial success. There is no arbitrary endpoint payload budget.

## Persistence and stale-action safety

Reuse migration 009's restrictive Expense FK, unique expense_id and coherent-state
CHECK; no new migration, revision scheme or idempotency table. Within the existing
unit of work, lock the exact Expense first, then the exact Bank row, matching the
[Expense deletion contract](expense-edit-delete-contract.md). The guarded Match
UPDATE revalidates all eligibility and link predicates after those locks; the
Unmatch UPDATE requires the exact expected link and Matched state. Read back the
post-write row in the same transaction, then commit. A subsequent SQL statement
is deliberate: an Unmatch count must see the newly freed Expense, not a
pre-update statement snapshot.

Stale old-tab/double-tap failures do not overwrite another link. Expense edit
retains a confirmed link even if its new amount/day is no longer eligible;
Unmatch can still remove that exact link. Expense delete clears it and returns the
Bank row to Needs review atomically. Neither operation gains a new revision model.
Failures before commit roll back via existing unit-of-work disposal. A lost or
failed commit response can be outcome-unknown: read the current Bank state before
renewing a deliberate action, never automatically retry or claim rollback.

## Verification boundary

Hermetic HTTP tests use real services/repositories and the existing authoritative,
predicate-aware fake Data API. They cover full history/counts/ranking, exact amount
and identity, Lisbon/DST boundaries, eligibility, actions, stale links, rollback,
provider errors and cross-feature Expense edit/delete behavior. Mutation checks
establish sensitivity to guards; they do **not** establish real PostgreSQL locks,
constraints or persistence.

José explicitly deferred dedicated real concurrency acceptance and expanded
orchestration: this is a one-person-at-a-time application. Basic guarded actions,
existing constraints and hermetic stale/conflict regressions remain. Real SQL
concurrency is **unverified/deferred by owner**, not an acceptance pass. No live
financial fixture/cleanup framework, AWS change or deployment is part of this work.
Parent/owner retains final review, staging and production gates.

### Local implementation evidence

The HTTP tracer tests first failed for absent suggestions/Match/Unmatch routes,
ranking/count behavior and strict numeric Expense ID binding. Transaction begin
and commit provider-failure tests initially returned 500, then passed with explicit
502 mapping. Final rebuilt gates: solution build succeeds with zero warnings/errors;
164 targeted Bank/Expense mutation cases pass; full `Category!=Integration` passes
594 API and 79 authorizer cases. Linux uses `DOTNET_USE_POLLING_FILE_WATCHER=1`.

Removing amount, EUR, Lisbon conversion, day window, linked-Expense exclusion,
review-state/unlinked-Bank guards, exact Expense selection, expected Unmatch link,
commit, similarity ranking or Expense-delete unlink independently makes the
corresponding HTTP regressions fail. All mutations were restored before final
rebuild. The unlinked-Bank mutation initially survived coherent-state-only tests;
an added incoherent-state regression now detects its removal. Independent staged
source review reported no blocking security/logic findings; its convention and
edited-Expense follow-ups were implemented. These are hermetic/source-review
results, not live PostgreSQL acceptance.
