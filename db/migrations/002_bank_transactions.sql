-- 002_bank_transactions: Bank Transactions aggregate (issues #82/#86, ADR-0013).
-- bank_accounts: one row per Enable Banking account linked through the auth flow.
-- bank_transactions: synced DBIT/BOOK expense-direction rows, deduped by sync_key.
-- Idempotent by design (IF NOT EXISTS); also gated by the _migrations ledger.

CREATE TABLE IF NOT EXISTS bank_accounts (
    uid              text PRIMARY KEY,          -- Enable Banking account uid
    session_id       text NOT NULL,             -- EB session this account belongs to
    iban             text,
    name             text NOT NULL,
    currency         text NOT NULL,
    aspsp_country    text NOT NULL,
    aspsp_name       text NOT NULL,
    consent_status   text NOT NULL,             -- e.g. VALID / expired (EB consent state)
    consent_expires_at timestamptz,
    created_at       timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS bank_transactions (
    id                   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_uid          text NOT NULL REFERENCES bank_accounts (uid),
    booking_date         date NOT NULL,
    amount               numeric(18, 2) NOT NULL CHECK (amount > 0),  -- stored positive (ADR-0010)
    currency             text NOT NULL,
    direction            text NOT NULL CHECK (direction = 'DBIT'),    -- income (CRDT) out of scope
    remittance_information text,
    status               text NOT NULL CHECK (status = 'BOOK'),       -- PDNG/INFO never stored
    sync_key             text NOT NULL,   -- hash of account uid + booking_date + amount +
                                          -- credit_debit_indicator + entry_reference + counterparty
    expense_id           text,            -- nullable until the matching flow (#83) links it
    imported_at          timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT bank_transactions_sync_key_unique UNIQUE (sync_key)
);

CREATE INDEX IF NOT EXISTS bank_transactions_account_idx
    ON bank_transactions (account_uid, booking_date);

-- GET /BankTransaction pending-rows query (#82: unmatched rows for the matching screen)
CREATE INDEX IF NOT EXISTS bank_transactions_pending_idx
    ON bank_transactions (account_uid)
    WHERE expense_id IS NULL;
