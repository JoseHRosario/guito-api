"""Offline runner/DDL safety regressions. These do not claim PostgreSQL execution."""
import contextlib
import hashlib
import io
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
MIGRATION = ROOT / "db/migrations/012_user_sessions.sql"


class UserSessionMigrationTests(unittest.TestCase):
    def test_applied_011_should_remain_byte_for_byte_unchanged(self):
        self.assertEqual("de5bef9e79511b3a08a1ef8f4c346cb166ea303d92d26ae49dfdb2b2efc7eb31",
                         hashlib.sha256((ROOT / "db/migrations/011_human_sessions.sql").read_bytes()).hexdigest())

    def test_runner_should_preserve_all_prior_statement_boundaries(self):
        source = (ROOT / "deploy/db-apply.sh").read_text()
        runner = source.split("<<'PYEOF'\n", 1)[1].split("\nPYEOF", 1)[0]
        for migration in sorted((ROOT / "db/migrations").glob("*.sql")):
            if migration == MIGRATION:
                continue
            sql = re.sub(r"--[^\n]*", "", migration.read_text())
            expected = [part.strip() for part in sql.split(";") if part.strip()]
            calls = []

            def transport(args, **kwargs):
                calls.append(args[args.index("--sql") + 1])
                return subprocess.CompletedProcess(args, 0, "", "")

            with self.subTest(migration=migration.name), \
                    patch.object(sys, "argv", ["runner", str(migration), "fake-cluster", "fake-secret", "fake-db", "fake-region"]), \
                    patch("subprocess.run", transport), contextlib.redirect_stdout(io.StringIO()):
                exec(compile(runner, "db-apply.sh", "exec"), {})
            self.assertEqual(expected, calls)

    def test_runner_should_send_atomic_rename_as_one_statement_before_recording_migration(self):
        source = (ROOT / "deploy/db-apply.sh").read_text()
        runner = source.split("<<'PYEOF'\n", 1)[1].split("\nPYEOF", 1)[0]
        calls = []

        def transport(args, **kwargs):
            calls.append(args[args.index("--sql") + 1])
            return subprocess.CompletedProcess(args, 0, "", "")

        sql = "DO $migration$ BEGIN ALTER TABLE public.human_sessions RENAME TO user_sessions; END; $migration$;"
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as directory:
            fixture = Path(directory) / "migration.sql"
            fixture.write_text(sql)
            with patch.object(sys, "argv", ["runner", str(fixture), "fake-cluster", "fake-secret", "fake-db", "fake-region"]), \
                    patch("subprocess.run", transport), contextlib.redirect_stdout(io.StringIO()):
                exec(compile(runner, "db-apply.sh", "exec"), {})
        self.assertEqual(1, len(calls))
        self.assertTrue(calls[0].startswith("DO $migration$"))
        self.assertIn("ALTER TABLE public.human_sessions RENAME TO user_sessions;", calls[0])
        self.assertTrue(calls[0].endswith("$migration$;"))
        calls.clear()
        with patch.object(sys, "argv", ["runner", str(MIGRATION), "fake-cluster", "fake-secret", "fake-db", "fake-region"]), \
                patch("subprocess.run", transport), contextlib.redirect_stdout(io.StringIO()):
            exec(compile(runner, "db-apply.sh", "exec"), {})
        self.assertEqual(1, len(calls))
        self.assertIn("END;\n$migration$;", calls[0])
        self.assertLess(source.index("PYEOF\n  rds_exec \"INSERT"), source.index('echo "$NAME applied'))

    def test_migration_should_only_rename_and_fail_closed_on_ambiguous_or_missing_tables(self):
        sql = MIGRATION.read_text()
        self.assertIn("to_regclass('public.human_sessions') IS NOT NULL", sql)
        self.assertIn("to_regclass('public.user_sessions') IS NOT NULL", sql)
        self.assertIn("to_regclass('public.user_sessions') IS NULL", sql)
        self.assertIn("RAISE EXCEPTION", sql)
        self.assertIn("LOCK TABLE public.user_sessions IN ACCESS EXCLUSIVE MODE", sql)
        self.assertIn("conrelid = 'public.user_sessions'::regclass", sql)
        for suffix in ("pkey", "handle_hash_check", "encrypted_credential_check", "check"):
            self.assertIn(f"('human_sessions_{suffix}', 'user_sessions_{suffix}')", sql)
        self.assertIn("ALTER INDEX IF EXISTS public.human_sessions_expiry_idx RENAME TO user_sessions_expiry_idx", sql)
        self.assertNotRegex(re.sub(r"--[^\n]*", "", sql), r"(?i)\b(DROP|DELETE|TRUNCATE|INSERT|UPDATE|CREATE)\b")


if __name__ == "__main__":
    unittest.main()
