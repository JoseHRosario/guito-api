"""Hermetic safety regression checks for the registered premerge dispatch path."""
import os
from pathlib import Path
import subprocess
import tempfile
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

    def test_archive_password_resolves_at_consuming_steps_after_staging_environment_binding(self):
        job = self.child.split('  rehearse:\n', 1)[1]
        self.assertIn('    environment: staging\n', job)
        job_settings, steps = job.split('    steps:\n', 1)
        self.assertNotIn('secrets.IMPORT_ARCHIVE_PASSWORD', job_settings)
        for name in ['Validate protected evidence prerequisites', 'Recover frozen snapshot for retry',
                     'Encrypt evidence even when parity blocks readiness']:
            with self.subTest(step=name):
                step = steps.split(f'      - name: {name}\n', 1)[1].split('      - ', 1)[0]
                self.assertIn('        env:\n          ARCHIVE_PASSWORD: ${{ secrets.IMPORT_ARCHIVE_PASSWORD }}\n', step)
        self.assertNotIn('secrets: inherit', self.caller)
        self.assertIn('    secrets:\n      IMPORT_ARCHIVE_PASSWORD: ${{ secrets.IMPORT_ARCHIVE_PASSWORD }}', self.caller)
        self.assertIn('    secrets:\n      IMPORT_ARCHIVE_PASSWORD:\n        required: false', self.child)
        self.assertNotIn('ARCHIVE_PASSWORD=', self.child)

    def test_protected_evidence_steps_fail_closed_and_round_trip_without_printing_secret(self):
        def script(name):
            step = self.child.split(f'      - name: {name}\n', 1)[1].split('      - ', 1)[0]
            return step.split('        run: |\n', 1)[1]

        with tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR')) as temporary:
            env = dict(os.environ, RUNNER_TEMP=temporary, APPROVAL_FILE='', ARCHIVE_PASSWORD='')
            preflight = script('Validate protected evidence prerequisites')
            result = subprocess.run(['bash', '-e', '-c', preflight], env=env, capture_output=True)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((Path(temporary) / 'import-evidence').exists())
            env['ARCHIVE_PASSWORD'] = 'synthetic-hermetic-evidence-key'
            result = subprocess.run(['bash', '-e', '-c', preflight], env=env, capture_output=True)
            self.assertEqual(0, result.returncode)
            snapshot = Path(temporary) / 'import-evidence' / 'synthetic.snapshot.json'
            snapshot.write_text('{"synthetic":true}')
            result = subprocess.run(['bash', '-e', '-o', 'pipefail', '-c', script('Encrypt evidence even when parity blocks readiness')], env=env, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertNotIn(env['ARCHIVE_PASSWORD'].encode(), result.stdout + result.stderr)
            (Path(temporary) / 'import-protected').rename(Path(temporary) / 'import-download')
            snapshot.unlink()
            recovery = script('Recover frozen snapshot for retry')
            result = subprocess.run(['bash', '-e', '-o', 'pipefail', '-c', recovery], env=env, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual('{"synthetic":true}', snapshot.read_text())
            snapshot.unlink()
            ciphertext = Path(temporary) / 'import-download' / 'evidence.tar.enc'
            ciphertext.write_bytes(ciphertext.read_bytes() + b'tampered')
            result = subprocess.run(['bash', '-e', '-o', 'pipefail', '-c', recovery], env=env, capture_output=True)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse(snapshot.exists())


if __name__ == '__main__':
    unittest.main()
