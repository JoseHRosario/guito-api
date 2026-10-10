#!/usr/bin/env python3
"""Postmerge Guito inline-policy reconciliation. Read-only unless --apply is explicit.

Run after BOTH environment deployments and SIT pass. --rollout-verified is a
human attestation of that gate, not a substitute for tests. No secrets are read.
Only the two fixed Guito roles are writable; attached policies are never changed.
"""
import argparse
import copy
import json
import os
import subprocess

ACCOUNT = "497087877832"
REGION = "eu-west-1"
ROLES = ("guito-api-lambda-role", "guito-api-deploy")
PREFIX = f"arn:aws:secretsmanager:{REGION}:{ACCOUNT}:secret:guito-api/"
BROAD = {PREFIX + "*", f"arn:aws:secretsmanager:{REGION}:*:secret:guito-api/*"}
OBSOLETE = {PREFIX + name for name in (
    "eb-*", "prod-*", "staging-*", "human-auth-*", "prod", "staging",
    "human-auth", "eb-prod-pk-*", "eb-staging-pk-*")}
READ_ACTIONS = {"secretsmanager:GetSecretValue", "secretsmanager:DescribeSecret"}


def sequence(value):
    return value if isinstance(value, list) else [value]


def transform(document):
    """Remove only known legacy secret resource/action pairs, keeping all else."""
    result = copy.deepcopy(document)
    statements = []
    for statement in sequence(document["Statement"]):
        resources = sequence(statement.get("Resource", []))
        if statement.get("Effect") != "Allow" or not any(
                resource in BROAD | OBSOLETE for resource in resources):
            statements.append(copy.deepcopy(statement))
            continue
        if "NotAction" in statement or "NotResource" in statement:
            raise ValueError("Unsupported negated legacy statement")
        actions = sequence(statement.get("Action", []))
        secret_actions = [a for a in actions if a.lower().startswith("secretsmanager:") or a == "*"]
        if not secret_actions or any(a not in READ_ACTIONS for a in secret_actions):
            raise ValueError("Legacy resource has unexpected actions; manual review required")
        retained = [PREFIX + "db-*" if r in BROAD else r for r in resources if r not in OBSOLETE]
        other_actions = [a for a in actions if a not in secret_actions]
        if other_actions:
            other = copy.deepcopy(statement)
            other["Action"] = other_actions
            statements.append(other)
        if retained:
            narrowed = copy.deepcopy(statement)
            narrowed["Action"] = secret_actions
            narrowed["Resource"] = list(dict.fromkeys(retained))
            if other_actions:
                narrowed.pop("Sid", None)  # split statements must not duplicate Sid
            statements.append(narrowed)
    result["Statement"] = statements
    return result


def aws(*arguments):
    command = ["aws", "--profile", "minerva-agent", "--region", REGION,
               *arguments, "--output", "json"]
    completed = subprocess.run(command, text=True, capture_output=True,
                               env={**os.environ, "AWS_PAGER": ""})
    if completed.returncode:
        # NoSuchEntity is NOT globally suppressed: access/CLI errors always abort.
        raise RuntimeError(completed.stderr.strip())
    return json.loads(completed.stdout) if completed.stdout.strip() else {}


def inventory():
    result = {}
    for role in ROLES:
        names = aws("iam", "list-role-policies", "--role-name", role)["PolicyNames"]
        result[role] = {name: aws("iam", "get-role-policy", "--role-name", role,
                                  "--policy-name", name)["PolicyDocument"] for name in names}
        attached = aws("iam", "list-attached-role-policies", "--role-name", role)["AttachedPolicies"]
        expected = ([] if role == "guito-api-deploy" else [{
            "PolicyName": "AWSLambdaBasicExecutionRole",
            "PolicyArn": "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"}])
        if attached != expected:
            raise ValueError(f"Unexpected attached policies on {role}; manual review required")
    return result


