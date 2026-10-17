# Atomic Expense creation and Bank Match (#135)

API **0.25.0** extends [ADR-0018](adr/0018-bank-transaction-review.md).
Migration **011_bank_expense_creation_receipts.sql** must precede serving these
routes. There are no new screens, design gates, dependencies or pipeline controls.

POST `/BankTransaction/{id}/create-and-match` accepts the flat normal ExpenseCreate
fields (`bookingDate`, `amount`, `currency`, `description`, `category`) plus required
nonempty UUID `operationId`. Every normal Expense field remains editable: the
existing-Expense suggestion amount equality and three-day window do not apply.
Explicit Save is the link decision. Existing Expense exact decimal, EUR, date-only
Lisbon midnight, explicit offset and six-digit fractional timestamp validation,
removed occurrence-field rejection and authenticated creator attribution apply.

Success is 200 `{operationId:string,transactionId:number,expenseId:number}` with a
raw numeric Int64 Transaction ID and numeric Int32 Expense ID. Transaction IDs
must be positive Int64 values. GET
`/BankTransaction/{id}/create-and-match/{operationId}` returns that same receipt or
404 when no committed receipt exists for that pair. Receipt GET has
`Cache-Control: no-store`, including 404. Both routes retain normal human/agent
auth; reporting credentials gain no access.

Clients generate one UUID for a deliberate Save and retain it across uncertain
responses. An identical retry returns the original immutable receipt without
creating or linking again, even after subsequent Unmatch or Expense deletion.
Amount comparison normalizes numeric value and bookingDate comparison normalizes
the instant; description, category and currency strings compare exactly. Creator
identity is resolved as for normal creation, not a new receipt ownership model.
Reusing a UUID for another Transaction or changed valid intent returns 409. Input
validation runs before receipt comparison, just as for normal Expense creation:
malformed amounts/dates and unsupported currency strings still return 400 even
when a UUID already has a receipt. A receipt is historical operation evidence,
not a claim about the current link or Expense.

400 covers malformed IDs/UUIDs/bodies and Expense validation; 404 a missing Bank
Transaction or absent committed receipt; 409 changed UUID intent or stale/unsupported
Bank state; 422 an unknown category; 502 provider failures or an unknown commit
outcome. Bank creation requires unlinked `needsReview`, EUR, BOOK/DBIT and positive
finite stored movement amount and a bank booking day within years 0001–9999. Existing unique Expense link, restrictive FK and coherent
review-state constraints remain authoritative.

The new Expense, guarded Bank link and immutable receipt commit in **one existing
ambient Data API unit of work**. Existing MatchAsync is never nested. The Bank row
is locked before creating a fresh, private Expense; existing Expense matching and
deletion keep their Expense-before-Bank order. A retry rechecks the receipt after
acquiring the Bank lock. UUID uniqueness arbitration rolls back all losing writes
before reading the winning receipt outside the losing transaction. No receipt FK
to Expense or cascading cleanup exists. No new concurrency framework is added.

A failed commit response never proves rollback. GET the durable receipt or retry
with the **same UUID and identical intent**, never automatically switch to a new
UUID. A 404 means no committed receipt was visible to that read; it does not prove
an in-flight transaction cannot subsequently commit. UUID uniqueness and guarded
Bank state protect retries even across that uncertainty.

Hermetic verification uses HTTP WebApplicationFactory through real services and
repositories to fake Data API. It does not establish live PostgreSQL DDL, locks or
constraint behavior. Real concurrency acceptance remains owner-deferred as in
#134; this work authorizes no live financial, AWS or deployment operations.

## Local red/green evidence

Implementation began from `a5a0415`. No AWS/financial operation or deployed test
was performed; hermetic verification does not authorize a deployment.

| Slice | Red observation | Green verification |
| --- | --- | --- |
| Edited creation through HTTP | POST 404 instead of 200 | Exact numeric IDs, edited negative amount and microsecond instant read back through Expense GET |
| Durable receipt | Identical retry 409 and receipt GET 404 | Normalized retry, changed-intent 409 and receipt surviving Unmatch/deletion |
| Applied commit with lost response | Timeout produced 500 | 502 outcome-unknown, then GET/retry recover one original Expense/link/receipt |
| Stored Bank predicates | Fake ignored BOOK/DBIT/positive amount | Predicate-aware fake rejects unsupported/stale Bank state |
| Supported stored values | Out-of-range/infinite dates accepted, NaN produced 500 | Guarded 409 with no committed Expense/link/receipt |

Mutation-stage provider faults include Expense insert, Bank link, receipt insert
and an applied receipt statement that throws before commit. HTTP reads verify
all mutations roll back together. A simulated competing UUID insertion verifies
that the losing Expense/link roll back before fresh winner recovery (same intent
200, changed intent 409). This transport simulation is not live SQL concurrency
acceptance.

The nested coding-agent sandbox denied VSTest's local TCP listener during initial
red/green development; those HTTP slices ran through the installed in-process
xUnit runner. Final verification uses ordinary VSTest outside that nested sandbox:

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_USE_POLLING_FILE_WATCHER=1
dotnet build -c Release --no-restore
dotnet test -c Release --no-restore --filter "Category!=Integration"
```

The full Release solution build and hermetic suites must pass before committing.
Integration-category suites remain excluded; no deployed fixtures run.
