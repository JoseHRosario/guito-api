#!/usr/bin/env python3
"""Copy application secrets to SSM without logging values or deleting sources."""
import argparse
import json
import boto3
from botocore.exceptions import ClientError

APPLICATION_SECRETS = (
    "guito-api/prod",
    "guito-api/staging",
    "guito-api/human-auth",
    "guito-api/eb-staging-pk",
    "guito-api/eb-prod-pk",
)


def migrate(name, secrets, ssm):
    value = secrets.get_secret_value(SecretId=name)["SecretString"]
    if len(value.encode("utf-8")) > 4096:
        raise RuntimeError(f"/{name} exceeds the standard-tier limit; no write attempted")
    parameter_name = f"/{name}"
    try:
        existing = ssm.get_parameter(Name=parameter_name, WithDecryption=True)["Parameter"]
    except ClientError as error:
        if error.response["Error"]["Code"] != "ParameterNotFound":
            raise
        ssm.put_parameter(Name=parameter_name, Value=value, Type="SecureString",
                          Tier="Standard", Overwrite=False)
    else:
        if existing["Type"] != "SecureString" or existing["Value"] != value:
            raise RuntimeError(f"{parameter_name} already exists with different content/type; refusing overwrite")
    actual = ssm.get_parameter(Name=parameter_name, WithDecryption=True)["Parameter"]
    if actual["Type"] != "SecureString" or actual["Value"] != value:
        raise RuntimeError(f"{parameter_name} read-back mismatch")
    try:
        fields = sorted(json.loads(value).keys())
    except (json.JSONDecodeError, AttributeError):
        fields = ["raw-payload"]
    print(f"{parameter_name}: SecureString verified; fields={','.join(fields)}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--region", default="eu-west-1")
    args = parser.parse_args()
    secrets = boto3.client("secretsmanager", region_name=args.region)
    ssm = boto3.client("ssm", region_name=args.region)
    for name in APPLICATION_SECRETS:
        migrate(name, secrets, ssm)
    print("Application parameters verified; database secrets and all sources retained.")


if __name__ == "__main__":
    main()
