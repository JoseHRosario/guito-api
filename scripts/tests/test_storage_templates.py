"""Checked-in native storage templates are the approved adoption boundary."""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def template(name):
    return json.loads((ROOT / "infra" / name).read_text(encoding="utf-8"))


class StorageTemplateTests(unittest.TestCase):
    def test_storage_resources_keep_identity_and_retention(self):
        expected = {
            "user-sessions.json": {"UserSessions": "AWS::DynamoDB::Table"},
            "postgres-shared.json": {
                "DatabaseCluster": "AWS::RDS::DBCluster",
                "DatabaseWriter": "AWS::RDS::DBInstance",
            },
        }

        for name, resources in expected.items():
            with self.subTest(template=name):
                document = template(name)
                self.assertEqual(resources, {
                    key: resource["Type"] for key, resource in document["Resources"].items()
                })
                self.assertEqual("aws-cloudformation@2", document["Metadata"]["AWSToolsMetrics"]["AWSAgentToolkit"])
                self.assertNotIn("Transform", document)
                for resource in document["Resources"].values():
                    self.assertEqual("Retain", resource["DeletionPolicy"])
                    self.assertEqual("Retain", resource["UpdateReplacePolicy"])
                    context = resource["Metadata"]["com.aws.cloudformation.Context"]
                    self.assertTrue(context["why"])
                    self.assertTrue(context["must"])

    def test_sessions_remain_fixed_capacity_and_environment_isolated(self):
        document = template("user-sessions.json")
        properties = document["Resources"]["UserSessions"]["Properties"]

        self.assertEqual(["staging", "production"], document["Parameters"]["Environment"]["AllowedValues"])
        self.assertNotIn("Default", document["Parameters"]["Environment"])
        self.assertEqual({"Fn::Equals": [{"Ref": "Environment"}, "production"]}, document["Conditions"]["IsProduction"])
        self.assertEqual({"Fn::If": ["IsProduction", "guito-api-user-sessions", "guito-api-user-sessions-staging"]}, properties["TableName"])
        self.assertEqual("STANDARD", properties["TableClass"])
        self.assertEqual("PROVISIONED", properties["BillingMode"])
        self.assertEqual({"ReadCapacityUnits": 1, "WriteCapacityUnits": 1}, properties["ProvisionedThroughput"])
        self.assertEqual([{"AttributeName": "handle_hash", "AttributeType": "S"}], properties["AttributeDefinitions"])
        self.assertEqual([{"AttributeName": "handle_hash", "KeyType": "HASH"}], properties["KeySchema"])
        self.assertEqual({"AttributeName": "ttl", "Enabled": True}, properties["TimeToLiveSpecification"])
        self.assertTrue(properties["DeletionProtectionEnabled"])
        self.assertEqual({"PointInTimeRecoveryEnabled": False}, properties["PointInTimeRecoverySpecification"])
        for paid_or_unapproved in ("SSESpecification", "GlobalSecondaryIndexes", "LocalSecondaryIndexes", "StreamSpecification", "Replicas", "OnDemandThroughput", "WarmThroughput"):
            self.assertNotIn(paid_or_unapproved, properties)

    def test_shared_postgres_references_the_existing_cluster_without_credentials(self):
        document = template("postgres-shared.json")
        cluster = document["Resources"]["DatabaseCluster"]["Properties"]
        writer = document["Resources"]["DatabaseWriter"]["Properties"]

        self.assertNotIn("Parameters", document)
        self.assertEqual("db-cluster", cluster["DBClusterIdentifier"])
        self.assertEqual("guito", writer["DBInstanceIdentifier"])
        self.assertEqual({"Ref": "DatabaseCluster"}, writer["DBClusterIdentifier"])
        self.assertEqual("aurora-postgresql", cluster["Engine"])
        self.assertEqual("17.9", cluster["EngineVersion"])
        self.assertEqual("db.serverless", writer["DBInstanceClass"])
        self.assertEqual({"MinCapacity": 0, "MaxCapacity": 16, "SecondsUntilAutoPause": 300}, cluster["ServerlessV2ScalingConfiguration"])
        self.assertTrue(cluster["EnableHttpEndpoint"])
        self.assertNotIn("EngineVersion", writer)
        self.assertNotIn("DBSubnetGroupName", writer)
        for properties in (cluster, writer):
            for credential_change in ("MasterUserPassword", "ManageMasterUserPassword", "MasterUserSecret", "SnapshotIdentifier"):
                self.assertNotIn(credential_change, properties)
        expected_arn = "arn:aws:rds:eu-west-1:497087877832:cluster:" + cluster["DBClusterIdentifier"]
        for environment in ("Staging", "Production"):
            config = json.loads((ROOT / "src" / "guito-api" / f"appsettings.{environment}.json").read_text())
            self.assertEqual(expected_arn, config["Database"]["ResourceArn"])

    def test_import_template_records_existing_risks_instead_of_silently_changing_them(self):
        document = template("postgres-shared.json")
        cluster = document["Resources"]["DatabaseCluster"]["Properties"]
        writer = document["Resources"]["DatabaseWriter"]["Properties"]
        context = " ".join(document["Metadata"]["com.aws.cloudformation.Context"]["must"])

        self.assertFalse(cluster["StorageEncrypted"])
        self.assertFalse(cluster["DeletionProtection"])
        self.assertTrue(writer["PubliclyAccessible"])
        self.assertIn("hardening requires separate approval", context)
        self.assertIn("IMPORT only", context)

    def test_operator_inspection_policy_has_no_stack_or_datastore_writes(self):
        policy = json.loads((ROOT / "deploy" / "storage-inspection-policy.json").read_text())
        statements = policy["Statement"]
        actions = {action for statement in statements for action in statement["Action"]}

        self.assertTrue(actions)
        self.assertTrue(all(action.startswith("cloudformation:") for action in actions))
        self.assertTrue(all(action.split(":", 1)[1].startswith(("Describe", "List", "Get", "Validate")) for action in actions))
        for statement in statements:
            self.assertEqual("Allow", statement["Effect"])
            if statement["Resource"] == "*":
                self.assertEqual("eu-west-1", statement["Condition"]["StringEquals"]["aws:RequestedRegion"])
            else:
                for resource in statement["Resource"]:
                    self.assertTrue(resource.startswith("arn:aws:cloudformation:eu-west-1:497087877832:"))
                    self.assertIn(resource.split(":", 5)[5].split("/", 2)[1], {
                        "guito-sessions-staging", "guito-sessions-production", "guito-postgres-shared", "guito-infra-*"
                    })

    def test_drift_permission_closure_is_explicit_and_scoped(self):
        policy = json.loads((ROOT / 'deploy/storage-cloudformation-policy.json').read_text())
        by_action = {action: statement for statement in policy['Statement']
                     for action in (statement['Action'] if isinstance(statement['Action'], list) else [statement['Action']])}
        stacks = [f'arn:aws:cloudformation:eu-west-1:497087877832:stack/{name}/*'
                  for name in ['guito-sessions-staging', 'guito-sessions-production', 'guito-postgres-shared']]
        for action in ['DetectStackDrift', 'DetectStackResourceDrift', 'DescribeStackResourceDrifts']:
            self.assertIn('cloudformation:'+action, by_action)
            self.assertEqual(by_action['cloudformation:'+action]['Resource'], stacks)
        regional = by_action['cloudformation:BatchDescribeTypeConfigurations']
        self.assertEqual(regional['Resource'], '*')
        self.assertEqual(regional['Condition'], {'StringEquals': {'aws:RequestedRegion': 'eu-west-1'}})

    def test_native_predeployment_and_stack_tag_permissions_are_scoped(self):
        policy = json.loads((ROOT / 'deploy/storage-cloudformation-policy.json').read_text())
        by_action = {action: statement for statement in policy['Statement']
                     for action in (statement['Action'] if isinstance(statement['Action'], list) else [statement['Action']])}
        stacks = [f'arn:aws:cloudformation:eu-west-1:497087877832:stack/{name}/*'
                  for name in ['guito-sessions-staging', 'guito-sessions-production', 'guito-postgres-shared']]
        self.assertIn('cloudformation:TagResource', by_action)
        self.assertEqual(by_action['cloudformation:TagResource']['Resource'], stacks)
        self.assertIn('cloudformation:DescribeEvents', by_action)
        self.assertEqual(by_action['cloudformation:DescribeEvents']['Resource'], stacks +
                         ['arn:aws:cloudformation:eu-west-1:497087877832:changeSet/guito-infra-*/*'])
        self.assertNotIn('cloudformation:UntagResource', by_action)

    def test_deployment_policy_bounds_change_sets_and_cannot_reconfigure_postgres(self):
        policy = json.loads((ROOT / "deploy" / "storage-cloudformation-policy.json").read_text())
        statements = {statement["Sid"]: statement for statement in policy["Statement"]}
        actions = {
            action
            for statement in statements.values()
            for action in (statement["Action"] if isinstance(statement["Action"], list) else [statement["Action"]])
        }

        self.assertTrue(all("*" not in action for action in actions))
        self.assertEqual({"rds:DescribeDBClusters", "rds:DescribeDBInstances", "rds:ListTagsForResource", "rds:AddTagsToResource", "rds:RemoveTagsFromResource"}, {action for action in actions if action.startswith("rds:")})
        self.assertNotIn("cloudformation:CreateStack", actions)
        self.assertNotIn("cloudformation:UpdateStack", actions)
        self.assertNotIn("cloudformation:DeleteStack", actions)
        shared = statements["PlanOnlySharedPostgresImports"]
        self.assertEqual("cloudformation:CreateChangeSet", shared["Action"])
        self.assertEqual("arn:aws:cloudformation:eu-west-1:497087877832:stack/guito-postgres-shared/*", shared["Resource"])
        self.assertEqual("false", shared["Condition"]["Null"]["cloudformation:ImportResourceTypes"])
        self.assertEqual(["AWS::RDS::DBCluster", "AWS::RDS::DBInstance"], shared["Condition"]["ForAllValues:StringEquals"]["cloudformation:ImportResourceTypes"])
        for statement in statements.values():
            if statement["Resource"] == "*":
                self.assertEqual("eu-west-1", statement["Condition"]["StringEquals"]["aws:RequestedRegion"])
        for sid in ("PlanOnlyNativeSessionTableChanges", "PlanOnlySharedPostgresImports"):
            condition = statements[sid]["Condition"]
            self.assertEqual("guito-infra-*", condition["StringLike"]["cloudformation:ChangeSetName"])
            self.assertEqual("false", condition["Null"]["cloudformation:ResourceTypes"])
            self.assertEqual("true", condition["Null"]["cloudformation:RoleArn"])
        execute = statements["ExecuteOnlyNamedStoragePlans"]
        self.assertTrue(all(":stack/guito-" in resource for resource in execute["Resource"]))
        self.assertEqual("guito-infra-*", execute["Condition"]["StringLike"]["cloudformation:ChangeSetName"][0])


if __name__ == "__main__":
    unittest.main()
