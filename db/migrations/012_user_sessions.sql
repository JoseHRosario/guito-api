-- 011 is already applied: preserve that file and every existing session value.
-- A single DO statement commits all metadata renames together. A retry after a
-- lost response or before ledger insertion sees only the new names and is a no-op.
DO $migration$
DECLARE
    old_name text;
    new_name text;
BEGIN
    IF to_regclass('public.human_sessions') IS NOT NULL THEN
        IF to_regclass('public.user_sessions') IS NOT NULL THEN
            RAISE EXCEPTION 'Both session tables exist: operator review required';
        END IF;
        ALTER TABLE public.human_sessions RENAME TO user_sessions;
    END IF;
    IF to_regclass('public.user_sessions') IS NULL THEN
        RAISE EXCEPTION 'Expected session table is missing';
    END IF;
    LOCK TABLE public.user_sessions IN ACCESS EXCLUSIVE MODE;
    FOR old_name, new_name IN
        SELECT * FROM (VALUES
            ('human_sessions_pkey', 'user_sessions_pkey'),
            ('human_sessions_handle_hash_check', 'user_sessions_handle_hash_check'),
            ('human_sessions_encrypted_credential_check', 'user_sessions_encrypted_credential_check'),
            ('human_sessions_check', 'user_sessions_check')
        ) AS names(old_name, new_name)
    LOOP
        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.user_sessions'::regclass AND conname = old_name) THEN
            EXECUTE format('ALTER TABLE public.user_sessions RENAME CONSTRAINT %I TO %I', old_name, new_name);
        ELSIF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.user_sessions'::regclass AND conname = new_name) THEN
            RAISE EXCEPTION 'Expected session constraint is missing';
        END IF;
    END LOOP;
    -- Renaming the primary-key constraint also renames its backing index.
    ALTER INDEX IF EXISTS public.human_sessions_expiry_idx RENAME TO user_sessions_expiry_idx;
    IF to_regclass('public.user_sessions_expiry_idx') IS NULL THEN
        RAISE EXCEPTION 'Expected session expiry index is missing';
    END IF;
END;
$migration$;
