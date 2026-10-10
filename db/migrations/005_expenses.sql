-- Manual expenses. Bank transactions remain separate until reconciliation.
-- Unconstrained numeric preserves exact decimal input without imposing positivity.
CREATE TABLE IF NOT EXISTS expenses (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    amount numeric NOT NULL CHECK (amount::text NOT IN ('NaN', 'Infinity', '-Infinity')),
    description text NOT NULL,
    category_id bigint NOT NULL REFERENCES categories(id),
    creator_email text NOT NULL,
    booking_date timestamptz NOT NULL,
    create_date timestamptz NOT NULL DEFAULT now(),
    update_date timestamptz
);

CREATE INDEX IF NOT EXISTS expenses_latest_idx
    ON expenses (booking_date DESC, id DESC);
