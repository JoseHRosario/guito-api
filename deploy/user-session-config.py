#!/usr/bin/env python3
"""Offline session deployment configuration; never provision or print key material."""
import argparse
import base64
import json
from pathlib import Path
import sys
from urllib.parse import urlsplit


def validate_key(payload):
    try:
        fields = json.loads(payload)
        if not isinstance(fields, dict):
            raise ValueError()
        # Runtime secret providers deserialize property names case-insensitively.
        fields = {name.lower(): value for name, value in fields.items()}
        decode_key(fields["usersessionencryptionkey"])
    except (ValueError, KeyError, TypeError):
        raise ValueError("UserSessionEncryptionKey must be canonical base64 for exactly 32 bytes") from None


def decode_key(encoded):
    if not isinstance(encoded, str):
        raise ValueError()
    key = base64.b64decode(encoded, validate=True)
    if len(key) != 32 or base64.b64encode(key).decode("ascii") != encoded:
        raise ValueError()
    return key


def configuration(root, environment):
    if environment not in ("Production", "Staging"):
        raise ValueError("Explicit Production or Staging environment required")
    source = root / "src" / "guito-api"
    base = json.loads((source / "appsettings.json").read_text())
    overlay = json.loads((source / f"appsettings.{environment}.json").read_text())
    origins = overlay["AppConfiguration"].get("Cors", base["AppConfiguration"]["Cors"])["AllowedOrigins"]
    if not origins or len(set(origins)) != len(origins):
        raise ValueError("Explicit unique UI origins are required")
    for origin in origins:
        parsed = urlsplit(origin)
        if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password or origin != f"https://{parsed.netloc}" or "*" in origin:
            raise ValueError("Only exact HTTPS UI origins are permitted")
    cors = {"AllowOrigins": origins, "AllowCredentials": True,
            "AllowMethods": ["GET", "POST", "PUT", "DELETE", "OPTIONS"],
            "AllowHeaders": ["content-type", "x-api-key", "authorization", "x-google-idtoken", "if-match", "x-guito-session"],
            "ExposeHeaders": ["x-api-version", "etag"], "MaxAge": 600}
    variables = {f"AppConfiguration__Cors__AllowedOrigins__{index}": origin for index, origin in enumerate(origins)}
    sessions = dict(base["AppConfiguration"].get("UserSessions", {}))
    sessions.update(overlay["AppConfiguration"].get("UserSessions", {}))
    expected = {"TableName": "guito-api-user-sessions" + ("-staging" if environment == "Staging" else ""),
                "Region": "eu-west-1"}
    if sessions != expected:
        raise ValueError("Explicit environment-isolated DynamoDB session configuration required")
    variables.update({f"AppConfiguration__UserSessions__{key}": value for key, value in sessions.items()})
    return cors, variables


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--validate-key", action="store_true")
    parser.add_argument("--environment", choices=["Production", "Staging"])
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    try:
        if args.validate_key:
            validate_key(sys.stdin.read())
            return
        if not args.output_dir:
            raise ValueError("Output directory is required")
        cors, variables = configuration(Path(__file__).resolve().parents[1], args.environment)
        (args.output_dir / "user-session-cors.json").write_text(json.dumps(cors))
        (args.output_dir / "user-session-env.json").write_text(json.dumps(variables))
    except (ValueError, KeyError, TypeError):
        sys.exit("Session deployment configuration is invalid or the owner-provisioned encryption key is missing/invalid; no credentials were created.")


if __name__ == "__main__":
    main()
