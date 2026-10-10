"""Explicit GitHub Actions-only reset for the unreleased issue-128 migration."""

import json
import os
import subprocess
import time

REGION = "eu-west-1"
DATABASE = "guito_staging"
CLUSTER = "arn:aws:rds:eu-west-1:497087877832:cluster:db-cluster"
MIGRATION = "005_expenses.sql"


def aws(*arguments):
    result = subprocess.run(
        ["aws", "--region", REGION, *arguments, "--output", "json"],
        capture_output=True,
        text=True,
        check=True,
    )
    return json.loads(result.stdout)


def main():
    required = {
        "ENV": "staging",
        "GITHUB_ACTIONS": "true",
        "GITHUB_EVENT_NAME": "workflow_dispatch",
        "GITHUB_REF": "refs/heads/feature/128-postgres-expenses",
        "GITHUB_REPOSITORY": "JoseHRosario/guito-api",
        "GUITO_RESET_STAGING_EXPENSES": "true",
    }
    for key, value in required.items():
        if os.environ.get(key) != value:
            raise SystemExit(f"Reset refused: {key} must equal {value}.")

    identity = aws("sts", "get-caller-identity")
    if identity["Account"] != "497087877832" or "/guito-api-deploy/" not in identity["Arn"]:
        raise SystemExit("Reset refused: the staging GitHub deployment role is required.")

    secret = aws("secretsmanager", "describe-secret", "--secret-id", "guito-api/db-admin")["ARN"]
    common = ["--resource-arn", CLUSTER, "--secret-arn", secret, "--database", DATABASE]

    def execute(sql):
        return aws("rds-data", "execute-statement", *common, "--sql", sql)

    # Only the read-only wake probe retries Aurora resume errors. Destructive
    # operations and other failures are never blindly retried.
    actual_database = None
    for attempt in range(12):
        try:
            actual_database = execute("SELECT current_database()")
            break
        except subprocess.CalledProcessError as error:
            if "DatabaseResumingException" not in (error.stderr or "") or attempt == 11:
                raise
            print(f"Staging cluster resuming ({attempt + 1}/12); retrying read-only probe.")
            time.sleep(5)
    if actual_database is None or actual_database["records"][0][0]["stringValue"] != DATABASE:
        raise SystemExit("Reset refused: database verification failed.")

    # One PostgreSQL DO statement is atomic under its implicit transaction and
    # uses only ExecuteStatement, already allowed to the GitHub migration role.
    # No CASCADE: unexpected dependencies fail without changing table or ledger.
    execute(
        "DO $reset$ BEGIN "
        "DROP TABLE IF EXISTS public.expenses; "
        f"DELETE FROM public._migrations WHERE name = '{MIGRATION}'; "
        "END; $reset$"
    )

    verification = execute(
        "SELECT to_regclass('public.expenses') IS NULL, "
        f"NOT EXISTS (SELECT 1 FROM public._migrations WHERE name = '{MIGRATION}')"
    )
    fields = verification["records"][0]
    if not all(field.get("booleanValue") is True for field in fields):
        raise SystemExit("Reset readback failed; refusing deployment.")
    print(f"Verified reset: {DATABASE}.expenses absent; {MIGRATION} ledger entry absent.")


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        raise SystemExit(error.stderr or "AWS command failed; reset/redeployment aborted.") from error
