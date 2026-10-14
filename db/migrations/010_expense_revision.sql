-- Durable optimistic concurrency, independent of timestamp precision and xmin wraparound.
ALTER TABLE expenses ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0);
