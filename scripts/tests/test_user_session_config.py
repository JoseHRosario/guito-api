"""Hermetic public-CLI tests for session deployment; no AWS invocation or live keys."""
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "deploy" / "user-session-config.py"


class UserSessionDeploymentTests(unittest.TestCase):
    def test_configuration_should_emit_credentialed_exact_origins_for_each_environment(self):
        for environment, expected in (
            ("Production", "https://guito.web.kerumirembora.com"),
            ("Staging", "https://guito-staging.web.kerumirembora.com"),
        ):
            with self.subTest(environment=environment), tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", os.environ.get("RUNNER_TEMP"))) as directory:
                result = subprocess.run([sys.executable, str(HELPER), "--environment", environment, "--output-dir", directory], capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stderr)
                cors = json.loads((Path(directory) / "user-session-cors.json").read_text())
                variables = json.loads((Path(directory) / "user-session-env.json").read_text())
                self.assertEqual([expected], cors["AllowOrigins"])
                self.assertIs(True, cors["AllowCredentials"])
                self.assertIn("x-guito-session", cors["AllowHeaders"])
                self.assertIn("if-match", cors["AllowHeaders"])
                self.assertIn("DELETE", cors["AllowMethods"])
                self.assertEqual({
                    "AppConfiguration__Cors__AllowedOrigins__0": expected,
                    "AppConfiguration__UserSessions__TableName": "guito-api-user-sessions" + ("-staging" if environment == "Staging" else ""),
                    "AppConfiguration__UserSessions__Region": "eu-west-1",
                }, variables)

    def test_source_session_configuration_should_reject_wrong_or_missing_environment_settings(self):
        import shutil
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", os.environ.get("RUNNER_TEMP"))) as directory:
            root = Path(directory)
            (root / 'deploy').mkdir()
            source = root / 'src/guito-api'
            source.mkdir(parents=True)
            shutil.copyfile(HELPER, root / 'deploy/user-session-config.py')
            (source / 'appsettings.json').write_text(json.dumps({'AppConfiguration': {'Cors': {'AllowedOrigins': ['https://test.example']}}}))
            for settings in (None, {}, {'TableName': 'guito-api-user-sessions', 'Region': 'eu-west-1'},
                             {'TableName': 'guito-api-user-sessions-staging', 'Region': 'us-east-1'},
                             {'TableName': 'guito-api-user-sessions-staging', 'Region': 'eu-west-1', 'Store': 'Postgres'}):
                with self.subTest(settings=settings):
                    overlay = {'AppConfiguration': {}}
                    if settings is not None:
                        overlay['AppConfiguration']['UserSessions'] = settings
                    (source / 'appsettings.Staging.json').write_text(json.dumps(overlay))
                    result = subprocess.run([sys.executable, str(root / 'deploy/user-session-config.py'),
                                             '--environment', 'Staging', '--output-dir', directory], capture_output=True, text=True)
                    self.assertNotEqual(0, result.returncode)
                    self.assertFalse((root / 'user-session-env.json').exists())
            for environment, table in (('Staging', 'guito-api-user-sessions-staging'), ('Production', 'guito-api-user-sessions')):
                (source / f'appsettings.{environment}.json').write_text(json.dumps({'AppConfiguration': {
                    'UserSessions': {'TableName': table, 'Region': 'eu-west-1'}}}))
                result = subprocess.run([sys.executable, str(root / 'deploy/user-session-config.py'),
                                         '--environment', environment, '--output-dir', directory], capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stderr)
                variables = json.loads((root / 'user-session-env.json').read_text())
                self.assertEqual(table, variables['AppConfiguration__UserSessions__TableName'])
                self.assertEqual('eu-west-1', variables['AppConfiguration__UserSessions__Region'])

    def test_key_validation_should_accept_only_base64_32_byte_fake_key_without_output(self):
        payload = json.dumps({"UserSessionEncryptionKey": base64.b64encode(bytes(range(32))).decode()})
        result = subprocess.run([sys.executable, str(HELPER), "--validate-key"], input=payload, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)
        self.assertEqual("", result.stderr)

    def test_key_validation_should_reject_missing_malformed_and_wrong_length_without_echoing_payload(self):
        for payload in ("{}", "not-json", '"not-object"', "[]", json.dumps({"UserSessionEncryptionKey": "fake-sensitive-invalid-key"}), json.dumps({"UserSessionEncryptionKey": base64.b64encode(bytes(31)).decode()})):
            with self.subTest(payload_shape=payload[:1]):
                result = subprocess.run([sys.executable, str(HELPER), "--validate-key"], input=payload, capture_output=True, text=True)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertNotIn(payload, result.stderr)
                self.assertNotIn("fake-sensitive-invalid-key", result.stderr)
                self.assertNotIn("Traceback", result.stderr)

    def test_key_validation_should_require_valid_canonical_field_and_reject_old_only_payload(self):
        key = base64.b64encode(bytes(range(32))).decode()
        for fields, accepted in (
            ({"UserSessionEncryptionKey": key}, True),
            ({"usersessionencryptionkey": key}, True),
            ({"HumanSessionEncryptionKey": key}, False),
            ({}, False),
            *[({"UserSessionEncryptionKey": value}, False)
              for value in ("invalid", "", None, 42, " " + key, base64.b64encode(bytes(31)).decode())],
        ):
            with self.subTest(fields=list(fields), accepted=accepted):
                result = subprocess.run([sys.executable, str(HELPER), "--validate-key"],
                                        input=json.dumps(fields), capture_output=True, text=True)
                self.assertEqual(accepted, result.returncode == 0, result.stderr)
                self.assertEqual("", result.stdout)
                self.assertNotIn(key, result.stderr)
                self.assertNotIn("Traceback", result.stderr)

    def test_environment_should_reject_unspecified_or_unknown_target(self):
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", os.environ.get("RUNNER_TEMP"))) as directory:
            for environment in ("", "Development", "prod"):
                args = [sys.executable, str(HELPER), "--output-dir", directory]
                if environment:
                    args += ["--environment", environment]
                result = subprocess.run(args, capture_output=True, text=True)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse((Path(directory) / "user-session-cors.json").exists())

    def test_deploy_should_reconcile_public_post_routes_and_retain_custom_business_gate(self):
        source = (ROOT / "deploy" / "deploy.sh").read_text()
        self.assertIn("for operation in token refresh logout; do", source)
        self.assertIn('route="POST /Auth/session/$operation"', source)
        self.assertIn('--authorization-type NONE --target "integrations/$INT_ID"', source)
        self.assertIn('--authorization-type CUSTOM --authorizer-id "$AUTH_ID" --target "integrations/$INT_ID"', source)
        self.assertIn('--cors-configuration "file://$DEPLOY_WORK_DIR/user-session-cors.json" >/dev/null\n', source)
        self.assertNotIn('"AllowOrigins":["*"]', source)
        result = subprocess.run(["bash", "-n", str(ROOT / "deploy" / "deploy.sh")], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
