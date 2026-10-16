# ADR-0018: Bank Transaction review inbox

Status: Accepted for implementation under #133, within #126.
Extends ADR-0013 and ADR-0015; matching operations remain later slices.

## Start checkpoint disposition

[José approved #133 development and the Figma designs](https://github.com/JoseHRosario/guito-api/issues/133#issuecomment-6157279425).
Initial Bank is All selected and fully collapsed; only a row click expands it,
with at most one open row. Approval disposes the development start gate; it does
not retroactively claim the distinct abort/recovery rehearsal was performed.
[#132 owner closure](https://github.com/JoseHRosario/guito-api/issues/132#issuecomment-6135613997)
records accepted production Postgres cutover and working saves/reporting, while
preserving the original unverified rehearsal criteria. See the historical
preparation record and current disposition in [expense-cutover.md](../expense-cutover.md).

## Persisted invariants

`bank_transactions.status` stays BOOK. Separate constrained text `review_status`
is `needsReview`, `matched` or `ignored`; no status table. New and historical
unlinked rows start `needsReview`. Only an existing valid persisted Expense link
is `matched`; category suggestions never complete review or create links.

Migration `009_bank_review.sql` converts unused text `expense_id` to integer,
matching `expenses.id`, with restrictive FK, unique link and coherent-state CHECK.
Uniqueness enforces the agreed one-to-one relationship now rather than leaving
an invalid database state for the future Match endpoint to repair. No Match,
Unmatch, candidates or create-and-link operation is introduced here.

The migration uses ordinary statements compatible with the existing semicolon
splitter. Conversion, FK and unique validation are one atomic ALTER: invalid
integer values, missing Expenses and duplicate links fail without dropping or
repairing existing links. Earlier ADD COLUMN may remain after failure; the
migration is rerunnable after an explicitly owner-approved data decision. Null
review states are initialized only after link validation; reruns retain Ignored.
Do not automatically clear invalid links or manufacture matching Expenses.
The migration may lock/rewrite the table; coordinate its rollout before serving
new API code. No ORM, new dependency, AWS provisioning or reset is required.

## HTTP contract

- `GET /BankTransaction` returns a bare array of **all stored rows**, ordered by
  booking day descending then numeric Int64 ID descending. Optional query
  `reviewStatus=all|needsReview|matched|ignored`; invalid/empty values return 400.
  The initial UI asks for All, not only Needs review.
- Existing `id` (Int64 JSON number), `accountUid`, date-only `bookingDate`
  (`yyyy-MM-dd`), `currency`, nullable `remittanceInformation` and
  `suggestedCategory` (`{id,name}` or null) remain. `amount` is now an exact
  invariant decimal **string** using the existing property-specific converter;
  the coordinated UI adapter also accepts legacy numeric amounts.
- Added `status` is BOOK; `reviewStatus` uses the lower-camel values above.
  `linkedExpense` is null or `{id,bookingDate,amount,currency,description,category}`:
  ID is an Int32 JSON number, bookingDate is the occurrence timestamp presented
  with its Europe/Lisbon offset, amount is exact invariant decimal text, currency
  is EUR under the existing Expense convention. It is not the bank booking day.
- `POST /BankTransaction/{id}/ignore` and `/review` accept an empty body and return
  the updated Transaction DTO. Positive Int64 IDs only: invalid 400, missing 404,
  matched/ineligible 409. Ignored → Needs review and the reverse are reversible;
  repeating the target state is successful and idempotent.

Each action is a guarded SQL UPDATE of an unlinked Needs review/Ignored row.
Ignore and Return-to-review remain separate operation-specific services. They pass
the internal `Model.MatchStatus` enum (`NeedsReview`, `Matched`, `Ignored`) to the
repository rather than string literals. The SQL adapter explicitly maps supported
targets to the existing stored text, rejecting `Matched` and undefined enum values
before issuing SQL: establishing/removing a Match requires an Expense-link operation.
This narrow refactor changes neither routes, JSON values, filters nor database schema;
`All` remains a filter, not an enum member. A Match service belongs to #134, not #133.
PostgreSQL rechecks the predicate after waiting for a concurrent updater; a
concurrent Match cannot be overwritten. The response reads UPDATE RETURNING
rather than the pre-update table snapshot. If no row updates, a fresh exact-ID
read distinguishes missing from conflict. A response describes the operation's
result, not a promise that another concurrent action cannot subsequently change it.
A transport failure after a write may be outcome-unknown: read current state and
retry the idempotent action, rather than assuming rollback.

Complete reads are uncapped and all-or-fail. Data API response/row limits and
provider failures produce 502, never empty or truncated success. Invalid exact
amounts/coherent-state data also fail explicitly. Ordinary MVC serialization is
retained; no endpoint-specific payload budget or serializer is introduced.
Existing booked-debit ingestion, sync deduplication and seven-day window stay
unchanged. Following merged #150, Category and Expense authority is Postgres-only;
the retired Sheets scope header is ignored and `/Smoke` is removed. Bank review
does not restore a store selector, Sheets adapters or a write-smoke framework.

## Reconciliation with #146 and #150

The combined feature declares API 0.23.0 after master's 0.22.0. Migration 009 and
the review routes coexist with migration 010 and revision-aware Expense operations.
Expense deletion locks the Expense before bank rows, clears only its links and
returns those Transactions to `needsReview` in the same transaction. Updates retain
links. Future Match writers must follow that same lock order and establish a valid
Expense link; `ChangeReviewStatusAsync` still rejects `Matched` as a target.
See [the Expense mutation contract](../expense-edit-delete-contract.md) and
[the retirement boundary](../sheets-persistence-retirement.md). Historical #132
rehearsal gaps and #133's separate live persistence/constraint/concurrency gate are
not satisfied by this merge or hermetic tests.

## Verification boundary

HTTP tests compose the real services/repository over fake Data API transport:
all filters, complete ordering/ties, preserved fields, exact amounts and Lisbon
linked timestamps, persisted reversible/idempotent actions, invalid/missing IDs,
matched and concurrent-match conflicts, empty results and provider errors.
CI stays hermetic. These tests do not establish actual PostgreSQL DDL, constraints,
locks or persistence. Separate approved staging migration reruns, FK/uniqueness
checks and concurrent guarded updates are required; this implementation does not
execute AWS, staging or production changes.
