"""Exercise deployment's environment update boundary with an offline AWS transport."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class DeployEnvironmentTests(unittest.TestCase):
    def apply_environment(self, installed, requested=None):
        # Run the actual deploy environment section, not a reimplementation of its merge.
        deploy = (Path(__file__).resolve().parents[2] / "deploy/deploy.sh").read_text()
        section = "# --- 3b. Function environment variables" + deploy.split("# --- 3b. Function environment variables", 1)[1]
        section = section.split('[ -n "${GOOGLE_CLIENT_ID:-}" ]', 1)[0]
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            state = {"guito-api": {"AppConfiguration__Authentication__OAuthAudience": "ui-client",
                                   "UNRELATED": "keep", **installed},
                     "guito-api-authorizer": {"GOOGLE_CLIENT_ID": "ui-client", "UNRELATED": "keep"}}
            (root / "state.json").write_text(json.dumps(state))
            fake = root / "aws"
            fake.write_text('''#!/usr/bin/env python3
import json, os, pathlib, sys
root = pathlib.Path(os.environ['DEPLOY_WORK_DIR'])
state = json.loads((root / 'state.json').read_text())
a = sys.argv[1:]
name = a[a.index('--function-name') + 1]
if 'get-function-configuration' in a:
    print(json.dumps(state[name]))
elif 'update-function-configuration' in a:
    source = a[a.index('--environment') + 1].removeprefix('file://')
    state[name] = json.loads(pathlib.Path(source).read_text())['Variables']
    (root / 'state.json').write_text(json.dumps(state))
elif 'wait' not in a:
    raise SystemExit('Unexpected AWS operation')
''')
            fake.chmod(0o755)
            (root / "guito-reporting-api-env.json").write_text(json.dumps({
                "AppConfiguration__SheetsReportingAuthentication__Audience": "report-client",
                "AppConfiguration__SheetsReportingAuthentication__Subject": "installer"}))
            (root / "guito-reporting-auth-env.json").write_text(json.dumps({
                "SHEETS_REPORTING_GOOGLE_AUDIENCE": "report-client",
                "SHEETS_REPORTING_GOOGLE_SUBJECT": "installer"}))
            helper = Path(__file__).resolve().parents[2] / "deploy/human-session-config.py"
            prepared = subprocess.run(["python3", str(helper), "--environment", "Production", "--output-dir", folder], capture_output=True, text=True)
            self.assertEqual(prepared.returncode, 0, prepared.stderr)
            env = {"PATH": f"{root}:{os.defpath}", "DEPLOY_WORK_DIR": folder,
                   "SECRET_NAME": "/guito-api/prod", "ASPNETCORE_ENV": "Production",
                   "GOOGLE_CLIENT_ID": "", "GOOGLE_ALLOWED_EMAILS": ""}
            if requested is not None:
                env["EXPENSE_STORE"] = requested
            result = subprocess.run(["bash", "-eu", "-c",
                                     'REGION=eu-west-1; API_NAME=guito-api; AUTH_NAME=guito-api-authorizer;\n' + section],
                                    env=env, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            return json.loads((root / "state.json").read_text())

    def test_routine_deploy_removes_retired_persistence_config_preserving_other_variables(self):
        for store in ("Sheets", "Postgres"):
            with self.subTest(store=store):
                state = self.apply_environment({
                    "AppConfiguration__ExpenseStore": store,
                    "AppConfiguration__Googlesheets__FilePath": "retired.json",
                    "AppConfiguration__Googlesheets__CategoriesRange": "retired",
                    "Database__DatabaseName": "preserve-database"})
                api = state["guito-api"]
                self.assertNotIn("AppConfiguration__ExpenseStore", api)
                self.assertFalse(any(k.lower().startswith("appconfiguration__googlesheets__") for k in api))
                self.assertEqual(api["Database__DatabaseName"], "preserve-database")
                self.assertEqual(api["UNRELATED"], "keep")
                self.assertEqual(state["guito-api-authorizer"]["UNRELATED"], "keep")
                self.assertEqual(api["ASPNETCORE_ENVIRONMENT"], "Production")

    def test_session_origins_replace_stale_wildcards_without_changing_other_configuration(self):
        api = self.apply_environment({
            "AppConfiguration__Cors__AllowedOrigins": "*",
            "AppConfiguration__Cors__AllowedOrigins__0": "https://stale.example",
            "AppConfiguration__Cors__AllowedOrigins__7": "https://unexpected.example",
            "UNRELATED": "keep"})["guito-api"]
        self.assertNotIn("AppConfiguration__Cors__AllowedOrigins", api)
        self.assertNotIn("AppConfiguration__Cors__AllowedOrigins__7", api)
        self.assertEqual(api["AppConfiguration__Cors__AllowedOrigins__0"], "https://guito.web.kerumirembora.com")
        self.assertEqual(api["UNRELATED"], "keep")
        self.assertFalse(any("encrypt" in key.lower() for key in api))

    def test_retired_shell_selector_cannot_reinstall_override(self):
        api = self.apply_environment({}, "Sheets")["guito-api"]
        self.assertNotIn("AppConfiguration__ExpenseStore", api)

    def test_reporting_selector_outputs_reach_both_gates_without_changing_human_auth(self):
        state = self.apply_environment({"AppConfiguration__ExpenseStore": "Postgres"})
        self.assertEqual(state["guito-api"]["AppConfiguration__SheetsReportingAuthentication__Audience"], "report-client")
        self.assertEqual(state["guito-api"]["AppConfiguration__SheetsReportingAuthentication__Subject"], "installer")
        self.assertEqual(state["guito-api-authorizer"]["SHEETS_REPORTING_GOOGLE_AUDIENCE"], "report-client")
        self.assertEqual(state["guito-api-authorizer"]["SHEETS_REPORTING_GOOGLE_SUBJECT"], "installer")
        self.assertEqual(state["guito-api"]["AppConfiguration__Authentication__OAuthAudience"], "ui-client")
        self.assertEqual(state["guito-api-authorizer"]["GOOGLE_CLIENT_ID"], "ui-client")


if __name__ == "__main__":
    unittest.main()
