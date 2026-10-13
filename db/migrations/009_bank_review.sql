-- Review decisions are separate from the provider's BOOK booking status (#133).
-- Plain statements only: compatible with db-apply.sh's semicolon splitter.
-- Nullable only during migration so retries initialize only uninitialized rows.
ALTER TABLE bank_transactions ADD COLUMN IF NOT EXISTS review_status text;

-- Conversion and link validation are ONE atomic statement. Invalid integer tokens,
-- missing Expenses or duplicate links fail without converting or removing links.
-- DROP + ADD in the same ALTER keeps reruns atomic, including existing constraints.
ALTER TABLE bank_transactions
    DROP CONSTRAINT IF EXISTS bank_transactions_expense_fk,
    DROP CONSTRAINT IF EXISTS bank_transactions_expense_unique,
    ALTER COLUMN expense_id TYPE integer USING expense_id::integer,
    ADD CONSTRAINT bank_transactions_expense_fk
        FOREIGN KEY (expense_id) REFERENCES expenses(id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    ADD CONSTRAINT bank_transactions_expense_unique UNIQUE (expense_id);

-- A valid existing persisted link is already a Match, never a suggested match.
-- Do not reset Ignored decisions on rerun or fabricate links for historical rows.
UPDATE bank_transactions
SET review_status = CASE WHEN expense_id IS NULL THEN 'needsReview' ELSE 'matched' END
WHERE review_status IS NULL;

ALTER TABLE bank_transactions
    ALTER COLUMN review_status SET DEFAULT 'needsReview',
    ALTER COLUMN review_status SET NOT NULL,
    DROP CONSTRAINT IF EXISTS bank_transactions_review_state_check,
    ADD CONSTRAINT bank_transactions_review_state_check CHECK (
        (review_status = 'matched' AND expense_id IS NOT NULL)
        OR (review_status IN ('needsReview', 'ignored') AND expense_id IS NULL)
    );
