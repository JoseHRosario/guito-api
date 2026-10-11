-- One Data API ExecuteStatement call is one atomic PostgreSQL transaction.
-- Expense insertion and its source-row link therefore commit or roll back together.
WITH source_lock AS MATERIALIZED (
    SELECT source_key FROM expense_import_sources
    WHERE source_key = :source_key AND current_snapshot_id = :snapshot_id FOR UPDATE
), input AS MATERIALIZED (
    SELECT CAST(:row AS jsonb) AS value FROM source_lock
), inserted AS (
    INSERT INTO expenses (booking_date, amount, description, category_id, creator_email)
    SELECT (value->>'bookingDate')::timestamptz, (value->>'amount')::numeric,
           value->>'description', category.id, value->>'creator'
    FROM input JOIN categories category ON category.name = value->>'category'
    WHERE value->>'classification' = 'Expense'
      AND NOT EXISTS (SELECT 1 FROM expense_import_rows
          WHERE snapshot_id = :snapshot_id AND source_row = (value->>'sourceRow')::integer)
    RETURNING id
)
INSERT INTO expense_import_rows (snapshot_id, source_row, raw_json, classification, expense_id, exclusion)
SELECT :snapshot_id, (value->>'sourceRow')::integer, value->'raw',
       value->>'classification', inserted.id, value->'exclusion'
FROM input LEFT JOIN inserted ON true
WHERE NOT EXISTS (SELECT 1 FROM expense_import_rows
    WHERE snapshot_id = :snapshot_id AND source_row = (value->>'sourceRow')::integer)
  AND (value->>'classification' <> 'Expense' OR inserted.id IS NOT NULL)
RETURNING source_row;
