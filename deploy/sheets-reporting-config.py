"""Select public reporting OIDC configuration before deployment mutates resources."""
import argparse
import json
import os
from pathlib import Path
import subprocess

API_AUDIENCE = "AppConfiguration__SheetsReportingAuthentication__Audience"
API_SUBJECT = "AppConfiguration__SheetsReportingAuthentication__Subject"
AUTH_AUDIENCE = "SHEETS_REPORTING_GOOGLE_AUDIENCE"
AUTH_SUBJECT = "SHEETS_REPORTING_GOOGLE_SUBJECT"


def select(api, authorizer, requested, environment, human_audience):
    mode = requested.get("SHEETS_REPORTING_AUTHENTICATION", "preserve")
    if mode not in ("preserve", "enabled", "disabled"):
        raise ValueError("SHEETS_REPORTING_AUTHENTICATION must be preserve, enabled or disabled")
    supplied = (requested.get(AUTH_AUDIENCE, ""), requested.get(AUTH_SUBJECT, ""))
    if mode != "enabled" and any(supplied):
        raise ValueError("Reporting claims require explicit enabled mode, not implicit enablement")
    if mode == "disabled":
        return ("", "")
    if mode == "enabled":
        if requested.get("SHEETS_REPORTING_IDENTITY_VERIFIED") != "true":
            raise ValueError("Verify the live Apps Script audience/installer subject before enabling reporting")
        if environment == "production" and requested.get("ALLOW_PRODUCTION_SHEETS_REPORTING") != "true":
            raise ValueError("Production reporting enablement requires explicit user approval")
        pair = supplied
    else:
        api_pair = (api.get(API_AUDIENCE, ""), api.get(API_SUBJECT, ""))
        pair = (authorizer.get(AUTH_AUDIENCE, ""), authorizer.get(AUTH_SUBJECT, ""))
        if api_pair != pair:
            raise ValueError("Installed reporting gates disagree; explicitly disable or reconfigure both")
        if pair == ("", ""):
            return pair
    if not all(isinstance(value, str) and value.strip() and value == value.strip() for value in pair):
        raise ValueError("Reporting audience and subject must both be complete exact nonblank claims")
    # An explicit human override will propagate to both gates; otherwise pin existing selectors.
    human_audiences = ([human_audience] if human_audience else
        [api.get("AppConfiguration__Authentication__OAuthAudience", ""), authorizer.get("GOOGLE_CLIENT_ID", "")])
    if not all(human_audiences) or len(set(human_audiences)) != 1 or pair[0] in human_audiences:
        raise ValueError("Reporting audience must differ from the known consistent UI audience")
    return pair


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
    parser.add_argument("--authorizer", required=True)
    parser.add_argument("--environment", choices=("staging", "production"), required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        audience, subject = select(current_variables(args.region, args.api), current_variables(args.region, args.authorizer),
                                   os.environ, args.environment, os.environ.get("GOOGLE_CLIENT_ID", ""))
    except (ValueError, RuntimeError) as error:
        parser.exit(1, str(error) + "\n")
    (args.output_dir / "guito-reporting-auth-env.json").write_text(json.dumps({AUTH_AUDIENCE: audience, AUTH_SUBJECT: subject}))
    (args.output_dir / "guito-reporting-api-env.json").write_text(json.dumps({API_AUDIENCE: audience, API_SUBJECT: subject}))


if __name__ == "__main__":
    main()
