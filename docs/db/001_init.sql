-- 001_init: migration ledger + baseline (issue #80). Application tables (bank_accounts,
-- bank_transactions) arrive with #82 in their own numbered scripts.
CREATE TABLE IF NOT EXISTS _migrations (
    name          text PRIMARY KEY,
    applied_at    timestamptz NOT NULL DEFAULT now()
);
