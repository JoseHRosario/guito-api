"""Hermetic checks at workflow routing and deployment shell/CLI boundaries."""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/ci-cd.yml'


def job(source, name):
    match = re.search(r'^  ' + re.escape(name) + r':\n(.*?)(?=^  [\w-]+:\n|\Z)', source, re.M | re.S)
    if not match:
        raise AssertionError('Missing workflow job: ' + name)
    return match.group(1)


def run_step(source, name):
    match = re.search(r'^      - name: ' + re.escape(name) + r'\n(.*?)(?=^      - |\Z)', source, re.M | re.S)
    if not match:
        raise AssertionError('Missing workflow step: ' + name)
    body = match.group(1).split('        run: |\n', 1)[1]
    return textwrap.dedent(body)


def selected(condition, event, ref, environment, target):
    # Evaluate the checked-in guard, not a duplicate routing implementation.
    values = {'github.event_name': event, 'github.ref_name': ref,
              'inputs.environment': environment, 'inputs.infrastructure_target': target}
    condition = ' '.join(condition.strip().removeprefix('${{').removesuffix('}}').split())
    for key, value in values.items():
        condition = condition.replace(key, repr(value))
    condition = condition.replace('&&', ' and ').replace('||', ' or ')
    return bool(eval(condition, {'__builtins__': {}, 'startsWith': lambda value, prefix: value.startswith(prefix)}))


class CloudFormationWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.source = WORKFLOW.read_text()
        self.temp = tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR', os.environ.get('RUNNER_TEMP')))
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.log = self.directory / 'calls.jsonl'
        self.log.write_text('')
        deploy = self.directory / 'deploy'
        deploy.mkdir()
        fake = '''import json, os, sys
with open(os.environ['FAKE_LOG'], 'a') as log:
    log.write(json.dumps([os.path.basename(__file__), *sys.argv[1:]]) + '\\n')
if os.environ.get('FAIL_GATE') == ' '.join(sys.argv[1:]):
    sys.exit(42)
'''
        (deploy / 'storage-infrastructure.py').write_text(fake)
        (deploy / 'user-session-dynamodb.py').write_text(fake)
        self.env = dict(os.environ, REPO_ROOT=str(self.directory), DEPLOY_WORK_DIR=str(self.directory),
                        FAKE_LOG=str(self.log), ENV='staging', REGION='eu-west-1', ACCOUNT_ID='497087877832',
                        RUNNER_TEMP=str(self.directory), ACTION='plan-import', TARGET='sessions-staging', CSARN='')

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def test_normal_deploy_gates_sessions_but_does_not_require_managed_aurora(self):
        source = (ROOT / 'deploy/deploy.sh').read_text()
        marker = '# --- User-session DynamoDB preflight ---'
        block = source.split(marker, 1)[1].split('# --- End user-session DynamoDB preflight ---', 1)[0]
        self.assertLess(source.index(marker), source.index('"$REPO_ROOT/deploy/db-apply.sh"'))
        self.assertLess(source.index(marker), source.index('create_or_update "$API_NAME"'))
        for environment in ('staging', 'production'):
            with self.subTest(environment=environment):
                self.log.write_text('')
                result = subprocess.run(['bash', '-euc', block], env=dict(self.env, ENV=environment),
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual([
                    ['storage-infrastructure.py', 'ensure', '--target', 'sessions-' + environment],
                    ['user-session-dynamodb.py', '--environment', environment, '--account-id', '497087877832',
                     '--region', 'eu-west-1', '--output-dir', str(self.directory)],
                ], self.calls())
        for failure, count in (('ensure --target sessions-staging', 1), ('--environment staging --account-id 497087877832 --region eu-west-1 --output-dir ' + str(self.directory), 2)):
            with self.subTest(failure=failure):
                self.log.write_text('')
                result = subprocess.run(['bash', '-euc', block + '\nprintf unsafe-after-gate'],
                                        env=dict(self.env, FAIL_GATE=failure), capture_output=True, text=True, timeout=10)
                self.assertEqual(42, result.returncode, result.stderr)
                self.assertNotIn('unsafe-after-gate', result.stdout)
                self.assertEqual(count, len(self.calls()))

    def test_manual_adoption_should_never_select_application_deployment(self):
        conditions = {}
        for name in ('deploy-staging', 'deploy-production', 'infrastructure-adoption'):
            body = job(self.source, name)
            match = re.search(r'^    if: >-\n(.*?)(?=^    \S)', body, re.M | re.S)
            self.assertIsNotNone(match)
            assert match is not None
            conditions[name] = match.group(1)
            self.assertIn('needs: test', body)
        for target in ('sessions-staging', 'sessions-production', 'postgres-shared'):
            for environment in ('staging', 'production'):
                with self.subTest(target=target, environment=environment):
                    self.assertFalse(selected(conditions['deploy-staging'], 'workflow_dispatch', 'feature/156', environment, target))
                    self.assertFalse(selected(conditions['deploy-production'], 'workflow_dispatch', 'feature/156', environment, target))
                    self.assertTrue(selected(conditions['infrastructure-adoption'], 'workflow_dispatch', 'feature/156', environment, target))
        for environment in ('staging', 'production'):
            for name in conditions:
                self.assertEqual(name == 'deploy-' + environment,
                                 selected(conditions[name], 'workflow_dispatch', 'feature/156', environment, 'none'))
        for ref, expected in (('feature/156-persistent-login', 'deploy-staging'), ('master', 'deploy-production'), ('chore/docs', None)):
            for name in conditions:
                self.assertEqual(name == expected, selected(conditions[name], 'push', ref, '', ''))

    def test_adoption_should_validate_inputs_before_oidc(self):
        adoption = job(self.source, 'infrastructure-adoption')
        self.assertIn('options: [none, sessions-staging, sessions-production]', self.source)
        script = run_step(adoption, 'Validate infrastructure inputs (no AWS)')
        self.assertLess(adoption.index('Validate infrastructure inputs (no AWS)'),
                        adoption.index('aws-actions/configure-aws-credentials@v4'))
        for values in ({'ACTION': 'execute-import', 'CSARN': ''},
                       {'ACTION': 'execute-import', 'CSARN': 'not-a-full-arn'},
                       {'ACTION': 'ensure'}, {'TARGET': 'postgres-shared'}, {'TARGET': 'postgres-shared; echo injected'},
                       {'ACTION': 'execute-import', 'CSARN': 'arn:aws:cloudformation:us-east-1:497087877832:changeSet/guito-infra-test/id'}):
            with self.subTest(values=values):
                result = subprocess.run(['bash', '-euc', script], env=dict(self.env, **values),
                                        capture_output=True, text=True, timeout=10)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual([], self.calls())
        for target in ('sessions-staging', 'sessions-production'):
            for action in ('plan-import', 'execute-import'):
                arn = 'arn:aws:cloudformation:eu-west-1:497087877832:changeSet/guito-infra-reviewed/00000000-0000-0000-0000-000000000000' if action == 'execute-import' else ''
                result = subprocess.run(['bash', '-euc', script], env=dict(self.env, ACTION=action, TARGET=target, CSARN=arn),
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(0, result.returncode, result.stderr)

    def test_adoption_should_pass_quoted_exact_arguments_to_controller(self):
        adoption = job(self.source, 'infrastructure-adoption')
        script = run_step(adoption, 'Run storage adoption controller')
        self.assertNotIn('${{', script)
        for arn in ('', 'arn:aws:cloudformation:eu-west-1:497087877832:changeSet/guito-infra-reviewed/id', '$(touch injected); untrusted value'):
            with self.subTest(arn=arn):
                self.log.write_text('')
                result = subprocess.run(['bash', '-euc', script], cwd=self.directory,
                                        env=dict(self.env, CSARN=arn), capture_output=True, text=True, timeout=10)
                self.assertEqual(0, result.returncode, result.stderr)
                expected = ['storage-infrastructure.py', 'plan-import', '--target', 'sessions-staging',
                            '--output-dir', str(self.directory / 'guito-infrastructure')]
                if arn:
                    expected += ['--change-set-arn', arn]
                self.assertEqual([expected], self.calls())
                self.assertFalse((self.directory / 'injected').exists())

    def test_adoption_should_pin_sha_approval_environment_and_safe_artifact(self):
        adoption = job(self.source, 'infrastructure-adoption')
        self.assertIn('ref: ${{ github.sha }}', adoption)
        self.assertIn('role-to-assume: arn:aws:iam::497087877832:role/guito-api-deploy', adoption)
        self.assertIn('aws-region: eu-west-1', adoption)
        self.assertIn("environment: ${{ inputs.infrastructure_target == 'sessions-production' && 'production' || 'staging' }}", adoption)
        for key, value in (('ACTION', 'infrastructure_action'), ('TARGET', 'infrastructure_target'), ('CSARN', 'infrastructure_change_set_arn')):
            self.assertIn(key + ': ${{ inputs.' + value + ' }}', adoption)
        self.assertIn('actions/upload-artifact@v4', adoption)
        self.assertIn('path: ${{ runner.temp }}/guito-infrastructure/infrastructure-plan.json', adoption)
        self.assertIn("if: ${{ success() && steps.plan-artifact.outputs.exists == 'true' }}", adoption)
        self.assertNotIn('deploy/deploy.sh', adoption)
        script = run_step(adoption, 'Check nonsecret plan artifact')
        output = self.directory / 'step-output'
        for exists in (False, True):
            output.write_text('')
            plan = self.directory / 'guito-infrastructure/infrastructure-plan.json'
            if exists:
                plan.parent.mkdir(exist_ok=True)
                plan.write_text('{}')
            result = subprocess.run(['bash', '-euc', script], env=dict(self.env, GITHUB_OUTPUT=str(output)),
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual('exists=true\n' if exists else '', output.read_text())

    def test_all_storage_operations_should_share_one_non_cancelling_lane(self):
        concurrency = self.source.split('\nconcurrency:\n', 1)[1].split('\njobs:', 1)[0]
        self.assertIn('group: ci-cd-guito-storage', concurrency)
        self.assertNotIn('${{', concurrency)
        self.assertIn('cancel-in-progress: false', concurrency)


if __name__ == '__main__':
    unittest.main()
