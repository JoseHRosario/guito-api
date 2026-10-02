# Amounts stored positive (outflow implied)

Guito stores and transmits every Expense `Amount` as a **positive** number; that it is an outflow is implied by it being an Expense. Earlier the convention was negative-at-create (the UI negated the parsed amount and the list rendered the stored negative verbatim). The flip was decided 2026-10-02 alongside the Favorites feature: positive storage is what the future Postgres migration and any external consumer should expect, and a sign that only means "outflow" adds confusion, not information.

## Consequences

- `POST /Expense` receives positive amounts; the UI no longer negates; the list renders the stored value as-is (no `-` prefix).
- Existing sheet rows (negative) need a **one-time data migration script**; the code flip and the migration must land together or old and new rows render inconsistently.
- The approved Figma frames that show `-65,55 €` are superseded on this point.

_Supersedes the "post the negative" convention noted in `create-expense-page.ts` and the negative-amount examples in the approved frames._