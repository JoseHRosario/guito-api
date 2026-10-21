#!/usr/bin/env python3
"""Fail-closed, metadata-only storage lifecycle controller; AWS CLI + stdlib only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time
from typing import Any
import uuid

ACCOUNT = '497087877832'
REGION = 'eu-west-1'
ROOT = Path(__file__).resolve().parents[1]


class GuardError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise GuardError(message)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'))


def aws(service, operation, **options) -> Any:
    command = ['aws', service, operation]
    for key, value in options.items():
        command += ['--'+key.replace('_', '-'), value if isinstance(value, str) else canonical(value)]
    command += ['--region', REGION, '--output', 'json', '--no-cli-pager', '--cli-connect-timeout', '5', '--cli-read-timeout', '20']
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=30,
                                env=dict(os.environ, AWS_MAX_ATTEMPTS='1', AWS_RETRY_MODE='standard'))
    except (OSError, subprocess.TimeoutExpired):
        raise GuardError(f'{service}:{operation} transport failure') from None
    if result.returncode:
        # Never expose AWS payloads or raw exceptions (including credentials).
        missing_stack = operation == 'describe-stacks' and re.search(r'ValidationError.*Stack with id [\w-]+ does not exist', result.stderr)
        missing_table = operation == 'describe-table' and re.search(r'An error occurred \(ResourceNotFoundException\) when calling the DescribeTable operation', result.stderr)
        if missing_stack or missing_table:
            return None
        raise GuardError(aws_failure(service, operation, result.stderr))
    try:
        return json.loads(result.stdout or '{}')
    except ValueError:
        raise GuardError(f'{service}:{operation} invalid JSON') from None


def aws_failure(service, operation, stderr):
    message = f'{service}:{operation} failed'
    code = re.search(r'An error occurred \(([A-Za-z0-9]+)\)', stderr)
    if code is None:
        return message
    message += ' (' + code[1] + ')'
    # Only fixed public infrastructure inputs are eligible for validation detail.
    if service != 'cloudformation' or operation != 'create-change-set' or code[1] != 'ValidationError':
        return message
    detail = re.search(r'CreateChangeSet operation(?: \([^\n]*\))?: ([^\n]+)', stderr)
    if detail and not re.search(r'credential|secret|password|token|access.?key|signature', detail[1], re.IGNORECASE):
        message += ': ' + detail[1][:700]
    return message


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['ensure', 'verify', 'plan-import', 'execute-import'])
    parser.add_argument('--target', required=True, choices=['sessions-staging', 'sessions-production', 'postgres-shared'])
    parser.add_argument('--change-set-arn')
    parser.add_argument('--output-dir', type=Path)
    parser.add_argument('--poll-interval', type=float, default=5)
    parser.add_argument('--max-polls', type=int, default=120)
    args = parser.parse_args()
    report = {'target': args.target, 'action': args.action, 'account': ACCOUNT, 'region': REGION}
    try:
        require(args.target != 'postgres-shared' or args.action == 'verify',
                'Aurora adoption is owner-paused; postgres-shared is template-only, not deployable')
        require(0 <= args.poll_interval <= 30 and 1 <= args.max_polls <= 180, 'invalid poll budget')
        identity = aws('sts', 'get-caller-identity')
        require(identity.get('Account') == ACCOUNT, 'unexpected AWS account')
        require(re.fullmatch(r'arn:aws:sts::'+ACCOUNT+r':assumed-role/(guito-api-deploy|MinervaAIAgent)/[^/]+', identity.get('Arn', '')), 'unapproved AWS identity')
        require('/MinervaAIAgent/' not in identity['Arn'] or args.action == 'verify', 'Minerva inspection identity is read-only')
        report.update(run(args))
    except (GuardError, KeyError, TypeError, ValueError, OSError, IndexError, AttributeError) as error:
        report.update(status='blocked', error=str(error) if isinstance(error, GuardError) else 'invalid local or AWS metadata')
    text = json.dumps(report, sort_keys=True)
    if args.output_dir:
        args.output_dir.mkdir(parents=True, exist_ok=True)
        (args.output_dir/'infrastructure-plan.json').write_text(text+'\n')
    print(text)
    return 1 if report.get('status') == 'blocked' else 0


def context(args):
    shared = args.target == 'postgres-shared'
    template = json.loads((ROOT/'infra'/('postgres-shared.json' if shared else 'user-sessions.json')).read_text())
    environment = args.target.removeprefix('sessions-')
    require(set(template) <= {'AWSTemplateFormatVersion', 'Description', 'Metadata', 'Parameters', 'Conditions', 'Resources', 'Outputs'}, 'unsafe template structure')
    # Native IMPORT cannot add Outputs; identities come from ListStackResources.
    require(not template.get('Outputs'), 'adoption templates must not define Outputs')
    resources = template['Resources']
    require(all(set(resource) <= {'Type', 'DeletionPolicy', 'UpdateReplacePolicy', 'Metadata', 'Properties'} for resource in resources.values()), 'unsafe template resource structure')
    expected = {'DatabaseCluster': ('AWS::RDS::DBCluster', 'DBClusterIdentifier', 'db-cluster'), 'DatabaseWriter': ('AWS::RDS::DBInstance', 'DBInstanceIdentifier', 'guito')} if shared else {'UserSessions': ('AWS::DynamoDB::Table', 'TableName', 'guito-api-user-sessions' + ('' if environment == 'production' else '-staging'))}
    require(set(resources) == set(expected), 'unexpected template resources')
    for logical, (kind, _, _) in expected.items():
        resource = resources[logical]
        require(resource['Type'] == kind and resource.get('DeletionPolicy') == 'Retain' and resource.get('UpdateReplacePolicy') == 'Retain', 'unsafe resource retention/type')
    parameters = [] if shared else [{'ParameterKey': 'Environment', 'ParameterValue': environment}]
    require(set(template.get('Parameters', {})) == (set() if shared else {'Environment'}), 'unexpected template parameters')
    if not shared:
        validate_sessions(template)
    return {'template': template, 'expected': expected, 'parameters': parameters, 'stack': 'guito-'+args.target, 'shared': shared}


def validate_sessions(template):
    require(template.get('Conditions') == {'IsProduction': {'Fn::Equals': [{'Ref': 'Environment'}, 'production']}}, 'unsafe session conditions')
    require(set(template['Parameters']['Environment']['AllowedValues']) == {'staging', 'production'}, 'unsafe environments')
    properties = template['Resources']['UserSessions']['Properties']
    expected = {'TableName': {'Fn::If': ['IsProduction', 'guito-api-user-sessions', 'guito-api-user-sessions-staging']}, 'BillingMode': 'PROVISIONED', 'TableClass': 'STANDARD', 'AttributeDefinitions': [{'AttributeName': 'handle_hash', 'AttributeType': 'S'}], 'KeySchema': [{'AttributeName': 'handle_hash', 'KeyType': 'HASH'}], 'ProvisionedThroughput': {'ReadCapacityUnits': 1, 'WriteCapacityUnits': 1}, 'TimeToLiveSpecification': {'AttributeName': 'ttl', 'Enabled': True}, 'DeletionProtectionEnabled': True, 'PointInTimeRecoverySpecification': {'PointInTimeRecoveryEnabled': False}}
    require(properties == expected, 'unsafe session table properties')


def stack_state(ctx):
    result = aws('cloudformation', 'describe-stacks', stack_name=ctx['stack'])
    if result is None:
        return None
    require(len(result['Stacks']) == 1, 'ambiguous stack')
    stack = result['Stacks'][0]
    require(stack['StackName'] == ctx['stack'], 'unexpected stack name')
    require(not stack.get('RoleARN'), 'unapproved CloudFormation service role')
    return stack


def check_sessions(ctx, allow_missing=False):
    name = ctx['expected']['UserSessions'][2]
    response = aws('dynamodb', 'describe-table', table_name=name)
    if response is None:
        require(allow_missing, 'session table missing')
        return False
    table = response['Table']
    expected = ctx['template']['Resources']['UserSessions']['Properties']
    require(table['TableName'] == name and table['TableStatus'] == 'ACTIVE', 'unexpected session table/status')
    for key in ['AttributeDefinitions', 'KeySchema', 'DeletionProtectionEnabled']:
        require(table.get(key) == expected[key], 'session property mismatch: '+key)
    require(table.get('TableClassSummary', {}).get('TableClass', 'STANDARD') == 'STANDARD', 'session table class mismatch')
    require(table.get('BillingModeSummary', {}).get('BillingMode', 'PROVISIONED') == 'PROVISIONED', 'session billing mismatch')
    require(all(table['ProvisionedThroughput'].get(k) == 1 for k in ['ReadCapacityUnits', 'WriteCapacityUnits']), 'session capacity mismatch')
    require(not any(table.get(k) for k in ['GlobalSecondaryIndexes', 'LocalSecondaryIndexes', 'SSEDescription', 'Replicas']) and not table.get('StreamSpecification', {}).get('StreamEnabled'), 'unexpected session paid features')
    ttl = aws('dynamodb', 'describe-time-to-live', table_name=name)['TimeToLiveDescription']
    require(ttl == {'AttributeName': 'ttl', 'TimeToLiveStatus': 'ENABLED'}, 'session TTL mismatch')
    backup = aws('dynamodb', 'describe-continuous-backups', table_name=name)['ContinuousBackupsDescription']
    require(backup['PointInTimeRecoveryDescription']['PointInTimeRecoveryStatus'] == 'DISABLED', 'session PITR mismatch')
    return True


def poll(args, fetch, done, label):
    for index in range(args.max_polls):
        value = fetch()
        if done(value):
            return value
        if index+1 < args.max_polls:
            time.sleep(args.poll_interval)
    raise GuardError(label+' poll budget exhausted')


def check_parameters(actual, expected):
    actual = [] if actual is None else actual
    message = 'unexpected stack/change-set parameters'
    require(isinstance(actual, list), message)
    normalized = []
    for parameter in actual:
        require(isinstance(parameter, dict) and
                {'ParameterKey', 'ParameterValue'} <= set(parameter) <=
                {'ParameterKey', 'ParameterValue', 'UsePreviousValue', 'ResolvedValue'}, message)
        require('UsePreviousValue' not in parameter or parameter['UsePreviousValue'] is False, message)
        require('ResolvedValue' not in parameter or parameter['ResolvedValue'] == '', message)
        normalized.append({key: parameter[key] for key in ['ParameterKey', 'ParameterValue']})
    require(len({p['ParameterKey'] for p in normalized}) == len(normalized), message)
    require(sorted(normalized, key=lambda p: p['ParameterKey']) ==
            sorted(expected, key=lambda p: p['ParameterKey']), message)


def validate_changes(ctx, change_set, kind):
    # DescribeChangeSet has no ChangeSetType response field in the AWS API.
    # Exact all-Import actions (with no adds/modifies/removes) prove import-only intent.
    require(change_set.get('StackName') == ctx['stack'] and change_set.get('ChangeSetType', kind) == kind, 'unexpected change-set stack/type')
    require(not change_set.get('RoleARN') and not change_set.get('NextToken') and not change_set.get('IncludeNestedStacks'), 'unapproved role or incomplete change set')
    require(not change_set.get('ImportExistingResources') and not change_set.get('Capabilities') and not change_set.get('DeploymentMode'), 'automatic import/capabilities/deployment mode forbidden')
    check_parameters(change_set.get('Parameters', []), ctx['parameters'])
    changes = change_set.get('Changes', [])
    require(len(changes) == len(ctx['expected']), 'unexpected change count')
    seen = set()
    for change in changes:
        resource = change['ResourceChange']
        logical = resource['LogicalResourceId']
        require(logical in ctx['expected'] and logical not in seen, 'unexpected change resource')
        seen.add(logical)
        require(resource['ResourceType'] == ctx['expected'][logical][0], 'unexpected resource type')
        require(resource['Action'] == {'IMPORT': 'Import', 'CREATE': 'Add', 'UPDATE': 'Modify'}[kind], 'unexpected change action')
        require(resource.get('Replacement') in (['False'] if kind == 'UPDATE' else [None, 'False']), 'replacement forbidden or unspecified')
        if kind == 'IMPORT':
            require(resource.get('PhysicalResourceId') == ctx['expected'][logical][2], 'unexpected imported physical resource')
    safe_keys = ['Action', 'LogicalResourceId', 'PhysicalResourceId', 'ResourceType', 'Replacement']
    return [{'ResourceChange': {key: change['ResourceChange'][key] for key in safe_keys if key in change['ResourceChange']}} for change in changes]


def create_plan(args, ctx, kind):
    options = dict(stack_name=ctx['stack'], change_set_name='guito-infra-'+uuid.uuid4().hex, change_set_type=kind, template_body=canonical(ctx['template']), resource_types=sorted({row[0] for row in ctx['expected'].values()}))
    if ctx['parameters']:
        options['parameters'] = ctx['parameters']
    if kind == 'IMPORT':
        summary = aws('cloudformation', 'get-template-summary', template_body=canonical(ctx['template']))
        identifiers = {logical: (row['ResourceType'], row['ResourceIdentifiers']) for row in summary['ResourceIdentifierSummaries'] for logical in row['LogicalResourceIds']}
        require(identifiers == {logical: (kind_, [key]) for logical, (kind_, key, _) in ctx['expected'].items()}, 'unexpected import identifier schema')
        options['resources_to_import'] = [{'ResourceType': kind_, 'LogicalResourceId': logical, 'ResourceIdentifier': {key: name}} for logical, (kind_, key, name) in ctx['expected'].items()]
    arn = aws('cloudformation', 'create-change-set', **options)['Id']
    change_set = poll(args, lambda: aws('cloudformation', 'describe-change-set', change_set_name=arn), lambda c: c.get('Status') in ['CREATE_COMPLETE', 'FAILED'], 'change set')
    if kind == 'UPDATE' and change_set['Status'] == 'FAILED' and not change_set.get('Changes') and change_set.get('StatusReason') in ["The submitted information didn't contain changes. Submit different information to create a change set.", "No updates are to be performed."]:
        return {'status': 'noop', 'change_set_arn': arn}
    require(change_set['Status'] == 'CREATE_COMPLETE' and change_set.get('ExecutionStatus') == 'AVAILABLE', 'change set unavailable')
    changes = validate_changes(ctx, change_set, kind)
    return {'status': 'planned', 'change_set_arn': arn, 'changes': changes, 'template_sha256': hashlib.sha256(canonical(ctx['template']).encode()).hexdigest()}


def validate_arn(arn):
    require(isinstance(arn, str) and re.fullmatch(r'arn:aws:cloudformation:'+REGION+':'+ACCOUNT+r':changeSet/guito-infra-[A-Za-z0-9-]+/[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}', arn), 'full approved change-set ARN required')


def same_template(ctx, **options):
    body = get_template(**options)
    require(body == ctx['template'], 'source template mismatch; create a fresh plan')


def get_template(**options):
    body = aws('cloudformation', 'get-template', **options)['TemplateBody']
    return json.loads(body) if isinstance(body, str) else body


def verify_managed(ctx, stack, allow_session_update=False):
    require(stack is not None and stack['StackStatus'] in ['CREATE_COMPLETE', 'UPDATE_COMPLETE', 'IMPORT_COMPLETE'], 'stack absent/incomplete; explicit import required')
    check_parameters(stack.get('Parameters', []), ctx['parameters'])
    response = aws('cloudformation', 'list-stack-resources', stack_name=ctx['stack'])
    require(not response.get('NextToken'), 'incomplete resource ownership listing')
    resources = response['StackResourceSummaries']
    actual = {row['LogicalResourceId']: (row['ResourceType'], row['PhysicalResourceId']) for row in resources}
    require(len(resources) == len(ctx['expected']) and actual == {logical: (kind, name) for logical, (kind, _, name) in ctx['expected'].items()}, 'stack resource ownership mismatch')
    deployed = get_template(stack_name=ctx['stack'])
    if allow_session_update:
        validate_sessions(deployed)
        require(set(deployed['Resources']) == {'UserSessions'} and deployed['Resources']['UserSessions']['Type'] == 'AWS::DynamoDB::Table', 'unexpected deployed session resources')
        require(all(deployed['Resources']['UserSessions'].get(k) == 'Retain' for k in ['DeletionPolicy', 'UpdateReplacePolicy']), 'unsafe deployed retention')
    else:
        require(deployed == ctx['template'], 'source template mismatch; create a fresh plan')
    check_physical(ctx)
    return deployed == ctx['template']


def check_postgres(ctx):
    clusters = aws('rds', 'describe-db-clusters', db_cluster_identifier='db-cluster')['DBClusters']
    writers = aws('rds', 'describe-db-instances', db_instance_identifier='guito')['DBInstances']
    require(len(clusters) == 1 and len(writers) == 1, 'missing/ambiguous shared Aurora resources')
    cluster, writer = clusters[0], writers[0]
    require(cluster.get('Status') == 'available' and writer.get('DBInstanceStatus') == 'available', 'shared Aurora not available')
    require(not cluster.get('PendingModifiedValues') and not writer.get('PendingModifiedValues'), 'pending Aurora configuration changes')
    members = cluster['DBClusterMembers']
    require(len(members) == 1 and members[0]['DBInstanceIdentifier'] == 'guito' and members[0]['IsClusterWriter'] is True and members[0].get('DBClusterParameterGroupStatus') == 'in-sync', 'unexpected Aurora writer ownership/status')
    desired_cluster = ctx['template']['Resources']['DatabaseCluster']['Properties']
    desired_writer = ctx['template']['Resources']['DatabaseWriter']['Properties']
    require(desired_cluster.get('DBClusterIdentifier') == 'db-cluster' and desired_writer.get('DBInstanceIdentifier') == 'guito' and desired_writer.get('DBClusterIdentifier') == {'Ref': 'DatabaseCluster'}, 'unexpected Aurora template identity')
    require(writer.get('EngineVersion') == desired_cluster['EngineVersion'] and writer['DBSubnetGroup']['DBSubnetGroupName'] == desired_cluster['DBSubnetGroupName'], 'Aurora writer cluster relation mismatch')
    require(all(group['Status'] == 'active' for group in cluster['VpcSecurityGroups']), 'Aurora security group change pending')
    compare_postgres(desired_cluster, cluster, {
        'DBClusterParameterGroupName': cluster.get('DBClusterParameterGroup'),
        'DBSubnetGroupName': cluster.get('DBSubnetGroup'),
        'VpcSecurityGroupIds': [g['VpcSecurityGroupId'] for g in cluster['VpcSecurityGroups']],
        'EnableHttpEndpoint': cluster.get('HttpEndpointEnabled'),
        'EnableIAMDatabaseAuthentication': cluster.get('IAMDatabaseAuthenticationEnabled')})
    groups = writer['DBParameterGroups']
    require(len(groups) == 1 and groups[0]['ParameterApplyStatus'] == 'in-sync', 'Aurora parameter group pending')
    compare_postgres(desired_writer, writer, {
        'DBParameterGroupName': groups[0]['DBParameterGroupName'],
        'EnablePerformanceInsights': writer.get('PerformanceInsightsEnabled')}, writer=True)
    return True


def compare_postgres(desired, live, mapped, writer=False):
    # An unknown managed field is never silently omitted from comparison.
    allowed = {'DBInstanceIdentifier', 'DBClusterIdentifier', 'DBInstanceClass', 'Engine', 'DBParameterGroupName', 'PubliclyAccessible', 'AvailabilityZone', 'AutoMinorVersionUpgrade', 'PromotionTier', 'CACertificateIdentifier', 'MonitoringInterval', 'EnablePerformanceInsights', 'PreferredMaintenanceWindow'} if writer else {'DBClusterIdentifier', 'Engine', 'EngineMode', 'EngineVersion', 'DatabaseName', 'MasterUsername', 'Port', 'StorageEncrypted', 'DeletionProtection', 'BackupRetentionPeriod', 'PreferredBackupWindow', 'PreferredMaintenanceWindow', 'CopyTagsToSnapshot', 'DBClusterParameterGroupName', 'DBSubnetGroupName', 'VpcSecurityGroupIds', 'AvailabilityZones', 'ServerlessV2ScalingConfiguration', 'EnableHttpEndpoint', 'EnableIAMDatabaseAuthentication', 'NetworkType', 'AutoMinorVersionUpgrade', 'EngineLifecycleSupport'}
    require(set(desired) == allowed, 'unexpected/missing Aurora managed properties')
    for key, expected in desired.items():
        actual = mapped[key] if key in mapped else live.get(key)
        if key == 'DBClusterIdentifier' and writer:
            expected = 'db-cluster'
        if key in ['VpcSecurityGroupIds', 'AvailabilityZones']:
            actual, expected = sorted(actual), sorted(expected)
        if key == 'ServerlessV2ScalingConfiguration':
            actual = {k: actual.get(k) for k in expected} if isinstance(actual, dict) else actual
        require(actual == expected, 'Aurora live property mismatch: '+key)


def postgres_policy():
    return {'Statement': [{'Effect': 'Allow', 'Principal': '*', 'Action': 'Update:*', 'Resource': '*'}, {'Effect': 'Deny', 'Principal': '*', 'Action': 'Update:*', 'Resource': ['LogicalResourceId/DatabaseCluster', 'LogicalResourceId/DatabaseWriter']}]}


def protect_postgres(ctx):
    policy = postgres_policy()
    aws('cloudformation', 'set-stack-policy', stack_name=ctx['stack'], stack_policy_body=canonical(policy))
    verify_postgres_policy(ctx)


def verify_postgres_policy(ctx):
    require(postgres_is_protected(ctx), 'shared Aurora stack protection missing/mismatched; recover with execute-import and the original reviewed change-set ARN')


def postgres_is_protected(ctx):
    result = aws('cloudformation', 'get-stack-policy', stack_name=ctx['stack'])
    return json.loads(result.get('StackPolicyBody', '{}')) == postgres_policy()


def check_physical(ctx):
    return check_postgres(ctx) if ctx['shared'] else check_sessions(ctx)


def verify_drift(args, ctx):
    detection = aws('cloudformation', 'detect-stack-drift', stack_name=ctx['stack'])['StackDriftDetectionId']
    result = poll(args, lambda: aws('cloudformation', 'describe-stack-drift-detection-status', stack_drift_detection_id=detection), lambda d: d.get('DetectionStatus') in ['DETECTION_COMPLETE', 'DETECTION_FAILED'], 'drift')
    require(result.get('DetectionStatus') == 'DETECTION_COMPLETE' and result.get('StackDriftStatus') == 'IN_SYNC', 'stack drift or failed detection')
    response = aws('cloudformation', 'describe-stack-resource-drifts', stack_name=ctx['stack'])
    require(not response.get('NextToken'), 'incomplete resource drift result')
    rows = response['StackResourceDrifts']
    require(len(rows) == len(ctx['expected']), 'missing checked resource drift')
    require({row['LogicalResourceId'] for row in rows} == set(ctx['expected']), 'unexpected drift resource')
    for row in rows:
        kind, _, name = ctx['expected'][row['LogicalResourceId']]
        require(row['StackResourceDriftStatus'] == 'IN_SYNC' and row['ResourceType'] == kind and row['PhysicalResourceId'] == name, 'resource drift mismatch')


def wait_stack(args, ctx, kind):
    wanted = {'IMPORT': 'IMPORT_COMPLETE', 'CREATE': 'CREATE_COMPLETE', 'UPDATE': 'UPDATE_COMPLETE'}[kind]
    result = poll(args, lambda: stack_state(ctx), lambda s: s is None or not s['StackStatus'].endswith('_IN_PROGRESS'), 'stack')
    require(result is not None and result['StackStatus'] == wanted, 'stack operation failed')


def execute_import(args, ctx, stack):
    validate_arn(args.change_set_arn)
    require(stack is not None and stack['StackStatus'] in ['REVIEW_IN_PROGRESS', 'CREATE_COMPLETE', 'UPDATE_COMPLETE', 'IMPORT_COMPLETE'], 'unexpected import stack state')
    change_set = aws('cloudformation', 'describe-change-set', change_set_name=args.change_set_arn)
    require(change_set.get('ChangeSetId') == args.change_set_arn, 'unexpected change-set ARN')
    require(change_set.get('StackId') == stack.get('StackId') and bool(stack.get('StackId')), 'change-set stack identity mismatch')
    validate_changes(ctx, change_set, 'IMPORT')
    consumed = change_set.get('ExecutionStatus') == 'EXECUTE_COMPLETE'
    require(change_set['Status'] == 'CREATE_COMPLETE' and
            change_set.get('ExecutionStatus') in ['AVAILABLE', 'EXECUTE_COMPLETE'], 'import not executable')
    require(not consumed or stack['StackStatus'] == 'IMPORT_COMPLETE', 'completed import stack state mismatch')
    same_template(ctx, change_set_name=args.change_set_arn)
    # Aurora import can apply template settings: compare immediately before Execute.
    if not consumed:
        check_physical(ctx)
        aws('cloudformation', 'execute-change-set', change_set_name=args.change_set_arn)
        wait_stack(args, ctx, 'IMPORT')
        stack = stack_state(ctx)
        require(stack.get('StackId') == change_set['StackId'], 'change-set stack identity mismatch')
    verify_managed(ctx, stack)
    already_protected = consumed and (not ctx['shared'] or postgres_is_protected(ctx))
    if ctx['shared'] and not already_protected:
        protect_postgres(ctx)
    verify_drift(args, ctx)
    if ctx['shared']:
        verify_postgres_policy(ctx)
    return {'status': 'noop' if already_protected else 'imported', 'change_set_arn': args.change_set_arn, 'template_sha256': hashlib.sha256(canonical(ctx['template']).encode()).hexdigest()}


def run(args):
    ctx = context(args)
    stack = stack_state(ctx)
    if args.action == 'execute-import':
        return execute_import(args, ctx, stack)
    if args.action == 'plan-import':
        if stack is not None:
            verify_managed(ctx, stack)
            if ctx['shared']:
                verify_postgres_policy(ctx)
            return {'status': 'noop'}
        check_physical(ctx)
        return create_plan(args, ctx, 'IMPORT')
    if args.action == 'verify':
        verify_managed(ctx, stack)
        if ctx['shared']:
            verify_postgres_policy(ctx)
        return {'status': 'verified'}
    if args.action == 'ensure':
        if ctx['shared']:
            verify_managed(ctx, stack)
            return {'status': 'noop'}
        if stack is not None:
            identical = verify_managed(ctx, stack, allow_session_update=True)
            if identical:
                return {'status': 'noop'}
            plan = create_plan(args, ctx, 'UPDATE')
            if plan['status'] == 'noop':
                verify_managed(ctx, stack_state(ctx), allow_session_update=True)
                return plan
            aws('cloudformation', 'execute-change-set', change_set_name=plan['change_set_arn'])
            wait_stack(args, ctx, 'UPDATE')
            verify_managed(ctx, stack_state(ctx))
            return dict(plan, status='updated')
        require(not check_sessions(ctx, allow_missing=True), 'existing table requires explicit plan-import')
        plan = create_plan(args, ctx, 'CREATE')
        aws('cloudformation', 'execute-change-set', change_set_name=plan['change_set_arn'])
        wait_stack(args, ctx, 'CREATE')
        verify_managed(ctx, stack_state(ctx))
        return dict(plan, status='created')
    raise GuardError('unsupported lifecycle action')


if __name__ == '__main__':
    raise SystemExit(main())
