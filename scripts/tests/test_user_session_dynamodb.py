"""Exercise the public provisioner CLI with a stateful, offline AWS executable."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "deploy/user-session-dynamodb.py"
ACCOUNT = "497087877832"
REGION = "eu-west-1"
TABLE = "guito-api-user-sessions-staging"

FAKE_AWS = r'''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
path = Path(os.environ['FAKE_STATE'])
s = json.loads(path.read_text())
a = sys.argv[1:]
with open(os.environ['FAKE_LOG'], 'a') as log:
    log.write(json.dumps(a) + '\n')
def save(): path.write_text(json.dumps(s))
def emit(value): print(json.dumps(value)); save(); sys.exit(0)
def error(code, operation):
    print('An error occurred (' + code + ') when calling the ' + operation + ' operation: fake failure', file=sys.stderr)
    save(); sys.exit(254)
def option(name): return a[a.index(name)+1]
if 'get-caller-identity' in a: emit({'Account': s.get('account', '497087877832')})
if 'put-role-policy' in a:
    s['runtime_policy'] = json.loads(Path(option('--policy-document').removeprefix('file://')).read_text())
    emit({})
if 'get-function-configuration' in a: emit(s.get('variables', {}))
if 'update-function-configuration' in a:
    s['variables'] = json.loads(Path(option('--environment').removeprefix('file://')).read_text())['Variables']
    emit({})
if 'wait' in a: emit({})
operation = next((x for x in a if x in ('describe-table', 'create-table', 'describe-time-to-live', 'update-time-to-live', 'describe-continuous-backups')), None)
if s.get('error'): error(s['error'], 'DescribeTable')
if operation == 'describe-table':
    if 'describe_result' in s: emit(s['describe_result'])
    if not s.get('table'): error('ResourceNotFoundException', 'DescribeTable')
    emit({'Table': s['table']})
if operation == 'create-table':
    p = json.loads(option('--cli-input-json'))
    s['created'] = p
    s['table'] = dict(p, TableStatus='ACTIVE', TableArn='arn:aws:dynamodb:eu-west-1:497087877832:table/' + p['TableName'])
    s['table']['TableClassSummary'] = {'TableClass': p['TableClass']}
    s['table']['BillingModeSummary'] = {'BillingMode': p['BillingMode']}
    s['ttl'] = {'TimeToLiveStatus': 'DISABLED'}
    emit({'TableDescription': s['table']})
if operation == 'describe-continuous-backups': emit({'ContinuousBackupsDescription': {'PointInTimeRecoveryDescription': {'PointInTimeRecoveryStatus': s.get('pitr', 'DISABLED')}}})
if operation == 'describe-time-to-live':
    if s.get('ttl', {}).get('TimeToLiveStatus') == 'ENABLING' and not s.get('stuck'):
        s['ttl']['TimeToLiveStatus'] = 'ENABLED'
    emit({'TimeToLiveDescription': s['ttl']})
if operation == 'update-time-to-live':
    s['ttl_request'] = json.loads(option('--time-to-live-specification'))
    s['ttl'] = {'TimeToLiveStatus': 'ENABLING', 'AttributeName': s['ttl_request']['AttributeName']}
    emit({'TimeToLiveSpecification': s['ttl_request']})
raise SystemExit('unexpected AWS call: ' + repr(a))
'''


def valid_table():
    return {'TableName': TABLE, 'TableArn': f'arn:aws:dynamodb:{REGION}:{ACCOUNT}:table/{TABLE}',
            'TableStatus': 'ACTIVE', 'AttributeDefinitions': [{'AttributeName': 'handle_hash', 'AttributeType': 'S'}],
            'KeySchema': [{'AttributeName': 'handle_hash', 'KeyType': 'HASH'}],
            'ProvisionedThroughput': {'ReadCapacityUnits': 1, 'WriteCapacityUnits': 1},
            'DeletionProtectionEnabled': True}


class UserSessionDynamoDbTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR', os.environ.get('RUNNER_TEMP')))
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        aws = self.directory / 'aws'
        aws.write_text(FAKE_AWS)
        aws.chmod(0o700)
        self.state = self.directory / 'state.json'
        self.log = self.directory / 'calls.jsonl'
        self.env = dict(os.environ, PATH=str(self.directory) + os.pathsep + os.environ['PATH'],
                        FAKE_STATE=str(self.state), FAKE_LOG=str(self.log), AWS_EC2_METADATA_DISABLED='true')
        self.set_state(table=valid_table(), ttl={'TimeToLiveStatus': 'ENABLED', 'AttributeName': 'ttl'})

    def set_state(self, **values):
        self.state.write_text(json.dumps(values))
        self.log.write_text('')

    def run_helper(self, *extra):
        return subprocess.run([sys.executable, str(HELPER), '--environment', 'staging', '--account-id', ACCOUNT,
                               '--max-attempts', '3', '--poll-seconds', '0', *extra],
                              capture_output=True, text=True, env=self.env, timeout=15)

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def test_success_should_report_verified_nonsecret_table_settings(self):
        # Arrange
        self.set_state(table=valid_table(), ttl={'TimeToLiveStatus': 'ENABLED', 'AttributeName': 'ttl'})

        # Act
        result = self.run_helper()

        # Assert
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn(TABLE, result.stdout)
        evidence = json.loads(result.stdout)
        self.assertEqual(TABLE, evidence['table'])
        self.assertEqual(REGION, evidence['region'])
        self.assertEqual({'ReadCapacityUnits': 1, 'WriteCapacityUnits': 1}, evidence['provisioned_capacity'])
        self.assertEqual('ACTIVE', evidence['status'])
        self.assertEqual('PROVISIONED', evidence['billing_mode'])
        self.assertEqual('STANDARD', evidence['table_class'])
        self.assertTrue(evidence['deletion_protection'])
        self.assertEqual({'TimeToLiveStatus': 'ENABLED', 'AttributeName': 'ttl'}, evidence['ttl'])
        self.assertEqual('DISABLED', evidence['pitr'])
        self.assertEqual('AWS_OWNED', evidence['encryption'])

    def test_existing_standard_provisioned_default_summaries_should_be_idempotent(self):
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(any('create-table' in call or 'update-time-to-live' in call for call in self.calls()))
        for call in self.calls():
            self.assertEqual(REGION, call[call.index('--region') + 1])
            if '--table-name' in call:
                self.assertEqual(TABLE, call[call.index('--table-name') + 1])

    def test_production_should_touch_only_production_and_accept_explicit_standard_defaults(self):
        table = valid_table()
        table.update(TableName='guito-api-user-sessions',
                     TableArn=f'arn:aws:dynamodb:{REGION}:{ACCOUNT}:table/guito-api-user-sessions',
                     TableClassSummary={'TableClass': 'STANDARD'}, BillingModeSummary={'BillingMode': 'PROVISIONED'},
                     StreamSpecification={'StreamEnabled': False}, SSEDescription={'SSEType': 'AES256'})
        self.set_state(table=table, ttl={'TimeToLiveStatus': 'ENABLED', 'AttributeName': 'ttl'})
        result = self.run_helper('--environment', 'production')
        self.assertEqual(0, result.returncode, result.stderr)
        for call in self.calls():
            if '--table-name' in call:
                self.assertEqual('guito-api-user-sessions', call[call.index('--table-name') + 1])
        self.assertFalse(any('create-table' in call or 'update-time-to-live' in call for call in self.calls()))

    def test_missing_table_should_create_exact_free_first_shape_and_enable_ttl_once(self):
        self.set_state()
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stderr)
        state = json.loads(self.state.read_text())
        self.assertEqual({
            'TableName': TABLE, 'AttributeDefinitions': [{'AttributeName': 'handle_hash', 'AttributeType': 'S'}],
            'KeySchema': [{'AttributeName': 'handle_hash', 'KeyType': 'HASH'}], 'BillingMode': 'PROVISIONED',
            'ProvisionedThroughput': {'ReadCapacityUnits': 1, 'WriteCapacityUnits': 1},
            'TableClass': 'STANDARD', 'DeletionProtectionEnabled': True,
        }, state['created'])
        self.assertEqual({'Enabled': True, 'AttributeName': 'ttl'}, state['ttl_request'])
        self.assertEqual('ENABLED', state['ttl']['TimeToLiveStatus'])
        first_count = len(self.calls())
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(any('create-table' in call or 'update-time-to-live' in call for call in self.calls()[first_count:]))
        self.assertGreaterEqual(sum('describe-table' in call for call in self.calls()), 2)
        self.assertGreaterEqual(sum('describe-time-to-live' in call for call in self.calls()), 3)

    def test_existing_drift_should_fail_closed_without_any_mutation(self):
        drifts = [
            {'BillingModeSummary': {'BillingMode': 'PAY_PER_REQUEST'}},
            {'BillingModeSummary': {'BillingMode': 'None'}},
            {'TableClassSummary': {'TableClass': 'STANDARD_INFREQUENT_ACCESS'}},
            {'ProvisionedThroughput': {'ReadCapacityUnits': 2, 'WriteCapacityUnits': 1}},
            {'ProvisionedThroughput': {'ReadCapacityUnits': 1, 'WriteCapacityUnits': 2}},
            {'DeletionProtectionEnabled': False},
            {'KeySchema': [{'AttributeName': 'other', 'KeyType': 'HASH'}]},
            {'AttributeDefinitions': [{'AttributeName': 'handle_hash', 'AttributeType': 'N'}]},
            {'GlobalSecondaryIndexes': [{'IndexName': 'paid-index'}]},
            {'LocalSecondaryIndexes': [{'IndexName': 'index'}]},
            {'StreamSpecification': {'StreamEnabled': True}},
            {'SSEDescription': {'SSEType': 'KMS', 'KMSMasterKeyArn': 'not-approved'}},
            {'TableName': 'guito-api-user-sessions'},
            {'TableArn': f'arn:aws:dynamodb:{REGION}:{ACCOUNT}:table/guito-api-user-sessions'},
        ]
        for drift in drifts:
            with self.subTest(drift=drift):
                self.set_state(table=dict(valid_table(), **drift), ttl={'TimeToLiveStatus': 'DISABLED'})
                result = self.run_helper()
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertNotIn('Traceback', result.stderr)
                self.assertFalse(any('create-table' in call or 'update-time-to-live' in call for call in self.calls()))
        self.set_state(table=valid_table(), pitr='ENABLED', ttl={'TimeToLiveStatus': 'DISABLED'})
        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(any('update-time-to-live' in call for call in self.calls()))

    def test_denial_network_and_wrong_account_should_never_be_treated_as_missing(self):
        for error in ('AccessDeniedException', 'UnrecognizedClientException', 'NetworkingError'):
            with self.subTest(error=error):
                self.set_state(error=error)
                result = self.run_helper()
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(any('create-table' in call for call in self.calls()))
        self.set_state(account='111111111111')
        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(1, len(self.calls()))

    def test_successful_malformed_describe_should_never_be_treated_as_missing(self):
        for response in (None, [], 'None', {}):
            with self.subTest(response=response):
                self.set_state(describe_result=response)
                result = self.run_helper()
                self.assertNotEqual(0, result.returncode)
                self.assertNotIn('Traceback', result.stderr)
                self.assertFalse(any('create-table' in call for call in self.calls()))

    def test_ttl_wrong_attribute_should_fail_without_changing_configuration(self):
        for status in ('DISABLED', 'ENABLING', 'ENABLED', 'DISABLING'):
            with self.subTest(status=status):
                self.set_state(table=valid_table(), ttl={'TimeToLiveStatus': status, 'AttributeName': 'wrong'})
                result = self.run_helper()
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(any('update-time-to-live' in call for call in self.calls()))

    def test_enabling_ttl_should_wait_idempotently_and_stuck_states_should_be_bounded(self):
        self.set_state(table=valid_table(), ttl={'TimeToLiveStatus': 'ENABLING', 'AttributeName': 'ttl'})
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(any('update-time-to-live' in call for call in self.calls()))
        self.set_state(table=valid_table(), ttl={'TimeToLiveStatus': 'DISABLED'}, stuck=True)
        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(1, sum('update-time-to-live' in call for call in self.calls()))
        self.assertEqual(3, sum('describe-time-to-live' in call for call in self.calls()))
        for stuck_table in (False, True):
            table = valid_table()
            if stuck_table:
                table['TableStatus'] = 'CREATING'
            self.set_state(table=table, ttl={'TimeToLiveStatus': 'ENABLING', 'AttributeName': 'ttl'}, stuck=True)
            result = self.run_helper()
            self.assertNotEqual(0, result.returncode)
            operation = 'describe-table' if stuck_table else 'describe-time-to-live'
            self.assertLessEqual(sum(operation in call for call in self.calls()), 4)

    def test_invalid_targets_and_wait_bounds_should_fail_before_aws(self):
        for args in (('--environment', 'unknown'), ('--account-id', '111111111111'), ('--region', 'us-east-1'),
                     ('--max-attempts', '0'), ('--max-attempts', '121'), ('--poll-seconds', '-1'), ('--poll-seconds', 'nan')):
            with self.subTest(args=args):
                self.set_state()
                result = self.run_helper(*args)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual([], self.calls())

    def test_policy_artifacts_should_be_exact_and_offline(self):
        result = self.run_helper('--policies-only', '--output-dir', str(self.directory))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.calls())
        resources = [f'arn:aws:dynamodb:{REGION}:{ACCOUNT}:table/{name}'
                     for name in ('guito-api-user-sessions', TABLE)]
        runtime = json.loads((self.directory / 'user-session-runtime-policy.json').read_text())
        provisioner = json.loads((self.directory / 'user-session-provisioner-policy.json').read_text())
        self.assertEqual({'Version': '2012-10-17', 'Statement': [{'Effect': 'Allow',
            'Action': ['dynamodb:GetItem', 'dynamodb:PutItem', 'dynamodb:UpdateItem', 'dynamodb:DeleteItem'],
            'Resource': resources}]}, runtime)
        self.assertEqual({'Version': '2012-10-17', 'Statement': [{'Effect': 'Allow',
            'Action': ['dynamodb:DescribeTable', 'dynamodb:CreateTable', 'dynamodb:DescribeTimeToLive',
                       'dynamodb:UpdateTimeToLive', 'dynamodb:DescribeContinuousBackups'],
            'Resource': resources}]}, provisioner)
        checked_in = json.loads((ROOT / 'deploy/user-session-provisioner-policy.json').read_text())
        self.assertEqual(provisioner['Version'], checked_in['Version'])
        self.assertEqual(1, len(checked_in['Statement']))
        actual, expected = checked_in['Statement'][0], provisioner['Statement'][0]
        self.assertEqual({'Sid', 'Effect', 'Action', 'Resource'}, set(actual))
        self.assertEqual(expected['Effect'], actual['Effect'])
        self.assertEqual(set(expected['Action']), set(actual['Action']))
        self.assertEqual(set(expected['Resource']), set(actual['Resource']))

    def test_deploy_should_provision_before_code_and_install_only_separate_runtime_policy(self):
        source = (ROOT / 'deploy/deploy.sh').read_text()
        marker = '# --- User-session DynamoDB preflight ---'
        self.assertIn(marker, source)
        block = source.split(marker, 1)[1].split('# --- End user-session DynamoDB preflight ---', 1)[0]
        self.assertLess(source.index(marker), source.index('create_or_update "$API_NAME"'))
        self.assertIn('--policy-name guito-api-user-sessions', source)
        policy = source.split('# --- User-session runtime IAM ---', 1)[1].split('# --- End user-session runtime IAM ---', 1)[0]
        env = dict(self.env, REPO_ROOT=str(ROOT), DEPLOY_WORK_DIR=str(self.directory), ENV='staging',
                   REGION=REGION, ACCOUNT_ID=ACCOUNT, ROLE='guito-api-lambda-role')
        result = subprocess.run(['bash', '-euc', block + '\n' + policy], env=env, capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stderr)
        puts = [call for call in self.calls() if 'put-role-policy' in call]
        self.assertEqual(1, len(puts))
        self.assertEqual('guito-api-lambda-role', puts[0][puts[0].index('--role-name') + 1])
        self.assertEqual('guito-api-user-sessions', puts[0][puts[0].index('--policy-name') + 1])
        state = json.loads(self.state.read_text())
        self.assertEqual(json.loads((self.directory / 'user-session-runtime-policy.json').read_text()), state['runtime_policy'])
        self.set_state(error='AccessDeniedException')
        result = subprocess.run(['bash', '-euc', block + '\n' + policy], env=env, capture_output=True, text=True, timeout=15)
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(any('put-role-policy' in call for call in self.calls()))

    def test_deploy_environment_merge_should_remove_all_stale_session_children(self):
        source = (ROOT / 'deploy/deploy.sh').read_text()
        function = 'apply_env () {' + source.split('apply_env () {', 1)[1].split('\n}\n', 1)[0] + '\n}'
        installed = {'KeepMe': 'untouched', 'AppConfiguration__UserSessions__TableName': 'prod-wrong',
                     'AppConfiguration__UserSessions__TableName__0': 'indexed-wrong',
                     'appconfiguration__usersessions__region': 'wrong', 'AppConfiguration__UserSessionStore': 'Postgres',
                     'AppConfiguration__UserSessions': 'invalid-parent', 'AppConfiguration__Cors__AllowedOrigins__9': 'https://stale.example'}
        expected = {'KeepMe': 'untouched', 'AppConfiguration__UserSessions__TableName': TABLE,
                    'AppConfiguration__UserSessions__Region': REGION,
                    'AppConfiguration__Cors__AllowedOrigins__0': 'https://staging.example'}
        payload = self.directory / 'env.json'
        payload.write_text(json.dumps({key: value for key, value in expected.items() if key != 'KeepMe'}))
        self.set_state(variables=installed)
        result = subprocess.run(['bash', '-euc', function + '\napply_env guito-api-staging "$PAYLOAD"'],
                                env=dict(self.env, REGION=REGION, DEPLOY_WORK_DIR=str(self.directory), PAYLOAD=str(payload)),
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(expected, json.loads(self.state.read_text())['variables'])


if __name__ == '__main__':
    unittest.main()
