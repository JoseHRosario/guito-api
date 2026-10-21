-- #159: forward-only identity evidence. No legacy identity guesses or financial backfill.
-- uid remains the immutable Guito account key and existing transaction FK target.
ALTER TABLE bank_accounts ADD COLUMN IF NOT EXISTS identification_hash text;
ALTER TABLE bank_accounts ADD COLUMN IF NOT EXISTS fetch_uid text;
CREATE UNIQUE INDEX IF NOT EXISTS bank_accounts_identification_hash_unique
    ON bank_accounts (identification_hash) WHERE identification_hash IS NOT NULL;

ALTER TABLE bank_transactions ADD COLUMN IF NOT EXISTS entry_reference text;
ALTER TABLE bank_transactions ADD COLUMN IF NOT EXISTS identity_reliable boolean;
ALTER TABLE bank_transactions ADD COLUMN IF NOT EXISTS transaction_date date;
ALTER TABLE bank_transactions ADD COLUMN IF NOT EXISTS value_date date;
CREATE INDEX IF NOT EXISTS bank_transactions_reference_idx
    ON bank_transactions (account_uid, entry_reference) WHERE entry_reference IS NOT NULL;
