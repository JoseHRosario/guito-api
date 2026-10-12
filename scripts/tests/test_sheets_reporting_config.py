import importlib.util
from pathlib import Path
import unittest
from unittest import mock
import tempfile
import json
import subprocess
import sys

spec = importlib.util.spec_from_file_location("reporting_config", Path(__file__).resolve().parents[2] / "deploy/sheets-reporting-config.py")
assert spec is not None
config = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
spec.loader.exec_module(config)


class ReportingConfigurationTests(unittest.TestCase):
    def test_explicit_verified_inputs_enable_both_gates(self):
        result = config.select({}, {}, {"SHEETS_REPORTING_AUTHENTICATION": "enabled", "SHEETS_REPORTING_IDENTITY_VERIFIED": "true",
            "SHEETS_REPORTING_GOOGLE_AUDIENCE": "report-client", "SHEETS_REPORTING_GOOGLE_SUBJECT": "installer"}, "staging", "ui-client")
        self.assertEqual(result, ("report-client", "installer"))

    def test_routine_deploy_preserves_consistent_installed_reporting_identity(self):
        api = {"AppConfiguration__SheetsReportingAuthentication__Audience": "installed-client",
               "AppConfiguration__SheetsReportingAuthentication__Subject": "installed-subject",
               "AppConfiguration__Authentication__OAuthAudience": "ui-client"}
        authorizer = {"SHEETS_REPORTING_GOOGLE_AUDIENCE": "installed-client",
                      "SHEETS_REPORTING_GOOGLE_SUBJECT": "installed-subject", "GOOGLE_CLIENT_ID": "ui-client"}
        self.assertEqual(config.select(api, authorizer, {}, "staging", "ui-client"),
                         ("installed-client", "installed-subject"))


    def test_production_enablement_requires_live_verification_and_explicit_approval(self):
        requested = {"SHEETS_REPORTING_AUTHENTICATION": "enabled", "SHEETS_REPORTING_IDENTITY_VERIFIED": "true",
                     "SHEETS_REPORTING_GOOGLE_AUDIENCE": "report-client", "SHEETS_REPORTING_GOOGLE_SUBJECT": "installer",
                     "ALLOW_PRODUCTION_SHEETS_REPORTING": "true"}
        self.assertEqual(config.select({}, {}, requested, "production", "ui-client"), ("report-client", "installer"))

    def test_configuration_discovery_denied_aborts_but_missing_function_is_new_stack(self):
        with mock.patch.object(config.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", "AccessDeniedException")):
            with self.assertRaises(RuntimeError):
                config.current_variables("region", "function")
        with mock.patch.object(config.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", "ResourceNotFoundException")):
            self.assertEqual(config.current_variables("region", "function"), {})

    def test_cli_emits_exact_environment_contract_for_both_gates(self):
        requested = {"SHEETS_REPORTING_AUTHENTICATION": "enabled", "SHEETS_REPORTING_IDENTITY_VERIFIED": "true",
                     "SHEETS_REPORTING_GOOGLE_AUDIENCE": "report-client", "SHEETS_REPORTING_GOOGLE_SUBJECT": "installer",
                     "GOOGLE_CLIENT_ID": "ui-client"}
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(config, "current_variables", return_value={}), \
             mock.patch.dict(config.os.environ, requested, clear=True), mock.patch.object(sys, "argv", ["selector", "--region", "region",
                "--api", "api", "--authorizer", "authorizer", "--environment", "staging", "--output-dir", folder]):
            config.main()
            self.assertEqual(json.loads((Path(folder) / "guito-reporting-auth-env.json").read_text()),
                             {"SHEETS_REPORTING_GOOGLE_AUDIENCE": "report-client", "SHEETS_REPORTING_GOOGLE_SUBJECT": "installer"})
            self.assertEqual(json.loads((Path(folder) / "guito-reporting-api-env.json").read_text()),
                             {"AppConfiguration__SheetsReportingAuthentication__Audience": "report-client",
                              "AppConfiguration__SheetsReportingAuthentication__Subject": "installer"})

    def test_invalid_enablement_is_rejected(self):
        valid = {"SHEETS_REPORTING_AUTHENTICATION": "enabled", "SHEETS_REPORTING_IDENTITY_VERIFIED": "true",
                 "SHEETS_REPORTING_GOOGLE_AUDIENCE": "report-client", "SHEETS_REPORTING_GOOGLE_SUBJECT": "installer"}
        cases = [{**valid, "SHEETS_REPORTING_IDENTITY_VERIFIED": "false"}, {**valid, "SHEETS_REPORTING_GOOGLE_SUBJECT": ""},
                 {**valid, "SHEETS_REPORTING_GOOGLE_AUDIENCE": "ui-client"},
                 {**valid, "SHEETS_REPORTING_GOOGLE_AUDIENCE": " "},
                 {**valid, "SHEETS_REPORTING_AUTHENTICATION": "preserve"},
                 {"SHEETS_REPORTING_AUTHENTICATION": "unknown"}]
        for requested in cases:
            with self.subTest(requested=requested), self.assertRaises(ValueError):
                config.select({}, {}, requested, "staging", "ui-client")
        with self.assertRaises(ValueError):
            config.select({}, {}, valid, "production", "ui-client")

    def test_inconsistent_installed_gates_abort_instead_of_silently_enabling(self):
        with self.assertRaises(ValueError):
            config.select({}, {"SHEETS_REPORTING_GOOGLE_AUDIENCE": "report-client",
                               "SHEETS_REPORTING_GOOGLE_SUBJECT": "installer"}, {}, "staging", "ui-client")

    def test_new_environment_defaults_disabled_and_explicit_disable_clears_both(self):
        self.assertEqual(config.select({}, {}, {}, "staging", "ui-client"), ("", ""))
        self.assertEqual(config.select({}, {}, {"SHEETS_REPORTING_AUTHENTICATION": "disabled"}, "staging", "ui-client"), ("", ""))


if __name__ == "__main__":
    unittest.main()
