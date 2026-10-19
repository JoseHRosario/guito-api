#!/usr/bin/env python3
"""Read-only DynamoDB session settings verifier and offline IAM policy emitter."""
import argparse
import json
import os
from pathlib import Path
import re
import time
import subprocess
import sys

ACCOUNT = "497087877832"
REGION = "eu-west-1"


def aws(operation, **parameters):
    command = ["aws", "--region", REGION, "--output", "json", "--cli-connect-timeout", "5",
               "--cli-read-timeout", "20", "sts" if operation == "get-caller-identity" else "dynamodb", operation]
    for key, value in parameters.items():
        command.extend(["--" + key.replace("_", "-"), value])
    result = subprocess.run(command, capture_output=True, text=True, timeout=30,
                            env=dict(os.environ, AWS_MAX_ATTEMPTS="2", AWS_PAGER=""))
    if result.returncode:
        if operation == "describe-table" and re.search(
                r"An error occurred \(ResourceNotFoundException\) when calling the DescribeTable operation:", result.stderr):
            return None
        raise ValueError(f"AWS {operation} failed; deployment stopped")
    payload = json.loads(result.stdout)
    if not isinstance(payload, dict):
        raise ValueError(f"AWS {operation} returned an invalid response")
    return payload


def validate_table(table, description):
    throughput = description["ProvisionedThroughput"]
    sse = description.get("SSEDescription") or {}
    valid = (
        description["TableName"] == table
        and description["TableArn"] == f"arn:aws:dynamodb:{REGION}:{ACCOUNT}:table/{table}"
        and description.get("TableClassSummary", {}).get("TableClass", "STANDARD") == "STANDARD"
        # DynamoDB omits BillingModeSummary for older provisioned tables.
        and description.get("BillingModeSummary", {}).get("BillingMode", "PROVISIONED") == "PROVISIONED"
        and throughput["ReadCapacityUnits"] == 1 and throughput["WriteCapacityUnits"] == 1
        and description.get("DeletionProtectionEnabled") is True
        and description["KeySchema"] == [{"AttributeName": "handle_hash", "KeyType": "HASH"}]
        and description["AttributeDefinitions"] == [{"AttributeName": "handle_hash", "AttributeType": "S"}]
        and not description.get("GlobalSecondaryIndexes") and not description.get("LocalSecondaryIndexes")
        and not description.get("StreamSpecification", {}).get("StreamEnabled", False)
        and not sse.get("KMSMasterKeyArn") and sse.get("SSEType", "AES256") == "AES256")
    if not valid:
        raise ValueError("Session table drift; refusing to change capacity, billing, schema, encryption or protections")


def verify_table(table, args):
    for attempt in range(args.max_attempts):
        description = aws("describe-table", table_name=table)
        if description is None:
            raise ValueError("Session table missing; use the CloudFormation storage lifecycle")
        if description["Table"]["TableStatus"] == "ACTIVE":
            return description["Table"]
        time.sleep(args.poll_seconds)
    raise ValueError("Table did not become ACTIVE within bounded wait")


def verify_ttl(table, args):
    for attempt in range(args.max_attempts):
        ttl = aws("describe-time-to-live", table_name=table)["TimeToLiveDescription"]
        if ttl.get("AttributeName") != "ttl" or ttl["TimeToLiveStatus"] not in ("ENABLING", "ENABLED"):
            raise ValueError("Session TTL configuration drift; reconcile through CloudFormation")
        if ttl["TimeToLiveStatus"] == "ENABLED":
            return ttl
        time.sleep(args.poll_seconds)
    raise ValueError("TTL did not become ENABLED within bounded wait")


def write_policies(directory):
    resources = [f"arn:aws:dynamodb:{REGION}:{ACCOUNT}:table/{name}"
                 for name in ("guito-api-user-sessions", "guito-api-user-sessions-staging")]
    actions = {
        "runtime": ["GetItem", "PutItem", "UpdateItem", "DeleteItem"],
        "provisioner": ["DescribeTable", "CreateTable", "DescribeTimeToLive", "UpdateTimeToLive", "DescribeContinuousBackups"],
    }
    for purpose, allowed in actions.items():
        policy = {"Version": "2012-10-17", "Statement": [{"Effect": "Allow",
                  "Action": ["dynamodb:" + action for action in allowed], "Resource": resources}]}
        (directory / f"user-session-{purpose}-policy.json").write_text(json.dumps(policy))


def report_verified_settings(table, description, ttl, backups):
    print(json.dumps({
        "table": table, "region": REGION, "status": description["TableStatus"],
        "billing_mode": description.get("BillingModeSummary", {}).get("BillingMode", "PROVISIONED"),
        "table_class": description.get("TableClassSummary", {}).get("TableClass", "STANDARD"),
        "provisioned_capacity": {name: description["ProvisionedThroughput"][name]
                                 for name in ("ReadCapacityUnits", "WriteCapacityUnits")},
        "deletion_protection": description["DeletionProtectionEnabled"], "ttl": ttl,
        "pitr": backups["ContinuousBackupsDescription"]["PointInTimeRecoveryDescription"]["PointInTimeRecoveryStatus"],
        "encryption": "AWS_OWNED",
    }))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--environment", required=True, choices=["production", "staging"])
    parser.add_argument("--account-id", required=True, choices=[ACCOUNT])
    parser.add_argument("--region", default=REGION, choices=[REGION])
    parser.add_argument("--max-attempts", type=int, default=30)
    parser.add_argument("--poll-seconds", type=float, default=2)
    parser.add_argument("--policies-only", action="store_true", help="Emit reviewable IAM JSON offline; never attach a policy")
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    if not 1 <= args.max_attempts <= 120 or not 0 <= args.poll_seconds <= 10:
        parser.error("Wait bounds must be 1..120 attempts and 0..10 seconds")
    if args.policies_only and not args.output_dir:
        parser.error("--policies-only requires --output-dir")
    table = "guito-api-user-sessions" + ("-staging" if args.environment == "staging" else "")
    try:
        if args.output_dir:
            write_policies(args.output_dir)
        if args.policies_only:
            return
        if aws("get-caller-identity")["Account"] != ACCOUNT:
            raise ValueError("Unexpected AWS account")
        description = verify_table(table, args)
        validate_table(table, description)
        backups = aws("describe-continuous-backups", table_name=table)
        if backups["ContinuousBackupsDescription"]["PointInTimeRecoveryDescription"]["PointInTimeRecoveryStatus"] != "DISABLED":
            raise ValueError("Session table PITR must remain disabled")
        ttl = verify_ttl(table, args)
        report_verified_settings(table, description, ttl, backups)
    except (ValueError, KeyError, TypeError, OSError, subprocess.TimeoutExpired) as error:
        sys.exit(str(error))


if __name__ == "__main__":
    main()
