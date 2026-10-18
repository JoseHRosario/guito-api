"""Offline direct-create/runner regressions; not PostgreSQL execution evidence."""
import contextlib
import io
from pathlib import Path
import re
import subprocess
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
MIGRATION = ROOT / "db/migrations/011_user_sessions.sql"


class UserSessionMigrationTests(unittest.TestCase):
    def test_feature_should_have_one_direct_user_session_migration(self):
        migrations = sorted((ROOT / "db/migrations").glob("*.sql"))
        session_migrations = [p for p in migrations if "sessions" in p.read_text()]
        self.assertEqual([MIGRATION], session_migrations)
        self.assertFalse((MIGRATION.parent / "011_human_sessions.sql").exists())
        self.assertFalse((MIGRATION.parent / "012_user_sessions.sql").exists())
        solution = (ROOT / "guito-api.slnx").read_text()
        self.assertEqual(1, solution.count('Path="db/migrations/011_user_sessions.sql"'))
        self.assertNotIn("011_human_sessions.sql", solution)
        self.assertNotIn("012_user_sessions.sql", solution)

    def test_runner_should_preserve_all_simple_statement_boundaries(self):
        source = (ROOT / "deploy/db-apply.sh").read_text()
        runner = source.split("<<'PYEOF'\n", 1)[1].split("\nPYEOF", 1)[0]
        for migration in sorted((ROOT / "db/migrations").glob("*.sql")):
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
            if migration == MIGRATION:
                self.assertEqual(2, len(calls))
                self.assertTrue(calls[0].startswith("CREATE TABLE IF NOT EXISTS user_sessions"))
                self.assertTrue(calls[1].startswith("CREATE INDEX IF NOT EXISTS user_sessions_expiry_idx"))
        self.assertNotIn("atomic_block", runner)
        self.assertLess(source.index("PYEOF\n  rds_exec \"INSERT"), source.index('echo "$NAME applied'))

    def test_runner_should_stop_on_failed_statement_before_index(self):
        source = (ROOT / "deploy/db-apply.sh").read_text()
        runner = source.split("<<'PYEOF'\n", 1)[1].split("\nPYEOF", 1)[0]
        with patch.object(sys, "argv", ["runner", str(MIGRATION), "fake-cluster", "fake-secret", "fake-db", "fake-region"]), \
                patch("subprocess.run", return_value=subprocess.CompletedProcess([], 1, "", "fake DDL failure")) as transport:
            with self.assertRaisesRegex(SystemExit, "fake DDL failure"):
                exec(compile(runner, "db-apply.sh", "exec"), {})
        self.assertEqual(1, transport.call_count)
        self.assertIn("set -euo pipefail", source)

    def test_direct_create_should_retain_crypto_and_lease_constraints(self):
        sql = re.sub(r"--[^\n]*", "", MIGRATION.read_text())
        self.assertIn("handle_hash text PRIMARY KEY CHECK (handle_hash ~ '^[A-F0-9]{64}$')", sql)
        self.assertIn("encrypted_credential text NOT NULL CHECK (encrypted_credential LIKE 'v1.%')", sql)
        for column in ("subject text NOT NULL", "email text NOT NULL", "expires_at timestamptz NOT NULL",
                       "refresh_lease text", "lease_expires_at timestamptz"):
            self.assertIn(column, sql)
        self.assertIn("CHECK ((refresh_lease IS NULL) = (lease_expires_at IS NULL))", sql)
        self.assertIn("CREATE INDEX IF NOT EXISTS user_sessions_expiry_idx ON user_sessions (expires_at)", sql)
        self.assertNotRegex(sql, r"(?i)\b(DO|ALTER|RENAME|DROP|DELETE|TRUNCATE|INSERT|UPDATE)\b")


if __name__ == "__main__":
    unittest.main()
