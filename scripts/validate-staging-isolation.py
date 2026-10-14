#!/usr/bin/env python3
"""Fail-closed operator attestation/readback gate; never provisions or writes data."""
import datetime as dt
import json
import subprocess
import sys
from urllib.parse import urlsplit


def validate(attestation, staging, production, base_url, now=None):
    now = now or dt.datetime.now(dt.timezone.utc)
    url = urlsplit(base_url)
    if url.scheme != "https" or not url.hostname or url.path not in ("", "/") or url.query or url.fragment or url.username:
        raise ValueError("An exact HTTPS staging origin is required")
    for key in ("approvedBy", "category", "resourceArn", "databaseName", "secretArn", "revisionId", "codeSha256"):
        if not isinstance(attestation.get(key), str) or not attestation[key].strip():
            raise ValueError(f"Missing operator attestation: {key}")
    if attestation.get("environment") != "staging" or attestation.get("baseUrl") != base_url:
        raise ValueError("Attestation must bind the exact staging target")
    for key in ("disposable", "noPersonalData", "exclusiveDatabaseCredentials", "syntheticCreateDeleteApproved"):
        if attestation.get(key) is not True:
            raise ValueError(f"Operator must attest {key}")
    expiry = dt.datetime.fromisoformat(attestation["expiresAt"].replace("Z", "+00:00"))
    if expiry.tzinfo is None or not now < expiry <= now + dt.timedelta(hours=4):
        raise ValueError("Attestation must expire within four hours")
    if staging.get("RevisionId") != attestation["revisionId"] or staging.get("CodeSha256") != attestation["codeSha256"]:
        raise ValueError("Staging code/config changed since operator inspection")
    stage_env = staging.get("Environment", {}).get("Variables", {})
    prod_env = production.get("Environment", {}).get("Variables", {})
    # Require explicit installed values, not assumptions from local appsettings or resource names.
    keys = {"resourceArn": "Database__ResourceArn", "databaseName": "Database__DatabaseName", "secretArn": "Database__SecretArn"}
    for field, key in keys.items():
        if stage_env.get(key) != attestation[field] or not prod_env.get(key):
            raise ValueError(f"Explicit deployed database configuration/readback required: {key}")
    if (stage_env[keys["resourceArn"]], stage_env[keys["databaseName"]]) == (prod_env[keys["resourceArn"]], prod_env[keys["databaseName"]]):
        raise ValueError("Staging must not address the production database")
    if stage_env[keys["secretArn"]] == prod_env[keys["secretArn"]]:
        raise ValueError("Staging must use exclusive non-production database credentials")
    return json.dumps(attestation, separators=(",", ":"))


def main():
    def configuration(name):
        return json.loads(subprocess.check_output([
            "aws", "--region", "eu-west-1", "lambda", "get-function-configuration",
            "--function-name", name, "--output", "json"], text=True))
    with open(sys.argv[1], encoding="utf-8") as source:
        attestation = json.load(source)
    print(validate(attestation, configuration("guito-api-staging"), configuration("guito-api"), sys.argv[2]))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        sys.exit("FATAL: disposable staging isolation is not established; inspect attestation and exact deployed database configuration. No test mutation is allowed.")
