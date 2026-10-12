"""Select Expense authority and pause before deployment mutates resources."""
import argparse
import json
import os
from pathlib import Path
import subprocess

STORE = "AppConfiguration__ExpenseStore"
APPROVED = "AppConfiguration__ExpenseCutover__ProductionPostgresApproved"
PAUSED = "AppConfiguration__ExpenseCutover__WritesPaused"


def select(current, requested, environment):
    installed = current.get(STORE, "Sheets")
    approved = current.get(APPROVED, "false")
    paused = current.get(PAUSED, "false")
    if installed not in ("Sheets", "Postgres") or approved not in ("true", "false") or paused not in ("true", "false"):
        raise ValueError("Invalid installed Expense cutover configuration; resolve explicitly before deployment")
    mode = requested.get("EXPENSE_STORE", "preserve")
    pause = requested.get("EXPENSE_WRITES_PAUSED", "preserve")
    approval = requested.get("ALLOW_PRODUCTION_EXPENSE_POSTGRES", "false")
    if mode not in ("preserve", "Sheets", "Postgres") or pause not in ("preserve", "true", "false") or approval not in ("true", "false"):
        raise ValueError("Invalid Expense selector: use preserve/Sheets/Postgres and exact true/false booleans")
    store = installed if mode == "preserve" else mode
    if installed == "Postgres" and store == "Sheets":
        raise ValueError("No stale Sheets fallback after Postgres authority; use reviewed recovery preserving new writes")
    if environment == "production" and store == "Postgres":
        if mode == "Postgres":
            if approval != "true":
                raise ValueError("Production Postgres selection requires explicit execution approval")
            if installed != "Postgres" and (paused != "true" or (paused if pause == "preserve" else pause) != "true"):
                raise ValueError("First production Postgres switch requires an installed write pause that remains enabled")
            approved = "true"
        elif approved != "true":
            raise ValueError("Installed production Postgres has no approval; refusing implicit enablement")
    return {STORE: store, APPROVED: approved, PAUSED: paused if pause == "preserve" else pause}


def current_variables(region, name):
    result = subprocess.run(["aws", "--region", region, "lambda", "get-function-configuration", "--function-name", name,
                             "--query", "Environment.Variables", "--output", "json"], capture_output=True, text=True)
    if result.returncode:
        if "ResourceNotFoundException" in result.stderr:
            return {}
        raise RuntimeError("Cannot read existing Lambda configuration: " + result.stderr.strip())
    return json.loads(result.stdout) or {}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--region", required=True)
    parser.add_argument("--api", required=True)
    parser.add_argument("--environment", choices=("staging", "production"), required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        variables = select(current_variables(args.region, args.api), os.environ, args.environment)
    except (ValueError, RuntimeError) as error:
        parser.exit(1, str(error) + "\n")
    (args.output_dir / "guito-expense-api-env.json").write_text(json.dumps(variables))


if __name__ == "__main__":
    main()
