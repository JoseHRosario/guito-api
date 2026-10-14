"""Hermetic isolation policy and actual runner preflight (no AWS/network/dotnet)."""
import copy
import datetime as dt
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("isolation", ROOT / "scripts/validate-staging-isolation.py")
assert spec is not None and spec.loader is not None
isolation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(isolation)


class StagingIsolationTests(unittest.TestCase):
    def setUp(self):
        self.now = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)
        self.gate: dict[str, Any] = dict(environment="staging", baseUrl="https://staging.example", approvedBy="operator",
                         expiresAt="2026-01-01T01:00:00Z", category="Existing category", disposable=True,
                         noPersonalData=True, exclusiveDatabaseCredentials=True, syntheticCreateDeleteApproved=True,
                         resourceArn="cluster", databaseName="disposable", secretArn="disposable-secret",
                         revisionId="inspected-revision", codeSha256="inspected-code")
        self.staging: dict[str, Any] = dict(RevisionId="inspected-revision", CodeSha256="inspected-code", Environment={"Variables": {
            "Database__ResourceArn": "cluster", "Database__DatabaseName": "disposable", "Database__SecretArn": "disposable-secret"}})
        self.production: dict[str, Any] = {"Environment": {"Variables": {
            "Database__ResourceArn": "cluster", "Database__DatabaseName": "personal", "Database__SecretArn": "personal-secret"}}}

    def validate(self):
        return isolation.validate(self.gate, self.staging, self.production, "https://staging.example", self.now)

    def test_all_attestations_and_exact_installed_target_are_required(self):
        self.assertEqual(json.loads(self.validate()), self.gate)
        for key in tuple(self.gate):
            with self.subTest(key=key):
                saved = self.gate.pop(key)
                with self.assertRaises((ValueError, KeyError)):
                    self.validate()
                self.gate[key] = saved

    def test_false_or_string_boolean_attestations_are_rejected(self):
        for key in ("disposable", "noPersonalData", "exclusiveDatabaseCredentials", "syntheticCreateDeleteApproved"):
            for value in (False, "true", 1):
                with self.subTest(key=key, value=value):
                    self.gate[key] = value
                    with self.assertRaises(ValueError):
                        self.validate()
                    self.gate[key] = True

    def test_expired_unbounded_or_naive_attestations_are_rejected(self):
        for expiry in ("2025-12-31T23:59:59Z", "2026-01-02T01:00:00Z", "2026-01-01T01:00:00"):
            with self.subTest(expiry=expiry):
                self.gate["expiresAt"] = expiry
                with self.assertRaises(ValueError):
                    self.validate()

    def test_code_config_or_origin_drift_is_rejected(self):
        for key in ("baseUrl", "revisionId", "codeSha256", "resourceArn", "databaseName", "secretArn", "environment"):
            with self.subTest(key=key):
                saved = self.gate[key]
                self.gate[key] = "changed"
                with self.assertRaises(ValueError):
                    self.validate()
                self.gate[key] = saved

    def test_same_production_database_or_credentials_are_rejected(self):
        for key in ("Database__DatabaseName", "Database__SecretArn"):
            with self.subTest(key=key):
                saved = copy.deepcopy(self.production)
                self.production["Environment"]["Variables"][key] = self.staging["Environment"]["Variables"][key]
                with self.assertRaises(ValueError):
                    self.validate()
                self.production = saved

    def test_missing_explicit_installed_values_fail_closed(self):
        for config in (self.staging, self.production):
            for key in tuple(config["Environment"]["Variables"]):
                with self.subTest(key=key):
                    saved = config["Environment"]["Variables"].pop(key)
                    with self.assertRaises(ValueError):
                        self.validate()
                    config["Environment"]["Variables"][key] = saved

    def test_runner_rejects_unsafe_modes_before_any_external_command(self):
        for arguments in (("production", "round-trip"), ("staging", "round-trip"), ("staging", "invalid")):
            with self.subTest(arguments=arguments):
                result = subprocess.run(["bash", str(ROOT / "scripts/run-deployed-tests.sh"), *arguments],
                                        env={"PATH": os.defpath, "HOME": str(ROOT)}, capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("FATAL" if arguments[1] != "invalid" else "Usage", result.stderr)

    def test_runner_read_only_filters_both_suites_with_offline_transports(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.make_executable(root / "aws", '''#!/bin/sh
case "$*" in
  *get-api-mappings*) printf '0' ;;
  *ApiEndpoint*) printf 'https://staging.example' ;;
  *ApiId*) printf 'test-api' ;;
  *GOOGLE_CLIENT_ID*) printf 'test-client' ;;
  *get-parameter*) printf '{"ApiKeys":["offline-key"]}' ;;
  *) exit 90 ;;
esac
''')
            self.make_executable(root / "python3", "#!/bin/sh\n# Token/key transports are synthetic: never contact Google.\nwhile IFS= read -r line; do :; done\nprintf 'offline-credential'\n")
            self.make_executable(root / "curl", "#!/bin/sh\nwhile IFS= read -r line; do :; done\nprintf 200\n")
            self.make_executable(root / "dotnet", '#!/bin/sh\nprintf "%s\\n" "$*" >> "$RECORD"\n')
            env = {"PATH": f"{root}:{os.defpath}", "HOME": folder, "RECORD": str(root / "calls")}
            for target in ("production", "staging"):
                result = subprocess.run(["bash", str(ROOT / "scripts/run-deployed-tests.sh"), target, "read-only"],
                                        env=env, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
            calls = (root / "calls").read_text().splitlines()
            self.assertEqual(len(calls), 4)
            self.assertTrue(all("--filter Category=Integration&LiveMutation!=true" in call for call in calls))
            # A transport or business-read failure must not count as a warmed app.
            self.make_executable(root / "curl", "#!/bin/sh\nwhile IFS= read -r line; do :; done\nprintf 503\n")
            self.make_executable(root / "sleep", "#!/bin/sh\nexit 0\n")
            (root / "calls").unlink()
            result = subprocess.run(["bash", str(ROOT / "scripts/run-deployed-tests.sh"), "production", "read-only"],
                                    env=env, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("warm-up never reached", result.stderr)
            self.assertFalse((root / "calls").exists())
            # Failure of one suite still runs the other and fails the overall gate.
            self.make_executable(root / "curl", "#!/bin/sh\nwhile IFS= read -r line; do :; done\nprintf 200\n")
            self.make_executable(root / "dotnet", '#!/bin/sh\nprintf "%s\\n" "$*" >> "$RECORD"\nexit 1\n')
            result = subprocess.run(["bash", str(ROOT / "scripts/run-deployed-tests.sh"), "production", "read-only"],
                                    env=env, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(len((root / "calls").read_text().splitlines()), 2)

    @staticmethod
    def make_executable(path, content):
        path.write_text(content)
        path.chmod(0o755)


if __name__ == "__main__":
    unittest.main()