def verify_consumers():
    for suffix, environment in (("", "prod"), ("-staging", "staging")):
        for kind in ("guito-api", "guito-api-authorizer"):
            config = aws("lambda", "get-function-configuration", "--function-name", kind + suffix)
            if config["Role"] != f"arn:aws:iam::{ACCOUNT}:role/{ROLES[0]}":
                raise ValueError("Unexpected consumer role")
            variables = config.get("Environment", {}).get("Variables", {})
            expected = ({"SECRETS_LOCATION": "AwsSsm", "SECRETS_SECRET_NAME": f"/guito-api/{environment}"}
                        if kind.endswith("authorizer") else {
                            "AppConfiguration__Secrets__Location": "AwsSsm",
                            "AppConfiguration__Secrets__SecretName": f"/guito-api/{environment}",
                            "AppConfiguration__Secrets__HumanAuthSecretName": "/guito-api/human-auth",
                            "AppConfiguration__EnableBanking__SecretsSource": "AwsSsm",
                            "AppConfiguration__EnableBanking__SsmParameterName": f"/guito-api/eb-{environment}-pk"})
            if any(variables.get(key) != value for key, value in expected.items()):
                raise ValueError(f"Consumer not fully SSM: {kind + suffix}")


def reconcile(apply=False, rollout_verified=False):
    identity = aws("sts", "get-caller-identity")
    if identity["Account"] != ACCOUNT or not identity["Arn"].startswith(
            f"arn:aws:sts::{ACCOUNT}:assumed-role/MinervaAIAgent/"):
        raise ValueError("Expected MinervaAIAgent assumed role in Guito account")
    if apply and not rollout_verified:
        raise ValueError("--apply requires --rollout-verified AFTER merge, both deploys and SIT")
    verify_consumers()
    before = inventory()
    after = {role: {name: transform(doc) for name, doc in policies.items()}
             for role, policies in before.items()}
    changes = [(role, name) for role in ROLES for name in before[role]
               if before[role][name] != after[role][name]]
    for role, name in changes:
        print(json.dumps({"role": role, "policy": name,
                          "operation": "put" if after[role][name]["Statement"] else "delete",
                          "before": before[role][name], "after": after[role][name]}))
    if not apply:
        print(f"Dry run: {len(changes)} changes; no IAM writes")
        return
    # All transforms/preflight succeed before any write; abort if policies drift.
    if inventory() != before:
        raise ValueError("IAM changed during planning; rerun")
    for role, name in changes:
        current = aws("iam", "get-role-policy", "--role-name", role, "--policy-name", name)["PolicyDocument"]
        if current != before[role][name]:
            raise ValueError("Policy changed before write; aborting")
        document = after[role][name]
        if document["Statement"]:
            aws("iam", "put-role-policy", "--role-name", role, "--policy-name", name,
                "--policy-document", json.dumps(document))
            readback = aws("iam", "get-role-policy", "--role-name", role,
                           "--policy-name", name)["PolicyDocument"]
            if readback != document:
                raise ValueError("Policy readback mismatch")
        else:
            aws("iam", "delete-role-policy", "--role-name", role, "--policy-name", name)
            names = aws("iam", "list-role-policies", "--role-name", role)["PolicyNames"]
            if name in names:
                raise ValueError("Deleted policy still present")
    expected = {role: {name: doc for name, doc in policies.items() if doc["Statement"]}
                for role, policies in after.items()}
    if inventory() != expected:
        raise ValueError("Final inventory mismatch; partial changes possible, inspect before retry")
    print("Verified IAM readback; database, SSM and unrelated statements retained")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--rollout-verified", action="store_true")
    args = parser.parse_args()
    try:
        reconcile(args.apply, args.rollout_verified)
    except (ValueError, RuntimeError, KeyError, TypeError) as error:
        parser.exit(1, f"FATAL: {error}\n")


if __name__ == "__main__":
    main()
