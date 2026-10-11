"""Hermetic safety regression checks for the registered premerge dispatch path."""
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class HistoricalImportWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.caller = (ROOT / '.github/workflows/ci-cd.yml').read_text()
        self.child = (ROOT / '.github/workflows/historical-import-rehearsal.yml').read_text()

    def test_rehearsal_requires_manual_staging_opt_in_and_successful_deploy(self):
        job = self.caller.split('  historical-import:\n', 1)[1].split('  deploy-production:', 1)[0]
        self.assertIn('needs: deploy-staging', job)
        self.assertIn("if: github.event_name == 'workflow_dispatch' && inputs.environment == 'staging' && inputs.rehearse_history", job)
        self.assertIn('uses: ./.github/workflows/historical-import-rehearsal.yml', job)
        self.assertIn('type: boolean\n        default: false', self.caller)
        self.assertIn('  workflow_call:', self.child)
        self.assertIn('environment: staging', self.child)
        self.assertNotIn('environment: production', self.child)

    def test_both_workflows_advertise_unpadded_month_day_convention(self):
        self.assertIn('MM/dd/yyyy|M/d/yyyy)', self.caller)
        self.assertIn("'MM/dd/yyyy', 'M/d/yyyy'", self.child)
        self.assertNotIn('no grouping', self.child)

    def test_child_does_not_wait_on_parent_staging_lane(self):
        self.assertIn("github.workflow == 'ci-cd' && format('historical-import-child-{0}', github.run_id) || 'ci-cd-staging'", self.child)
        self.assertIn('cancel-in-progress: false', self.caller)
        self.assertIn('cancel-in-progress: false', self.child)
        self.assertIn('secrets.IMPORT_ARCHIVE_PASSWORD', self.child)
        self.assertNotIn('secrets: inherit', self.caller)

    def test_preflight_rejects_production_and_missing_or_invalid_conventions(self):
        step = self.caller.split('      - name: Validate manual rehearsal request', 1)[1].split('      - uses:', 1)[0]
        script = step.split('        run: |\n', 1)[1]
        for target, row, separator, date, expected in [
            ('staging', '123', '.', 'yyyy-MM-dd', 0),
            ('staging', '123', ',', 'dd/MM/yyyy', 0),
            ('staging', '987', '.', 'M/d/yyyy', 0),
            ('staging', '987', '.', 'M/d/yy', 1),
            ('production', '123', '.', 'yyyy-MM-dd', 1),
            ('staging', '', '.', 'yyyy-MM-dd', 1),
            ('staging', '0', '.', 'yyyy-MM-dd', 1),
            ('staging', '123', '', 'yyyy-MM-dd', 1),
            ('staging', '123', '.', '', 1),
        ]:
            with self.subTest(target=target, row=row, separator=separator, date=date):
                env = dict(os.environ, TARGET=target, LAST_ROW=row, DECIMAL_SEPARATOR=separator, DATE_FORMAT=date)
                result = subprocess.run(['bash', '-e', '-c', script], env=env, capture_output=True)
                self.assertEqual(expected, result.returncode)


if __name__ == '__main__':
    unittest.main()
