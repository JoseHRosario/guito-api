-- Immutable operation evidence deliberately has no Expense FK: deletion must retain recovery.
CREATE TABLE IF NOT EXISTS bank_expense_creation_receipts (
    operation_id uuid PRIMARY KEY,
    transaction_id bigint NOT NULL CHECK (transaction_id > 0),
    expense_id integer NOT NULL CHECK (expense_id > 0),
    intent text NOT NULL
);
