# Amounts stored positive (outflow implied)

Guito stores and transmits every Expense `Amount` as a **positive** number; that it is an outflow is implied by it being an Expense. Earlier the convention was negative-at-create (the UI negated the parsed amount and the list rendered the stored negative verbatim). The flip was decided 2026-10-02 alongside the Favorites feature: positive storage is what the future Postgres migration and any external consumer should expect, and a sign that only means "outflow" adds confusion, not information.

## Consequences

- `POST /Expense` receives positive amounts and **rejects non-positive amounts with 400** (`ExpenseCreate.Amount` is `[Range]`-validated strictly positive), so no caller can persist a negative.
- The guito-ui create screen posts the parsed amount as-is (negation removed) and the list renders the stored value as-is (no `-` prefix).
- The spreadsheet data was already positive when this was decided — no data migration was needed or performed.
- The approved Figma frames that show `-65,55 €` are superseded on this point.

_Supersedes the "post the negative" convention noted in `create-expense-page.ts` and the negative-amount examples in the approved frames._