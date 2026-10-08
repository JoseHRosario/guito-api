-- 004_seed_categories: one-time seed of the categories mirror (issue #112, ADR-0014).
-- The names mirror the Sheets Config tab list (snapshot at authoring time); Sheets
-- remains the source of truth for category writes. Idempotent: ON CONFLICT (name)
-- DO NOTHING — re-running (or a later environment) never duplicates or overwrites.

INSERT INTO categories (name) VALUES
    ('Eating out'),
    ('Education'),
    ('Entertainment'),
    ('Gifts'),
    ('Groceries'),
    ('Health/medical'),
    ('Home'),
    ('Home supplies'),
    ('Insurance'),
    ('Integration Tests'),
    ('Other'),
    ('Personal'),
    ('Taxes'),
    ('Transportation'),
    ('Utilities')
ON CONFLICT (name) DO NOTHING;
