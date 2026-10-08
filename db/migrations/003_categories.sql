-- 003_categories: categories mirror in Postgres + suggestion FK (issue #112, ADR-0014).
-- The mirror is SEEDED from the Sheets category list (lazy, at sync time, idempotent by
-- name); Sheets remains the source of truth for category writes. Postgres-as-truth is a
-- declared future step. Idempotent by design (IF NOT EXISTS); gated by the _migrations ledger.

CREATE TABLE IF NOT EXISTS categories (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        text NOT NULL,
    description text,
    create_date timestamptz NOT NULL DEFAULT now(),
    update_date timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT categories_name_unique UNIQUE (name)
);

ALTER TABLE bank_transactions
    ADD COLUMN IF NOT EXISTS suggested_category_id bigint REFERENCES categories (id);
