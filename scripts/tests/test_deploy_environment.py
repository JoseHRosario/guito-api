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

    def test_routine_production_deploy_preserves_postgres_and_other_variables(self):
        state = self.apply_environment({"AppConfiguration__ExpenseStore": "Postgres"})
        self.assertEqual(state["guito-api"]["AppConfiguration__ExpenseStore"], "Postgres")
        self.assertEqual(state["guito-api"]["UNRELATED"], "keep")
        self.assertEqual(state["guito-api-authorizer"]["UNRELATED"], "keep")
        self.assertEqual(state["guito-api"]["ASPNETCORE_ENVIRONMENT"], "Production")

    def test_omitted_selector_does_not_switch_existing_sheets_or_install_override(self):
        for installed in ({}, {"AppConfiguration__ExpenseStore": "Sheets"}):
            with self.subTest(installed=installed):
                api = self.apply_environment(installed)["guito-api"]
                self.assertEqual(api.get("AppConfiguration__ExpenseStore"), installed.get("AppConfiguration__ExpenseStore"))

    def test_existing_explicit_selector_can_set_production_postgres(self):
        api = self.apply_environment({"AppConfiguration__ExpenseStore": "Sheets"}, "Postgres")["guito-api"]
        self.assertEqual(api["AppConfiguration__ExpenseStore"], "Postgres")

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
