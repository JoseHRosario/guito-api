# Atomic Expense creation and Bank Match (#135)

API **0.25.0** extends [ADR-0018](adr/0018-bank-transaction-review.md).

## Owner correction and migration history

José explicitly removed the previous receipt/idempotency design as
overengineering. The earlier requirement for durable creation evidence,
operation IDs and replay after Unmatch/deletion is superseded, not an outstanding
acceptance criterion. Historical receipts are no longer an accepted API contract.
No replacement ledger, retry framework or recovery endpoint is introduced.

Migration **011_bank_expense_creation_receipts.sql** already deployed to shared
staging via CI and is preserved unchanged as historical migration state.
**012_drop_bank_expense_creation_receipts.sql** is the minimal forward removal:
`DROP TABLE IF EXISTS bank_expense_creation_receipts;` — only that table, no
CASCADE. Routine migrations belong to the pipeline, not manual execution.
Receipt removal is authorized; personal-data mutations and production are not.

## Pinned API/UI contract

POST `/BankTransaction/{id}/create-and-match` accepts the flat existing
ExpenseCreate fields only: `bookingDate`, `amount`, `currency`, `description`,
`category`. No `operationId` or nested Expense. All normal Expense fields remain
editable; existing-Expense candidate amount equality and three-day rules do not
apply. Explicit Save is the link decision. Existing exact decimal, EUR,
date-only Lisbon midnight, explicit-offset/microsecond validation, removed
occurrence-field rejection and authenticated creator attribution remain.

Success is **200**, with the existing **BankTransaction review DTO**, exactly the
same current-state payload as manual Match: numeric Int64 `id`, `accountUid`,
bank `bookingDate`, exact-text `amount`, `currency`, `remittanceInformation`,
`suggestedCategory`, `status`, `reviewStatus`, `linkedExpense` and
`suggestedExpenseCount`. `reviewStatus` is `matched`; `linkedExpense` contains
the new numeric Int32 Expense ID and current editable Expense fields. No creation
receipt or historical creation result is returned. Normal human/agent auth
remains; reporting credentials gain no access.

- **400**: malformed/nonpositive Int64 ID, malformed body or Expense validation.
- **404**: missing Bank Transaction.
- **409**: already matched, stale or unsupported Bank state.
- **422**: unknown category, as in normal Expense creation.
- **502**: provider failure or unknown commit outcome.

Creation requires unlinked `needsReview`, EUR, BOOK/DBIT, a positive finite stored
Bank amount and bank booking day within years 0001–9999. Existing one-to-one,
restrictive FK and coherent-review-state constraints remain authoritative.

## Atomicity and uncertain outcomes

The new Expense and guarded Bank link commit in **one existing ambient Data API
unit of work**. No nested manual Match transaction. The Bank row is locked before
creating the fresh private Expense; existing-Expense matching/deletion keep their
Expense-before-Bank lock order. A failed guarded update rolls back the new
Expense instead of leaving an orphan, and cannot overwrite an existing link.
A second submission while matched returns 409, not successful replay.

After a timeout/502, the UI uses existing **GET `/BankTransaction`** to refresh
current state before the user decides what to do next. A failed commit response
does not prove rollback, and an unlinked read does not prove an in-flight Save
cannot subsequently commit. There is **no automatic retry or replay promise**.
After later Unmatch/deletion, a new explicit Save is a new creation decision,
not a replay of an earlier one.

## Hermetic verification boundary

HTTP WebApplicationFactory tests compose real services/repositories over fake
Data API transport. They cover flat Expense input and current Bank response,
exact money/occurrence, validation/auth, stale/duplicate guarded rollback,
provider-stage rollback and current-state recovery after a lost commit response.
The simplified contract test first failed 400 without operationId, then passed
200 after removal of that requirement.

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_USE_POLLING_FILE_WATCHER=1
dotnet test tst/guito-api.Tests -c Release --no-restore --filter 'FullyQualifiedName~BankCreateAndMatch'
dotnet build -c Release --no-restore
dotnet test -c Release --no-restore --filter "Category!=Integration"
```

Hermetic tests do not establish live PostgreSQL DDL, locking or constraint
behavior. Real concurrency acceptance remains owner-deferred, not passed. This
work executes no migration, AWS call, live financial mutation or deployment.
