import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("expense_cutover_config", ROOT / "deploy/expense-cutover-config.py")
assert spec is not None and spec.loader is not None
config = importlib.util.module_from_spec(spec)
spec.loader.exec_module(config)


class ExpenseCutoverConfigurationTests(unittest.TestCase):
    def test_deploy_entrypoint_preserves_authority_pause_and_reporting_in_emitted_payloads(self):
        installed = {
            "guito-api": {config.STORE: "Postgres", config.APPROVED: "true", config.PAUSED: "true",
                "AppConfiguration__SheetsReportingAuthentication__Audience": "report-client",
                "AppConfiguration__SheetsReportingAuthentication__Subject": "owner",
                "AppConfiguration__Authentication__OAuthAudience": "ui-client", "Unrelated": "retained"},
            "guito-api-authorizer": {"SHEETS_REPORTING_GOOGLE_AUDIENCE": "report-client",
                "SHEETS_REPORTING_GOOGLE_SUBJECT": "owner", "GOOGLE_CLIENT_ID": "ui-client"}}
        payloads = self.run_deploy_until_config(installed, {})
        for name, expected in installed.items():
            for key, value in expected.items():
                self.assertEqual(payloads[name][key], value)

    def test_deploy_entrypoint_enables_authority_and_identity_with_separate_approvals(self):
        payloads = self.run_deploy_until_config({"guito-api": {config.STORE: "Sheets", config.PAUSED: "true"}}, {"EXPENSE_STORE": "Postgres",
            "EXPENSE_WRITES_PAUSED": "true", "ALLOW_PRODUCTION_EXPENSE_POSTGRES": "true",
            "SHEETS_REPORTING_AUTHENTICATION": "enabled", "ALLOW_PRODUCTION_SHEETS_REPORTING": "true",
            "SHEETS_REPORTING_IDENTITY_VERIFIED": "true", "SHEETS_REPORTING_GOOGLE_AUDIENCE": "report-client",
            "SHEETS_REPORTING_GOOGLE_SUBJECT": "owner", "GOOGLE_CLIENT_ID": "ui-client"})
        self.assertEqual(payloads["guito-api"][config.STORE], "Postgres")
        self.assertEqual(payloads["guito-api"][config.APPROVED], "true")
        self.assertEqual(payloads["guito-api"][config.PAUSED], "true")
        self.assertEqual(payloads["guito-api"]["AppConfiguration__SheetsReportingAuthentication__Audience"], "report-client")
        self.assertEqual(payloads["guito-api-authorizer"]["SHEETS_REPORTING_GOOGLE_SUBJECT"], "owner")

    def run_deploy_until_config(self, installed, requested):
        # Every provider command is fake; inspect the real shell's emitted config,
        # then deliberately stop before gateway provisioning. No real deployment.
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as folder:
            base = Path(folder)
            (base / "installed.json").write_text(json.dumps(installed))
            fake_aws = base / "aws"
            fake_aws.write_text('''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
base = pathlib.Path(os.environ['FAKE_DEPLOY_DIR'])
if 'get-caller-identity' in args:
    print('497087877832')
elif 'get-function-configuration' in args:
    name = args[args.index('--function-name') + 1]
    print(json.dumps(json.loads((base / 'installed.json').read_text()).get(name, {})))
elif 'update-function-configuration' in args and '--environment' in args:
    name = args[args.index('--function-name') + 1]
    path = args[args.index('--environment') + 1][7:]
    (base / (name + '.json')).write_text(pathlib.Path(path).read_text())
elif 'get-apis' in args:
    sys.exit('FAKE_STOP_AFTER_CONFIG')
elif 'describe-secret' in args:
    print('fake-db-secret')
else:
    print('{}')
''')
            fake_aws.chmod(0o755)
            fake_dotnet = base / "dotnet"
            fake_dotnet.write_text("#!/usr/bin/env python3\nimport pathlib, sys\npathlib.Path(sys.argv[sys.argv.index('-o') + 1]).mkdir()\n")
            fake_dotnet.chmod(0o755)
            fake_zip = base / "zip"
            fake_zip.write_text("#!/usr/bin/env bash\nexit 0\n")
            fake_zip.chmod(0o755)
            result = subprocess.run(["bash", str(ROOT / "deploy/deploy.sh")], cwd=ROOT, capture_output=True, text=True,
                env={"PATH": folder + os.pathsep + os.environ["PATH"], "HOME": os.environ["HOME"], "TMPDIR": folder,
                     "FAKE_DEPLOY_DIR": folder, "ENV": "production", "PYTHONDONTWRITEBYTECODE": "1", **requested})
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("FAKE_STOP_AFTER_CONFIG", result.stderr, result.stdout + result.stderr)
            return {name: json.loads((base / (name + '.json')).read_text())["Variables"]
                    for name in ("guito-api", "guito-api-authorizer")}

    def run_cli(self, installed, requested, environment="production"):
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as folder, \
             mock.patch.object(config, "current_variables", return_value=installed), \
             mock.patch.dict(config.os.environ, requested, clear=True), \
             mock.patch.object(sys, "argv", ["selector", "--region", "region", "--api", "api", "--environment", environment, "--output-dir", folder]):
            config.main()
            return json.loads((Path(folder) / "guito-expense-api-env.json").read_text())

    def test_cli_defaults_to_sheets_and_denied_approval(self):
        self.assertEqual(self.run_cli({}, {}), {
            "AppConfiguration__ExpenseStore": "Sheets",
            "AppConfiguration__ExpenseCutover__ProductionPostgresApproved": "false",
            "AppConfiguration__ExpenseCutover__WritesPaused": "false"})

    def test_cli_refuses_first_production_switch_when_pause_only_requested(self):
        requested = {"EXPENSE_STORE": "Postgres", "ALLOW_PRODUCTION_EXPENSE_POSTGRES": "true", "EXPENSE_WRITES_PAUSED": "true"}
        for installed in ({}, {config.STORE: "Sheets", config.PAUSED: "false"}):
            with self.subTest(installed=installed), self.assertRaises(SystemExit):
                self.run_cli(installed, requested)

    def test_cli_refuses_first_production_switch_when_installed_pause_removed(self):
        with self.assertRaises(SystemExit):
            self.run_cli({config.PAUSED: "true"}, {"EXPENSE_STORE": "Postgres",
                "ALLOW_PRODUCTION_EXPENSE_POSTGRES": "true", "EXPENSE_WRITES_PAUSED": "false"})

    def test_cli_enables_production_when_installed_pause_remains_with_explicit_approval(self):
        for pause in ("preserve", "true"):
            with self.subTest(pause=pause):
                self.assertEqual(self.run_cli({config.STORE: "Sheets", config.PAUSED: "true"}, {
                    "EXPENSE_STORE": "Postgres", "ALLOW_PRODUCTION_EXPENSE_POSTGRES": "true", "EXPENSE_WRITES_PAUSED": pause}), {
                    "AppConfiguration__ExpenseStore": "Postgres",
                    "AppConfiguration__ExpenseCutover__ProductionPostgresApproved": "true",
                    "AppConfiguration__ExpenseCutover__WritesPaused": "true"})

    def test_cli_preserves_installed_postgres_approval_and_pause_without_repeat_approval(self):
        installed = {config.STORE: "Postgres", config.APPROVED: "true", config.PAUSED: "true"}
        self.assertEqual(self.run_cli(installed, {}), installed)
        self.assertEqual(self.run_cli(installed, {"EXPENSE_WRITES_PAUSED": "false"}), {**installed, config.PAUSED: "false"})

    def test_cli_refuses_unapproved_production_and_malformed_inputs(self):
        cases = [({}, {"EXPENSE_STORE": "Postgres"}),
                 ({}, {"EXPENSE_STORE": "Postgres", "ALLOW_PRODUCTION_EXPENSE_POSTGRES": "true"}),
                 ({config.STORE: "Postgres"}, {}),
                 ({config.STORE: "Invalid"}, {}),
                 ({config.APPROVED: "TRUE"}, {}),
                 ({config.PAUSED: ""}, {}),
                 ({}, {"EXPENSE_STORE": ""}),
                 ({}, {"EXPENSE_STORE": "postgres"}),
                 ({}, {"EXPENSE_WRITES_PAUSED": "yes"}),
                 ({}, {"ALLOW_PRODUCTION_EXPENSE_POSTGRES": "TRUE"})]
        for installed, requested in cases:
            with self.subTest(installed=installed, requested=requested), self.assertRaises(SystemExit):
                self.run_cli(installed, requested)

    def test_cli_refuses_stale_sheets_fallback_even_with_approval(self):
        for environment in ("staging", "production"):
            with self.subTest(environment=environment), self.assertRaises(SystemExit):
                self.run_cli({config.STORE: "Postgres", config.APPROVED: "true"},
                             {"EXPENSE_STORE": "Sheets", "ALLOW_PRODUCTION_EXPENSE_POSTGRES": "true"}, environment)

    def test_cli_staging_postgres_does_not_grant_production_approval(self):
        self.assertEqual(self.run_cli({}, {"EXPENSE_STORE": "Postgres"}, "staging")[config.APPROVED], "false")

    def test_cli_discovery_denied_aborts_without_output(self):
        with mock.patch.object(config, "current_variables", side_effect=RuntimeError("AccessDenied")), \
             self.assertRaises(SystemExit):
            with mock.patch.object(sys, "argv", ["selector", "--region", "region", "--api", "api", "--environment", "production", "--output-dir", "."]):
                config.main()

    def test_deploy_entrypoint_refuses_unapproved_postgres_before_mutations(self):
        # Execute the real shell entrypoint with an AWS executable that can only read.
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as folder:
            fake = Path(folder) / "aws"
            fake.write_text("#!/usr/bin/env python3\nimport sys\nargs = sys.argv\nif 'get-caller-identity' in args: print('497087877832')\nelif 'get-function-configuration' in args: print('{}')\nelse: raise RuntimeError('Unexpected AWS mutation: ' + ' '.join(args))\n")
            fake.chmod(0o755)
            result = subprocess.run(["bash", str(ROOT / "deploy/deploy.sh")], capture_output=True, text=True,
                env={"PATH": folder + os.pathsep + os.environ["PATH"], "HOME": os.environ["HOME"], "TMPDIR": folder,
                     "ENV": "production", "EXPENSE_STORE": "Postgres"})
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("explicit execution approval", result.stderr)
            self.assertNotIn("Unexpected AWS mutation", result.stderr)


if __name__ == "__main__":
    unittest.main()
