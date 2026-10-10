-- 005_expenses: opt-in Expense authority (issue #128, ADR-0015).
-- Each statement is independently resumable by the semicolon-based runner.
-- Unconstrained numeric preserves decimal precision instead of bank numeric(18,2).
CREATE TABLE IF NOT EXISTS expenses (
    id text PRIMARY KEY CHECK (id ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'),
    occurred_at timestamptz NOT NULL,
    amount numeric NOT NULL CHECK (amount > 0 AND amount <> 'NaN'::numeric AND amount <> 'Infinity'::numeric),
    currency text NOT NULL CHECK (currency = 'EUR'),
    description text NOT NULL,
    category text NOT NULL,
    creator_email text NOT NULL,
    creation_date timestamptz NOT NULL DEFAULT now(),
    update_date timestamptz NOT NULL DEFAULT now(),
    scope text NOT NULL CHECK (scope IN ('normal', 'smoke'))
);

CREATE INDEX IF NOT EXISTS expenses_latest_idx
    ON expenses (scope, occurred_at DESC, id DESC);
