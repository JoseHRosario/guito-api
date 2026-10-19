"""Hermetic public CLI tests. AWS is a deny-by-default executable transcript."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
ACCOUNT = '497087877832'
ARN = f'arn:aws:cloudformation:eu-west-1:{ACCOUNT}:changeSet/guito-infra-test/12345678-1234-1234-1234-123456789abc'


class StorageInfrastructureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'deploy').mkdir()
        (self.root / 'infra').mkdir()
        source = ROOT / 'deploy/storage-infrastructure.py'
        if source.exists():
            shutil.copy(source, self.root / 'deploy/storage-infrastructure.py')
        self.calls = self.root / 'calls.jsonl'
        fake = self.root / 'aws'
        fake.write_text('''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
args = sys.argv[1:]
p = Path(os.environ['FAKE_STEPS'])
steps = json.loads(p.read_text())
with open(os.environ['FAKE_CALLS'], 'a') as f: f.write(json.dumps(args)+'\\n')
if not steps:
    print('Unexpected AWS call', file=sys.stderr); sys.exit(93)
step = steps.pop(0); p.write_text(json.dumps(steps))
if args[:2] != step['op'].split():
    print('Wrong AWS operation', file=sys.stderr); sys.exit(94)
print(json.dumps(step.get('data', {})) if not step.get('error') else step['error'], file=sys.stderr if step.get('error') else sys.stdout)
sys.exit(1 if step.get('error') else 0)
''')
        fake.chmod(0o755)
        self.steps = []

    def queue(self, op, data=None, error=None):
        if data and op == 'cloudformation describe-change-set' and data.get('StackName'):
            data.setdefault('StackId', f"arn:aws:cloudformation:eu-west-1:{ACCOUNT}:stack/{data['StackName']}/stack-1")
        if data and op == 'cloudformation describe-stacks':
            for stack in data['Stacks']:
                stack.setdefault('StackId', f"arn:aws:cloudformation:eu-west-1:{ACCOUNT}:stack/{stack['StackName']}/stack-1")
        self.steps.append(dict(op=op, data=data, error=error))

    def identity(self, account=ACCOUNT):
        self.queue('sts get-caller-identity', {'Account': account, 'Arn': f'arn:aws:sts::{account}:assumed-role/guito-api-deploy/test'})

    def run_cli(self, action='verify', target='sessions-staging', extra=()):
        transcript = self.root / 'steps.json'
        transcript.write_text(json.dumps(self.steps))
        env = dict(os.environ, PATH=str(self.root)+os.pathsep+os.environ['PATH'], FAKE_STEPS=str(transcript), FAKE_CALLS=str(self.calls))
        result = subprocess.run([sys.executable, str(self.root/'deploy/storage-infrastructure.py'), action, '--target', target, '--poll-interval', '0', '--max-polls', '2', *extra], env=env, capture_output=True, text=True, timeout=10)
        self.history = [json.loads(x) for x in self.calls.read_text().splitlines()] if self.calls.exists() else []
        return result

    def sessions(self):
        self.template = {'Parameters': {'Environment': {'Type': 'String', 'AllowedValues': ['staging', 'production']}}, 'Conditions': {'IsProduction': {'Fn::Equals': [{'Ref': 'Environment'}, 'production']}}, 'Resources': {'UserSessions': {'Type': 'AWS::DynamoDB::Table', 'DeletionPolicy': 'Retain', 'UpdateReplacePolicy': 'Retain', 'Properties': {'TableName': {'Fn::If': ['IsProduction', 'guito-api-user-sessions', 'guito-api-user-sessions-staging']}, 'BillingMode': 'PROVISIONED', 'TableClass': 'STANDARD', 'AttributeDefinitions': [{'AttributeName': 'handle_hash', 'AttributeType': 'S'}], 'KeySchema': [{'AttributeName': 'handle_hash', 'KeyType': 'HASH'}], 'ProvisionedThroughput': {'ReadCapacityUnits': 1, 'WriteCapacityUnits': 1}, 'TimeToLiveSpecification': {'AttributeName': 'ttl', 'Enabled': True}, 'DeletionProtectionEnabled': True, 'PointInTimeRecoverySpecification': {'PointInTimeRecoveryEnabled': False}}}}, 'Outputs': {'TableName': {'Value': {'Ref': 'UserSessions'}}}}
        (self.root/'infra/user-sessions.json').write_text(json.dumps(self.template))
        self.table = {'TableName': 'guito-api-user-sessions-staging', 'TableStatus': 'ACTIVE', 'TableClassSummary': {'TableClass': 'STANDARD'}, 'BillingModeSummary': {'BillingMode': 'PROVISIONED'}, 'AttributeDefinitions': [{'AttributeName': 'handle_hash', 'AttributeType': 'S'}], 'KeySchema': [{'AttributeName': 'handle_hash', 'KeyType': 'HASH'}], 'ProvisionedThroughput': {'ReadCapacityUnits': 1, 'WriteCapacityUnits': 1}, 'DeletionProtectionEnabled': True}
        self.changes = [{'ResourceChange': {'Action': 'Import', 'LogicalResourceId': 'UserSessions', 'ResourceType': 'AWS::DynamoDB::Table', 'Replacement': 'False', 'PhysicalResourceId': self.table['TableName']}}]

    def missing_stack(self):
        self.queue('cloudformation describe-stacks', error='An error occurred (ValidationError): Stack with id guito-sessions-staging does not exist')

    def physical(self):
        self.queue('dynamodb describe-table', {'Table': self.table})
        self.queue('dynamodb describe-time-to-live', {'TimeToLiveDescription': {'AttributeName': 'ttl', 'TimeToLiveStatus': 'ENABLED'}})
        self.queue('dynamodb describe-continuous-backups', {'ContinuousBackupsDescription': {'PointInTimeRecoveryDescription': {'PointInTimeRecoveryStatus': 'DISABLED'}}})

    def plan(self):
        self.identity()
        self.missing_stack()
        self.physical()
        self.queue('cloudformation get-template-summary', {'ResourceIdentifierSummaries': [{'LogicalResourceIds': ['UserSessions'], 'ResourceType': 'AWS::DynamoDB::Table', 'ResourceIdentifiers': ['TableName']}]})
        self.queue('cloudformation create-change-set', {'Id': ARN})
        self.queue('cloudformation describe-change-set', {'ChangeSetId': ARN, 'StackName': 'guito-sessions-staging', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'AVAILABLE', 'Changes': self.changes, 'Parameters': [{'ParameterKey': 'Environment', 'ParameterValue': 'staging'}]})

    def test_import_creation_validation_reports_only_safe_public_metadata(self):
        self.sessions()
        self.plan()
        self.steps[-2]['error'] = 'An error occurred (ValidationError) when calling the CreateChangeSet operation: ResourceTypes cannot be specified for IMPORT'

        result = self.run_cli('plan-import')

        self.assertEqual(result.returncode, 1)
        self.assertIn('ResourceTypes cannot be specified for IMPORT', json.loads(result.stdout)['error'])
        self.assertFalse(any('execute-change-set' in call for call in self.history))

    def test_import_creation_does_not_report_credential_related_validation_detail(self):
        self.sessions()
        self.plan()
        self.steps[-2]['error'] = 'An error occurred (ValidationError) when calling the CreateChangeSet operation: credential token PRIVATE-MARKER'

        result = self.run_cli('plan-import')

        self.assertEqual(result.returncode, 1)
        self.assertNotIn('PRIVATE-MARKER', result.stdout)
        self.assertIn('ValidationError', json.loads(result.stdout)['error'])

    def test_response_parameter_false_metadata_is_accepted(self):
        self.sessions()
        self.plan()
        self.steps[-1]['data']['Parameters'][0]['UsePreviousValue'] = False
        result = self.run_cli('plan-import')
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(json.loads((self.root/'steps.json').read_text()), [])

    def test_response_parameters_reject_ambiguity_or_unexpected_fields(self):
        cases = [dict(UsePreviousValue=True), dict(UsePreviousValue='false'),
                 dict(ResolvedValue='resolved'), dict(Unknown=False),
                 dict(ParameterValue='production')]
        for metadata in cases:
            with self.subTest(metadata=metadata):
                self.steps = []
                self.calls.unlink(missing_ok=True)
                self.sessions()
                self.plan()
                self.steps[-1]['data']['Parameters'][0].update(metadata)
                result = self.run_cli('plan-import')
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn('parameters', result.stdout)

    def test_response_parameters_reject_duplicate_extra_or_unresolved_keys(self):
        for parameters in [[{'ParameterKey': 'Environment'}],
                           [{'ParameterKey': 'Other', 'ParameterValue': 'staging'}],
                           [{'ParameterKey': 'Environment', 'ParameterValue': 'staging'}]*2]:
            with self.subTest(parameters=parameters):
                self.steps = []
                self.calls.unlink(missing_ok=True)
                self.sessions()
                self.plan()
                self.steps[-1]['data']['Parameters'] = parameters
                result = self.run_cli('plan-import')
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('parameters', result.stdout)

    def test_plan_import_only_creates_exact_import_change_set(self):
        self.sessions()
        self.plan()
        result = self.run_cli('plan-import', extra=('--output-dir', str(self.root/'out')))
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(report['change_set_arn'], ARN)
        self.assertEqual(len(report['template_sha256']), 64)
        create = next(c for c in self.history if c[:2] == ['cloudformation', 'create-change-set'])
        self.assertEqual(json.loads(create[create.index('--resource-types')+1]) if '--resource-types' in create else None, ['AWS::DynamoDB::Table'])
        self.assertEqual(json.loads(create[create.index('--resources-to-import')+1]), [{'ResourceType': 'AWS::DynamoDB::Table', 'LogicalResourceId': 'UserSessions', 'ResourceIdentifier': {'TableName': 'guito-api-user-sessions-staging'}}])
        self.assertFalse(any('execute-change-set' in c for c in self.history))
        self.assertEqual(json.loads((self.root/'out/infrastructure-plan.json').read_text()), report)

    def managed(self, status='IMPORT_COMPLETE'):
        self.queue('cloudformation describe-stacks', {'Stacks': [{'StackName': 'guito-sessions-staging', 'StackId': f'arn:aws:cloudformation:eu-west-1:{ACCOUNT}:stack/guito-sessions-staging/stack-1', 'StackStatus': status, 'Parameters': [{'ParameterKey': 'Environment', 'ParameterValue': 'staging'}]}]})

    def ownership(self):
        self.queue('cloudformation list-stack-resources', {'StackResourceSummaries': [{'LogicalResourceId': 'UserSessions', 'ResourceType': 'AWS::DynamoDB::Table', 'PhysicalResourceId': self.table['TableName'], 'ResourceStatus': 'IMPORT_COMPLETE'}]})
        self.queue('cloudformation get-template', {'TemplateBody': self.template})

    def execute_preview(self):
        self.identity()
        self.managed('REVIEW_IN_PROGRESS')
        self.queue('cloudformation describe-change-set', {'ChangeSetId': ARN, 'StackName': 'guito-sessions-staging', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'AVAILABLE', 'Changes': self.changes, 'Parameters': [{'ParameterKey': 'Environment', 'ParameterValue': 'staging'}]})
        self.queue('cloudformation get-template', {'TemplateBody': self.template})

    def drift(self, status='IN_SYNC'):
        self.queue('cloudformation detect-stack-drift', {'StackDriftDetectionId': 'drift-1'})
        self.queue('cloudformation describe-stack-drift-detection-status', {'DetectionStatus': 'DETECTION_COMPLETE', 'StackDriftStatus': status})
        self.queue('cloudformation describe-stack-resource-drifts', {'StackResourceDrifts': [{'LogicalResourceId': 'UserSessions', 'ResourceType': 'AWS::DynamoDB::Table', 'PhysicalResourceId': self.table['TableName'], 'StackResourceDriftStatus': status}]})

    def test_execute_import_revalidates_then_reads_back_ownership_and_drift(self):
        self.sessions()
        self.execute_preview()
        self.physical()
        self.queue('cloudformation execute-change-set')
        self.managed()
        self.managed()
        self.ownership()
        self.physical()
        self.drift()
        result = self.run_cli('execute-import', extra=('--change-set-arn', ARN))
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        self.assertEqual(json.loads(result.stdout)['status'], 'imported')
        self.assertEqual(json.loads((self.root/'steps.json').read_text()), [])

    def test_ensure_never_adopts_preexisting_table(self):
        self.sessions()
        self.identity()
        self.missing_stack()
        self.physical()
        result = self.run_cli('ensure')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('plan-import', result.stdout)
        self.assertFalse(any('create-change-set' in c for c in self.history))

    def test_ensure_creates_only_absent_sessions_table_then_verifies(self):
        self.sessions()
        self.identity()
        self.missing_stack()
        self.queue('dynamodb describe-table', error='An error occurred (ResourceNotFoundException) when calling the DescribeTable operation: Requested resource not found')
        self.queue('cloudformation create-change-set', {'Id': ARN})
        changes = json.loads(json.dumps(self.changes))
        changes[0]['ResourceChange']['Action'] = 'Add'
        self.queue('cloudformation describe-change-set', {'StackName': 'guito-sessions-staging', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'AVAILABLE', 'Changes': changes, 'Parameters': [{'ParameterKey': 'Environment', 'ParameterValue': 'staging'}]})
        self.queue('cloudformation execute-change-set')
        self.managed('CREATE_COMPLETE')
        self.managed('CREATE_COMPLETE')
        self.ownership()
        self.physical()
        result = self.run_cli('ensure')
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        self.assertEqual(json.loads(result.stdout)['status'], 'created')
        self.assertEqual(json.loads((self.root/'steps.json').read_text()), [])

    def test_ensure_identical_managed_table_is_read_only(self):
        self.sessions()
        self.identity()
        self.managed()
        self.ownership()
        self.physical()
        result = self.run_cli('ensure')
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        self.assertEqual(json.loads(result.stdout)['status'], 'noop')
        self.assertFalse(any('create-change-set' in c for c in self.history))

    def postgres(self):
        self.template = json.loads((ROOT/'infra/postgres-shared.json').read_text())
        (self.root/'infra/postgres-shared.json').write_text(json.dumps(self.template))
        # Native DescribeDBClusters/DescribeDBInstances metadata, independent of
        # CFN properties so a missing API-name translation cannot pass by construction.
        self.cluster = {
            'DBClusterIdentifier': 'db-cluster', 'Engine': 'aurora-postgresql',
            'EngineMode': 'provisioned', 'EngineVersion': '17.9',
            'DatabaseName': 'guito_dev', 'MasterUsername': 'postgres', 'Port': 5432,
            'StorageEncrypted': False, 'DeletionProtection': False,
            'BackupRetentionPeriod': 1, 'PreferredBackupWindow': '23:29-23:59',
            'PreferredMaintenanceWindow': 'mon:22:33-mon:23:03',
            'CopyTagsToSnapshot': False,
            'DBClusterParameterGroup': 'default.aurora-postgresql17',
            'DBSubnetGroup': 'default',
            'VpcSecurityGroups': [{'VpcSecurityGroupId': 'sg-0be03e5526e822276', 'Status': 'active'}],
            'AvailabilityZones': ['eu-west-1a', 'eu-west-1b', 'eu-west-1c'],
            'ServerlessV2ScalingConfiguration': {'MinCapacity': 0.0, 'MaxCapacity': 16.0, 'SecondsUntilAutoPause': 300},
            'HttpEndpointEnabled': True, 'IAMDatabaseAuthenticationEnabled': False,
            'NetworkType': 'IPV4', 'AutoMinorVersionUpgrade': True,
            'EngineLifecycleSupport': 'open-source-rds-extended-support',
            'Status': 'available', 'PendingModifiedValues': {},
            'DBClusterMembers': [{'DBInstanceIdentifier': 'guito', 'IsClusterWriter': True, 'DBClusterParameterGroupStatus': 'in-sync', 'PromotionTier': 1}]}
        self.writer = {
            'DBInstanceIdentifier': 'guito', 'DBClusterIdentifier': 'db-cluster',
            'DBInstanceClass': 'db.serverless', 'Engine': 'aurora-postgresql',
            'EngineVersion': '17.9',
            'DBParameterGroups': [{'DBParameterGroupName': 'default.aurora-postgresql17', 'ParameterApplyStatus': 'in-sync'}],
            'PubliclyAccessible': True, 'AvailabilityZone': 'eu-west-1a',
            'AutoMinorVersionUpgrade': True, 'PromotionTier': 1,
            'CACertificateIdentifier': 'rds-ca-rsa2048-g1', 'MonitoringInterval': 0,
            'PerformanceInsightsEnabled': False,
            'PreferredMaintenanceWindow': 'thu:04:19-thu:04:49',
            'DBSubnetGroup': {'DBSubnetGroupName': 'default'},
            'DBInstanceStatus': 'available', 'PendingModifiedValues': {}}
        self.changes = [{'ResourceChange': {'Action': 'Import', 'LogicalResourceId': logical, 'ResourceType': row['Type'], 'PhysicalResourceId': 'db-cluster' if logical == 'DatabaseCluster' else 'guito'}} for logical, row in self.template['Resources'].items()]

    def pg_physical(self):
        self.queue('rds describe-db-clusters', {'DBClusters': [self.cluster]})
        self.queue('rds describe-db-instances', {'DBInstances': [self.writer]})

    def pg_managed(self, status='IMPORT_COMPLETE'):
        self.queue('cloudformation describe-stacks', {'Stacks': [{'StackName': 'guito-postgres-shared', 'StackStatus': status, 'Parameters': []}]})

    def test_shared_import_compares_live_settings_and_locks_stack_policy(self):
        self.postgres()
        self.identity()
        self.pg_managed('REVIEW_IN_PROGRESS')
        self.queue('cloudformation describe-change-set', {'ChangeSetId': ARN, 'StackName': 'guito-postgres-shared', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'AVAILABLE', 'Changes': self.changes, 'Parameters': []})
        self.queue('cloudformation get-template', {'TemplateBody': self.template})
        self.pg_physical()
        self.queue('cloudformation execute-change-set')
        self.pg_managed()
        self.pg_managed()
        self.queue('cloudformation list-stack-resources', {'StackResourceSummaries': [{'LogicalResourceId': c['ResourceChange']['LogicalResourceId'], 'PhysicalResourceId': c['ResourceChange']['PhysicalResourceId'], 'ResourceType': c['ResourceChange']['ResourceType']} for c in self.changes]})
        self.queue('cloudformation get-template', {'TemplateBody': self.template})
        self.pg_physical()
        policy = {'Statement': [{'Effect': 'Allow', 'Principal': '*', 'Action': 'Update:*', 'Resource': '*'}, {'Effect': 'Deny', 'Principal': '*', 'Action': 'Update:*', 'Resource': ['LogicalResourceId/DatabaseCluster', 'LogicalResourceId/DatabaseWriter']}]}
        self.queue('cloudformation set-stack-policy')
        self.queue('cloudformation get-stack-policy', {'StackPolicyBody': json.dumps(policy)})
        self.queue('cloudformation detect-stack-drift', {'StackDriftDetectionId': 'drift-1'})
        self.queue('cloudformation describe-stack-drift-detection-status', {'DetectionStatus': 'DETECTION_COMPLETE', 'StackDriftStatus': 'IN_SYNC'})
        self.queue('cloudformation describe-stack-resource-drifts', {'StackResourceDrifts': [dict(c['ResourceChange'], StackResourceDriftStatus='IN_SYNC') for c in self.changes]})
        self.queue('cloudformation get-stack-policy', {'StackPolicyBody': json.dumps(policy)})
        result = self.run_cli('execute-import', 'postgres-shared', ('--change-set-arn', ARN))
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        self.assertEqual(json.loads((self.root/'steps.json').read_text()), [])
        call = next(c for c in self.history if 'set-stack-policy' in c)
        self.assertEqual(json.loads(call[call.index('--stack-policy-body')+1]), policy)
        self.assertFalse(any(c[0] == 'rds' and not c[1].startswith('describe-') for c in self.history))

    def test_aws_describe_change_set_has_no_changesettype_field(self):
        self.sessions()
        self.plan()
        self.assertNotIn('ChangeSetType', self.steps[-1]['data'])
        self.assertNotIn('RoleARN', self.steps[-1]['data'])
        result = self.run_cli('plan-import')
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)

    def test_ensure_updates_only_retained_nonreplacement_session_resource(self):
        self.sessions()
        self.identity()
        self.managed()
        self.ownership()
        self.steps[-1]['data']['TemplateBody'] = dict(self.template, Description='old description')
        self.physical()
        self.queue('cloudformation create-change-set', {'Id': ARN})
        changes = json.loads(json.dumps(self.changes))
        changes[0]['ResourceChange']['Action'] = 'Modify'
        self.queue('cloudformation describe-change-set', {'StackName': 'guito-sessions-staging', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'AVAILABLE', 'Changes': changes, 'Parameters': [{'ParameterKey': 'Environment', 'ParameterValue': 'staging'}]})
        self.queue('cloudformation execute-change-set')
        self.managed('UPDATE_COMPLETE')
        self.managed('UPDATE_COMPLETE')
        self.ownership()
        self.physical()
        result = self.run_cli('ensure')
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        self.assertEqual(json.loads(result.stdout)['status'], 'updated')

    def test_known_empty_update_is_noop_but_still_freshly_verified(self):
        self.sessions()
        self.identity()
        self.managed()
        self.ownership()
        self.steps[-1]['data']['TemplateBody'] = dict(self.template, Description='old')
        self.physical()
        self.queue('cloudformation create-change-set', {'Id': ARN})
        self.queue('cloudformation describe-change-set', {'Status': 'FAILED', 'StatusReason': "The submitted information didn't contain changes. Submit different information to create a change set.", 'Changes': []})
        self.managed()
        self.ownership()
        self.steps[-1]['data']['TemplateBody'] = dict(self.template, Description='old')
        self.physical()
        result = self.run_cli('ensure')
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        self.assertEqual(json.loads(result.stdout)['status'], 'noop')
        self.assertFalse(any('execute-change-set' in c for c in self.history))

    def test_execute_rejects_tampered_plans_before_any_mutation(self):
        cases = ['other-stack', 'extra-resource', 'remove', 'replace', 'wrong-parameters', 'service-role', 'different-template', 'not-available', 'nested']
        for case in cases:
            with self.subTest(case=case):
                self.steps = []
                self.calls.unlink(missing_ok=True)
                self.sessions()
                self.execute_preview()
                change = self.steps[-2]['data']
                if case == 'other-stack': change['StackName'] = 'guito-sessions-production'
                if case == 'extra-resource': change['Changes'] = change['Changes'] + change['Changes']
                if case == 'remove': change['Changes'][0]['ResourceChange']['Action'] = 'Remove'
                if case == 'replace': change['Changes'][0]['ResourceChange']['Replacement'] = 'True'
                if case == 'wrong-parameters': change['Parameters'][0]['ParameterValue'] = 'production'
                if case == 'service-role': self.steps[1]['data']['Stacks'][0]['RoleARN'] = 'unapproved'
                if case == 'different-template': self.steps[-1]['data']['TemplateBody'] = dict(self.template, Description='tampered')
                if case == 'not-available': change['ExecutionStatus'] = 'OBSOLETE'
                if case == 'nested': change['IncludeNestedStacks'] = True
                result = self.run_cli('execute-import', extra=('--change-set-arn', ARN))
                self.assertNotEqual(result.returncode, 0, case)
                self.assertFalse(any('execute-change-set' in c for c in self.history), case)

    def test_execute_requires_full_account_region_scoped_arn(self):
        for arn in [None, 'guito-infra-test', ARN.replace('eu-west-1', 'us-east-1'), ARN.replace(ACCOUNT, '000000000000'), ARN.replace('guito-infra-', 'unapproved-')]:
            with self.subTest(arn=arn):
                self.steps = []
                self.calls.unlink(missing_ok=True)
                self.sessions()
                self.identity()
                self.managed('REVIEW_IN_PROGRESS')
                result = self.run_cli('execute-import', extra=('--change-set-arn', arn) if arn else ())
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('ARN required', result.stdout)
                self.assertFalse(any('execute-change-set' in c for c in self.history))

    def test_native_cluster_subnet_mismatch_or_absence_blocks_import_plan(self):
        for subnet in ['other', None]:
            with self.subTest(subnet=subnet):
                self.steps = []
                self.calls.unlink(missing_ok=True)
                self.postgres()
                # A CFN-shaped field must never substitute for the native API field.
                self.cluster['DBSubnetGroupName'] = 'default'
                if subnet is None:
                    self.cluster.pop('DBSubnetGroup')
                else:
                    self.cluster['DBSubnetGroup'] = subnet
                self.identity()
                self.queue('cloudformation describe-stacks', error='ValidationError: Stack with id guito-postgres-shared does not exist')
                self.pg_physical()

                result = self.run_cli('plan-import', 'postgres-shared')

                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(json.loads(result.stdout)['error'], 'Aurora live property mismatch: DBSubnetGroupName')
                self.assertFalse(any(c[1] in ['get-template-summary', 'create-change-set', 'execute-change-set'] for c in self.history))
                self.assertEqual(json.loads((self.root/'steps.json').read_text()), [])

    def test_pending_extra_aurora_security_group_blocks_import_plan(self):
        self.postgres()
        self.identity()
        self.queue('cloudformation describe-stacks', error='ValidationError: Stack with id guito-postgres-shared does not exist')
        self.cluster['VpcSecurityGroups'].append({'VpcSecurityGroupId': 'sg-pending-extra', 'Status': 'adding'})
        self.pg_physical()
        self.queue('cloudformation get-template-summary')
        result = self.run_cli('plan-import', 'postgres-shared')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('security group', result.stdout)
        self.assertFalse(any('get-template-summary' in c or 'create-change-set' in c for c in self.history))

    def test_access_denied_with_missing_table_marker_never_creates(self):
        self.sessions()
        self.identity()
        self.missing_stack()
        self.queue('dynamodb describe-table', error='An error occurred (AccessDeniedException) when calling the DescribeTable operation: ResourceNotFoundException')
        self.queue('cloudformation create-change-set')
        result = self.run_cli('ensure')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('describe-table failed', result.stdout)
        self.assertFalse(any('create-change-set' in c for c in self.history))

    def test_fresh_aurora_mismatch_blocks_execute(self):
        for case in ['pending', 'capacity', 'writer-class', 'writer-subnet', 'unexpected-member', 'engine-version']:
            with self.subTest(case=case):
                self.steps = []
                self.calls.unlink(missing_ok=True)
                self.postgres()
                self.identity()
                self.pg_managed('REVIEW_IN_PROGRESS')
                self.queue('cloudformation describe-change-set', {'ChangeSetId': ARN, 'StackName': 'guito-postgres-shared', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'AVAILABLE', 'Changes': self.changes, 'Parameters': []})
                self.queue('cloudformation get-template', {'TemplateBody': self.template})
                if case == 'pending': self.cluster['PendingModifiedValues'] = {'EngineVersion': '18'}
                if case == 'capacity': self.cluster['ServerlessV2ScalingConfiguration'] = {'MinCapacity': 1, 'MaxCapacity': 16, 'SecondsUntilAutoPause': 300}
                if case == 'writer-class': self.writer['DBInstanceClass'] = 'db.r6g.large'
                if case == 'writer-subnet': self.writer['DBSubnetGroup'] = {'DBSubnetGroupName': 'other'}
                if case == 'unexpected-member': self.cluster['DBClusterMembers'][0]['DBInstanceIdentifier'] = 'other'
                if case == 'engine-version': self.cluster['EngineVersion'] = '18'
                self.pg_physical()
                result = self.run_cli('execute-import', 'postgres-shared', ('--change-set-arn', ARN))
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('Aurora', result.stdout)
                self.assertFalse(any('execute-change-set' in c for c in self.history))

    def test_missing_shared_stack_never_creates_aurora(self):
        self.postgres()
        self.identity()
        self.queue('cloudformation describe-stacks', error='ValidationError: Stack with id guito-postgres-shared does not exist')
        result = self.run_cli('ensure', 'postgres-shared')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any('create-change-set' in c for c in self.history))

    def test_access_denied_is_not_missing_and_raw_errors_never_escape(self):
        self.sessions()
        self.identity()
        self.queue('cloudformation describe-stacks', error='ValidationError AccessDenied PRIVATE_SECRET_SENTINEL')
        result = self.run_cli('ensure')
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('PRIVATE_SECRET_SENTINEL', result.stdout+result.stderr)
        self.assertEqual(len(self.history), 2)

    def test_plan_existing_owned_table_is_idempotent(self):
        self.sessions()
        self.identity()
        self.managed()
        self.ownership()
        self.physical()
        result = self.run_cli('plan-import')
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(json.loads(result.stdout)['status'], 'noop')
        self.assertFalse(any('create-change-set' in c for c in self.history))

    def test_incorrect_stack_ownership_cannot_be_adopted(self):
        self.sessions()
        self.identity()
        self.managed()
        self.ownership()
        self.steps[-2]['data']['StackResourceSummaries'][0]['PhysicalResourceId'] = 'some-other-table'
        result = self.run_cli('plan-import')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('ownership', result.stdout)
        self.assertFalse(any('create-change-set' in c for c in self.history))

    def test_post_import_drift_blocks_success(self):
        self.sessions()
        self.execute_preview()
        self.physical()
        self.queue('cloudformation execute-change-set')
        self.managed()
        self.managed()
        self.ownership()
        self.physical()
        self.drift('DRIFTED')
        result = self.run_cli('execute-import', extra=('--change-set-arn', ARN))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('drift', result.stdout)

    def test_change_set_polling_is_finite(self):
        self.sessions()
        self.plan()
        self.steps[-1]['data']['Status'] = 'CREATE_IN_PROGRESS'
        self.queue('cloudformation describe-change-set', self.steps[-1]['data'])
        result = self.run_cli('plan-import')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('poll budget exhausted', result.stdout)
        self.assertEqual(len([c for c in self.history if 'describe-change-set' in c]), 2)

    def test_plan_report_does_not_echo_aws_context_or_property_values(self):
        self.sessions()
        self.plan()
        self.steps[-1]['data']['Changes'][0]['ResourceChange']['BeforeContext'] = 'PRIVATE_SECRET_SENTINEL'
        self.steps[-1]['data']['Changes'][0]['ResourceChange']['Details'] = [{'Value': 'PRIVATE_SECRET_SENTINEL'}]
        result = self.run_cli('plan-import')
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertNotIn('PRIVATE_SECRET_SENTINEL', result.stdout+result.stderr)

    def test_template_macros_cannot_expand_unapproved_resources(self):
        self.sessions()
        self.template['Transform'] = 'unapproved-macro'
        (self.root/'infra/user-sessions.json').write_text(json.dumps(self.template))
        self.identity()
        result = self.run_cli('ensure')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('template structure', result.stdout)
        self.assertEqual(len(self.history), 1)

    def test_minerva_inspection_identity_cannot_plan_mutations(self):
        self.identity()
        self.steps[0]['data']['Arn'] = f'arn:aws:sts::{ACCOUNT}:assumed-role/MinervaAIAgent/test'
        result = self.run_cli('plan-import')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('read-only', result.stdout)
        self.assertEqual(len(self.history), 1)

    def pg_ownership(self):
        self.queue('cloudformation list-stack-resources', {'StackResourceSummaries': [{'LogicalResourceId': c['ResourceChange']['LogicalResourceId'], 'PhysicalResourceId': c['ResourceChange']['PhysicalResourceId'], 'ResourceType': c['ResourceChange']['ResourceType']} for c in self.changes]})
        self.queue('cloudformation get-template', {'TemplateBody': self.template})

    def pg_policy(self, installed=True):
        policy = {'Statement': [{'Effect': 'Allow', 'Principal': '*', 'Action': 'Update:*', 'Resource': '*'}, {'Effect': 'Deny', 'Principal': '*', 'Action': 'Update:*', 'Resource': ['LogicalResourceId/DatabaseCluster', 'LogicalResourceId/DatabaseWriter']}]}
        self.queue('cloudformation get-stack-policy', {'StackPolicyBody': json.dumps(policy)} if installed else {})

    def pg_preview(self, consumed=False):
        self.identity()
        self.pg_managed('IMPORT_COMPLETE' if consumed else 'REVIEW_IN_PROGRESS')
        self.queue('cloudformation describe-change-set', {'ChangeSetId': ARN, 'StackName': 'guito-postgres-shared', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'EXECUTE_COMPLETE' if consumed else 'AVAILABLE', 'Changes': self.changes, 'Parameters': []})
        self.queue('cloudformation get-template', {'TemplateBody': self.template})

    def pg_drift(self):
        self.queue('cloudformation detect-stack-drift', {'StackDriftDetectionId': 'drift-1'})
        self.queue('cloudformation describe-stack-drift-detection-status', {'DetectionStatus': 'DETECTION_COMPLETE', 'StackDriftStatus': 'IN_SYNC'})
        self.queue('cloudformation describe-stack-resource-drifts', {'StackResourceDrifts': [dict(c['ResourceChange'], StackResourceDriftStatus='IN_SYNC') for c in self.changes]})

    def test_first_shared_import_installs_protection_before_drift_failure(self):
        self.postgres()
        self.pg_preview()
        self.pg_physical()
        self.queue('cloudformation execute-change-set')
        self.pg_managed()
        self.pg_managed()
        self.pg_ownership()
        self.pg_physical()
        self.queue('cloudformation set-stack-policy')
        self.pg_policy()
        self.queue('cloudformation detect-stack-drift', error='AccessDenied')
        result = self.run_cli('execute-import', 'postgres-shared', ('--change-set-arn', ARN))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('detect-stack-drift failed', result.stdout)
        self.assertEqual(json.loads((self.root/'steps.json').read_text()), [])
        operations = [c[1] for c in self.history]
        self.assertLess(operations.index('set-stack-policy'), operations.index('detect-stack-drift'))

    def test_consumed_import_retry_finishes_without_second_execute(self):
        for installed in [False, True]:
            with self.subTest(installed=installed):
                self.steps = []
                self.calls.unlink(missing_ok=True)
                self.postgres()
                self.pg_preview(consumed=True)
                self.pg_ownership()
                self.pg_physical()
                self.pg_policy(installed)
                if not installed:
                    self.queue('cloudformation set-stack-policy')
                    self.pg_policy()
                self.pg_drift()
                self.pg_policy()
                result = self.run_cli('execute-import', 'postgres-shared', ('--change-set-arn', ARN))
                self.assertEqual(result.returncode, 0, result.stdout)
                self.assertEqual(json.loads(result.stdout)['status'], 'noop' if installed else 'imported')
                self.assertEqual(json.loads((self.root/'steps.json').read_text()), [])
                self.assertFalse(any('execute-change-set' in c for c in self.history))
                self.assertEqual(sum('set-stack-policy' in c for c in self.history), 0 if installed else 1)

    def test_consumed_session_import_revalidates_without_second_execute(self):
        self.sessions()
        self.execute_preview()
        self.steps[1]['data']['Stacks'][0]['StackStatus'] = 'IMPORT_COMPLETE'
        self.steps[1]['data']['Stacks'][0]['Parameters'][0]['UsePreviousValue'] = False
        self.steps[-2]['data']['ExecutionStatus'] = 'EXECUTE_COMPLETE'
        self.steps[-2]['data']['Parameters'][0]['UsePreviousValue'] = False
        self.ownership()
        self.physical()
        self.drift()
        result = self.run_cli('execute-import', extra=('--change-set-arn', ARN))
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(json.loads(result.stdout)['status'], 'noop')
        self.assertEqual(json.loads((self.root/'steps.json').read_text()), [])
        self.assertFalse(any('execute-change-set' in c for c in self.history))

    def test_completed_import_rechecks_protection_after_drift_before_success(self):
        self.postgres()
        self.pg_preview(consumed=True)
        self.pg_ownership()
        self.pg_physical()
        self.pg_policy()
        self.pg_drift()
        self.pg_policy(False)
        result = self.run_cli('execute-import', 'postgres-shared', ('--change-set-arn', ARN))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('protection', result.stdout)
        self.assertEqual(json.loads((self.root/'steps.json').read_text()), [])

    def test_consumed_import_retry_rejects_changed_evidence_before_metadata_writes(self):
        for case in ['template', 'stack', 'baseline', 'ownership', 'action', 'physical-id', 'stack-status']:
            with self.subTest(case=case):
                self.steps = []
                self.calls.unlink(missing_ok=True)
                self.postgres()
                self.pg_preview(consumed=True)
                if case == 'template': self.steps[-1]['data']['TemplateBody'] = dict(self.template, Description='changed')
                if case == 'stack': self.steps[-2]['data']['StackId'] += '-recreated'
                if case == 'action': self.changes[0]['ResourceChange']['Action'] = 'Modify'
                if case == 'physical-id': self.changes[0]['ResourceChange']['PhysicalResourceId'] = 'replaced'
                if case == 'stack-status': self.steps[1]['data']['Stacks'][0]['StackStatus'] = 'UPDATE_COMPLETE'
                self.pg_ownership()
                if case == 'ownership': self.steps[-2]['data']['StackResourceSummaries'][0]['PhysicalResourceId'] = 'replaced'
                if case == 'baseline': self.writer['PubliclyAccessible'] = False
                self.pg_physical()
                result = self.run_cli('execute-import', 'postgres-shared', ('--change-set-arn', ARN))
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any(c[1] in ['execute-change-set', 'set-stack-policy', 'detect-stack-drift'] for c in self.history))

    def test_shared_plan_noop_requires_protection_and_reports_original_arn_recovery(self):
        self.postgres()
        self.identity()
        self.pg_managed()
        self.pg_ownership()
        self.pg_physical()
        self.pg_policy(False)
        result = self.run_cli('plan-import', 'postgres-shared')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('execute-import', result.stdout)
        self.assertIn('original', result.stdout)
        self.assertFalse(any(c[1] in ['create-change-set', 'set-stack-policy'] for c in self.history))

    def test_shared_verify_requires_anti_update_stack_policy(self):
        self.postgres()
        self.identity()
        self.pg_managed()
        self.pg_ownership()
        self.pg_physical()
        self.queue('cloudformation get-stack-policy', {})
        result = self.run_cli('verify', 'postgres-shared')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('protection', result.stdout)

    def test_execute_rejects_same_named_recreated_stack(self):
        self.sessions()
        self.execute_preview()
        self.steps[-2]['data']['StackId'] += '-other'
        self.physical()
        self.queue('cloudformation execute-change-set')
        result = self.run_cli('execute-import', extra=('--change-set-arn', ARN))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('stack identity', result.stdout)
        self.assertFalse(any('execute-change-set' in c for c in self.history))

    def test_non_uuid_change_set_identifier_is_rejected(self):
        self.sessions()
        self.identity()
        self.managed('REVIEW_IN_PROGRESS')
        result = self.run_cli('execute-import', extra=('--change-set-arn', ARN.rsplit('/', 1)[0]+'/not-a-uuid'))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('ARN required', result.stdout)
        self.assertEqual(len(self.history), 2)

    def test_shared_plan_import_maps_both_existing_resources_without_execution(self):
        self.postgres()
        self.identity()
        self.queue('cloudformation describe-stacks', error='ValidationError: Stack with id guito-postgres-shared does not exist')
        self.cluster['AvailabilityZones'].reverse()
        self.pg_physical()
        self.queue('cloudformation get-template-summary', {'ResourceIdentifierSummaries': [{'LogicalResourceIds': ['DatabaseCluster'], 'ResourceType': 'AWS::RDS::DBCluster', 'ResourceIdentifiers': ['DBClusterIdentifier']}, {'LogicalResourceIds': ['DatabaseWriter'], 'ResourceType': 'AWS::RDS::DBInstance', 'ResourceIdentifiers': ['DBInstanceIdentifier']}]})
        self.queue('cloudformation create-change-set', {'Id': ARN})
        self.queue('cloudformation describe-change-set', {'ChangeSetId': ARN, 'StackName': 'guito-postgres-shared', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'AVAILABLE', 'Changes': self.changes, 'Parameters': []})
        result = self.run_cli('plan-import', 'postgres-shared')
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        create = next(c for c in self.history if 'create-change-set' in c)
        self.assertEqual(create[create.index('--change-set-type')+1], 'IMPORT')
        self.assertEqual(json.loads(create[create.index('--resource-types')+1]), ['AWS::RDS::DBCluster', 'AWS::RDS::DBInstance'])
        self.assertNotIn('--capabilities', create)
        self.assertEqual(json.loads(create[create.index('--resources-to-import')+1]), [{'LogicalResourceId': 'DatabaseCluster', 'ResourceType': 'AWS::RDS::DBCluster', 'ResourceIdentifier': {'DBClusterIdentifier': 'db-cluster'}}, {'LogicalResourceId': 'DatabaseWriter', 'ResourceType': 'AWS::RDS::DBInstance', 'ResourceIdentifier': {'DBInstanceIdentifier': 'guito'}}])
        self.assertFalse(any('execute-change-set' in c for c in self.history))

    def test_production_selects_only_production_table_and_parameters(self):
        self.sessions()
        self.table['TableName'] = 'guito-api-user-sessions'
        self.changes[0]['ResourceChange']['PhysicalResourceId'] = self.table['TableName']
        self.identity()
        self.queue('cloudformation describe-stacks', error='ValidationError: Stack with id guito-sessions-production does not exist')
        self.physical()
        self.queue('cloudformation get-template-summary', {'ResourceIdentifierSummaries': [{'LogicalResourceIds': ['UserSessions'], 'ResourceType': 'AWS::DynamoDB::Table', 'ResourceIdentifiers': ['TableName']}]})
        self.queue('cloudformation create-change-set', {'Id': ARN})
        self.queue('cloudformation describe-change-set', {'ChangeSetId': ARN, 'StackName': 'guito-sessions-production', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'AVAILABLE', 'Changes': self.changes, 'Parameters': [{'ParameterKey': 'Environment', 'ParameterValue': 'production'}]})
        result = self.run_cli('plan-import', 'sessions-production')
        self.assertEqual(result.returncode, 0, result.stdout+result.stderr)
        create = next(c for c in self.history if 'create-change-set' in c)
        self.assertEqual(json.loads(create[create.index('--parameters')+1]), [{'ParameterKey': 'Environment', 'ParameterValue': 'production'}])
        self.assertEqual(json.loads(create[create.index('--resources-to-import')+1])[0]['ResourceIdentifier'], {'TableName': 'guito-api-user-sessions'})

    def test_missing_physical_shared_cluster_never_creates_a_change_set(self):
        self.postgres()
        self.identity()
        self.queue('cloudformation describe-stacks', error='ValidationError: Stack with id guito-postgres-shared does not exist')
        self.queue('rds describe-db-clusters', error='DBClusterNotFoundFault: private-info')
        result = self.run_cli('plan-import', 'postgres-shared')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any('create-change-set' in c for c in self.history))
        self.assertNotIn('private-info', result.stdout+result.stderr)

    def test_unrecognized_failed_change_set_is_never_noop(self):
        self.sessions()
        self.plan()
        self.steps[-1]['data'] = {'Status': 'FAILED', 'StatusReason': 'AccessDenied private-info', 'Changes': []}
        result = self.run_cli('plan-import')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('unavailable', result.stdout)
        self.assertNotIn('private-info', result.stdout+result.stderr)

    def test_auto_import_change_set_is_not_explicit_import_approval(self):
        self.sessions()
        self.execute_preview()
        self.steps[-2]['data']['ImportExistingResources'] = True
        self.physical()
        self.queue('cloudformation execute-change-set')
        result = self.run_cli('execute-import', extra=('--change-set-arn', ARN))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('automatic import', result.stdout)
        self.assertFalse(any('execute-change-set' in c for c in self.history))

    def test_update_requires_explicit_no_replacement(self):
        self.sessions()
        self.identity()
        self.managed()
        self.ownership()
        self.steps[-1]['data']['TemplateBody'] = dict(self.template, Description='old')
        self.physical()
        self.queue('cloudformation create-change-set', {'Id': ARN})
        changes = json.loads(json.dumps(self.changes))
        changes[0]['ResourceChange'].update(Action='Modify')
        changes[0]['ResourceChange'].pop('Replacement')
        self.queue('cloudformation describe-change-set', {'StackName': 'guito-sessions-staging', 'Status': 'CREATE_COMPLETE', 'ExecutionStatus': 'AVAILABLE', 'Changes': changes, 'Parameters': [{'ParameterKey': 'Environment', 'ParameterValue': 'staging'}]})
        self.queue('cloudformation execute-change-set')
        result = self.run_cli('ensure')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('replacement', result.stdout)
        self.assertFalse(any('execute-change-set' in c for c in self.history))

    def test_wrong_account_rejected_before_storage_access(self):
        self.identity('000000000000')
        result = self.run_cli()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('account', result.stdout)
        self.assertEqual(len(self.history), 1)


if __name__ == '__main__':
    unittest.main()
