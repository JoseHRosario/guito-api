-- Reporting membership and values are copied atomically, never inferred from sequences.
CREATE TABLE IF NOT EXISTS expense_history_snapshots (
    id uuid PRIMARY KEY,
    expires_at timestamptz NOT NULL,
    total_count bigint NOT NULL CHECK (total_count >= 0)
);

CREATE INDEX IF NOT EXISTS expense_history_expiry_idx ON expense_history_snapshots (expires_at);

CREATE TABLE IF NOT EXISTS expense_history_rows (
    snapshot_id uuid NOT NULL REFERENCES expense_history_snapshots(id) ON DELETE CASCADE,
    position bigint NOT NULL CHECK (position > 0),
    expense_id integer NOT NULL,
    booking_date timestamptz NOT NULL,
    create_date timestamptz NOT NULL,
    update_date timestamptz,
    amount text NOT NULL,
    description text NOT NULL,
    category text NOT NULL,
    creator_email text NOT NULL,
    PRIMARY KEY (snapshot_id, position),
    UNIQUE (snapshot_id, expense_id)
);

CREATE TABLE IF NOT EXISTS expense_history_cursors (
    token uuid PRIMARY KEY,
    snapshot_id uuid NOT NULL REFERENCES expense_history_snapshots(id) ON DELETE CASCADE,
    start_position bigint NOT NULL CHECK (start_position > 0),
    page_size integer NOT NULL CHECK (page_size BETWEEN 1 AND 100),
    UNIQUE (snapshot_id, start_position, page_size)
);

-- deploy/db-apply.sh grants SELECT/INSERT/UPDATE/DELETE on ALL public tables to
-- the environment runtime role after migrations, including these three tables.
