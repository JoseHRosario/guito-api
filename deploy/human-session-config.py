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
        key = base64.b64decode(json.loads(payload)["HumanSessionEncryptionKey"], validate=True)
        if len(key) != 32:
            raise ValueError()
    except (ValueError, KeyError, TypeError):
        raise ValueError("HumanSessionEncryptionKey must be an owner-provisioned base64 32-byte key") from None


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
        (args.output_dir / "human-session-cors.json").write_text(json.dumps(cors))
        (args.output_dir / "human-session-env.json").write_text(json.dumps(variables))
    except (ValueError, KeyError, TypeError):
        sys.exit("Session deployment configuration is invalid or the owner-provisioned encryption key is missing/invalid; no credentials were created.")


if __name__ == "__main__":
    main()
