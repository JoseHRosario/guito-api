"""Hermetic IAM cleanup tests: fixture ARNs/policies only, never credentials."""
import copy
import importlib.util
import json
import pathlib
import subprocess
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("reconcile_iam", ROOT / "deploy/reconcile-iam.py")
assert SPEC is not None and SPEC.loader is not None
iam = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(iam)


def policy(resources, actions=None, **extra):
    return {"Version": "2012-10-17", "Statement": [{
        "Effect": "Allow", "Action": actions or list(iam.READ_ACTIONS),
        "Resource": resources, **extra}]}


class ReconcileIamTests(unittest.TestCase):
    def test_broad_grant_is_database_only_and_idempotent(self):
        for resource in iam.BROAD:
            original = policy(resource)
            before = copy.deepcopy(original)
            result = iam.transform(original)
            self.assertEqual([iam.PREFIX + "db-*"], result["Statement"][0]["Resource"])
            self.assertEqual(result, iam.transform(result))
            self.assertEqual(before, original)

    def test_eb_removed_and_database_permissions_preserved(self):
        document = policy([iam.PREFIX + "db-*", iam.PREFIX + "eb-*"])
        result = iam.transform(document)
        self.assertEqual([iam.PREFIX + "db-*"], result["Statement"][0]["Resource"])
        self.assertEqual(document["Statement"][0]["Action"], result["Statement"][0]["Action"])

    def test_old_guito_read_secrets_policy_becomes_empty(self):
        self.assertEqual([], iam.transform(policy(iam.PREFIX + "prod-*"))["Statement"])

    def test_unrelated_and_denies_unchanged(self):
        document = policy([iam.PREFIX + "db-admin*", "arn:aws:secretsmanager:eu-west-1:497087877832:secret:minerva/*"])
        document["Statement"].extend([
            {"Effect": "Allow", "Action": "ssm:GetParameter", "Resource": "*"},
            {"Effect": "Allow", "Action": "rds-data:ExecuteStatement", "Resource": "cluster"},
            {"Effect": "Deny", "Action": "secretsmanager:*", "Resource": iam.PREFIX + "*"}])
        self.assertEqual(document, iam.transform(document))

    def test_mixed_actions_resources_preserve_unrelated_pairs_and_condition(self):
        condition = {"StringEquals": {"aws:RequestedRegion": "eu-west-1"}}
        unrelated = "arn:aws:secretsmanager:eu-west-1:497087877832:secret:minerva/*"
        document = policy([iam.PREFIX + "eb-*", unrelated],
                          ["secretsmanager:GetSecretValue", "ssm:GetParameter"], Sid="Mixed", Condition=condition)
        result = iam.transform(document)["Statement"]
        self.assertEqual(document["Statement"][0]["Resource"], result[0]["Resource"])
        self.assertEqual(["ssm:GetParameter"], result[0]["Action"])
        self.assertEqual([unrelated], result[1]["Resource"])
        self.assertEqual(condition, result[1]["Condition"])
        self.assertNotIn("Sid", result[1])

    def test_unknown_actions_fail_closed(self):
        for action in ("*", "secretsmanager:*", "secretsmanager:DeleteSecret"):
            with self.assertRaises(ValueError):
                iam.transform(policy(iam.PREFIX + "*", [action]))

    def test_identity_and_rollout_gate_reject_without_writes(self):
        for identity, apply in (({"Account": "wrong", "Arn": "wrong"}, False),
                                ({"Account": iam.ACCOUNT, "Arn": f"arn:aws:sts::{iam.ACCOUNT}:assumed-role/MinervaAIAgent/test"}, True)):
            with patch.object(iam, "aws", return_value=identity) as aws:
                with self.assertRaises(ValueError):
                    iam.reconcile(apply=apply)
                self.assertEqual(1, aws.call_count)

    def test_aws_errors_including_delete_missing_are_not_masked(self):
        for error in ("AccessDenied", "NoSuchEntity", "ExpiredToken"):
            with patch.object(iam.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", error)):
                with self.assertRaisesRegex(RuntimeError, error):
                    iam.aws("iam", "delete-role-policy", "--role-name", iam.ROLES[0], "--policy-name", "old")

    def test_dry_run_has_no_writes_and_apply_reads_back(self):
        for apply in (False, True):
            state = {role: {"old": policy(iam.PREFIX + "prod-*")} for role in iam.ROLES}
            calls = []

            def fake_aws(*args):
                calls.append(args)
                if args[0] == "sts":
                    return {"Account": iam.ACCOUNT, "Arn": f"arn:aws:sts::{iam.ACCOUNT}:assumed-role/MinervaAIAgent/test"}
                role = args[args.index("--role-name") + 1]
                if args[1] == "get-role-policy":
                    return {"PolicyDocument": state[role]["old"]}
                if args[1] == "delete-role-policy":
                    del state[role]["old"]
                    return {}
                if args[1] == "list-role-policies":
                    return {"PolicyNames": list(state[role])}
                self.fail(args)

            with patch.object(iam, "aws", side_effect=fake_aws), patch.object(iam, "verify_consumers"), patch.object(iam, "inventory", side_effect=lambda: copy.deepcopy(state)):
                iam.reconcile(apply, rollout_verified=apply)
            deletes = [call for call in calls if call[1] == "delete-role-policy"]
            self.assertEqual(2 if apply else 0, len(deletes))
            self.assertTrue(all(not policies for policies in state.values()) if apply else all(state.values()))

    def test_apply_put_readback_failure_and_drift_abort(self):
        for scenario in ("success", "readback-failure", "drift", "write-denied"):
            state = {role: {"broad": policy(iam.PREFIX + "*")} for role in iam.ROLES}
            writes = []
            inventories = []

            def fake_inventory():
                inventories.append(True)
                if scenario == "drift" and len(inventories) == 2:
                    return {}
                return copy.deepcopy(state)

            def fake_aws(*args):
                if args[0] == "sts":
                    return {"Account": iam.ACCOUNT, "Arn": f"arn:aws:sts::{iam.ACCOUNT}:assumed-role/MinervaAIAgent/test"}
                role = args[args.index("--role-name") + 1]
                if args[1] == "get-role-policy":
                    return {"PolicyDocument": copy.deepcopy(state[role]["broad"])}
                if args[1] == "put-role-policy":
                    if scenario == "write-denied":
                        raise RuntimeError("AccessDenied")
                    writes.append(role)
                    if scenario != "readback-failure":
                        state[role]["broad"] = json.loads(args[-1])
                    return {}
                self.fail(args)

            with patch.object(iam, "aws", side_effect=fake_aws), patch.object(iam, "verify_consumers"), patch.object(iam, "inventory", side_effect=fake_inventory):
                if scenario == "success":
                    iam.reconcile(True, True)
                    self.assertEqual(2, len(writes))
                else:
                    with self.assertRaises((ValueError, RuntimeError)):
                        iam.reconcile(True, True)
                    self.assertEqual(1 if scenario == "readback-failure" else 0, len(writes))

    def test_deploy_has_only_database_secret_grants_and_no_migration_reference(self):
        text = (ROOT / "deploy/deploy.sh").read_text()
        self.assertNotIn("migrate-secrets-to-ssm", text)
        self.assertNotIn("--policy-name guito-api-secret-read", text)
        self.assertNotIn("secret:guito-api/eb-*", text)
        self.assertNotIn("secret:guito-api/*", text)
        self.assertIn("secret:guito-api/db-*", text)


if __name__ == "__main__":
    unittest.main()
