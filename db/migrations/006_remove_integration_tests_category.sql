-- Remove the smoke-only category accidentally included in migration 004.
-- Match both identity and name so a different category at ID 10 is never deleted.
-- Keep foreign-key protection: referenced expenses or bank suggestions must be
-- investigated explicitly, not deleted or reassigned by this migration.
DELETE FROM categories
WHERE id = 10 AND name = 'Integration Tests';
