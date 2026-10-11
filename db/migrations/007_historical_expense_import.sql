-- Each statement is independently resumable with the existing semicolon runner.
CREATE TABLE IF NOT EXISTS expense_import_sources (
    source_key text PRIMARY KEY,
    current_snapshot_id text NOT NULL
);
CREATE TABLE IF NOT EXISTS expense_import_snapshots (
    id text PRIMARY KEY,
    source_key text NOT NULL REFERENCES expense_import_sources(source_key),
    metadata jsonb NOT NULL,
    reconciliation jsonb,
    captured_at timestamptz NOT NULL,
    create_date timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS expense_import_rows (
    snapshot_id text NOT NULL REFERENCES expense_import_snapshots(id),
    source_row integer NOT NULL,
    raw_json jsonb NOT NULL,
    classification text NOT NULL,
    expense_id integer REFERENCES expenses(id),
    exclusion jsonb,
    PRIMARY KEY (snapshot_id, source_row),
    UNIQUE (snapshot_id, expense_id),
    CHECK (expense_id IS NULL OR exclusion IS NULL)
);
