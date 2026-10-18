-- Browser handles are random capabilities: retain only SHA-256 hashes.
-- Refresh credentials are application-encrypted AES-256-GCM envelopes, not plaintext.
CREATE TABLE IF NOT EXISTS human_sessions (
    handle_hash text PRIMARY KEY CHECK (handle_hash ~ '^[A-F0-9]{64}$'),
    subject text NOT NULL,
    email text NOT NULL,
    encrypted_credential text NOT NULL CHECK (encrypted_credential LIKE 'v1.%'),
    expires_at timestamptz NOT NULL,
    refresh_lease text,
    lease_expires_at timestamptz,
    CHECK ((refresh_lease IS NULL) = (lease_expires_at IS NULL))
);
CREATE INDEX IF NOT EXISTS human_sessions_expiry_idx ON human_sessions (expires_at);
