#!/usr/bin/env python3
"""Owner-run Google verification before enabling reporting; never persists tokens."""
import argparse
import getpass
import json
import sys
import warnings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--audience", required=True, help="Public audience discovered from the private script")
    parser.add_argument("--subject", required=True, help="Public installer subject discovered from the private script")
    args = parser.parse_args()
    try:
        from google.auth.transport.requests import Request
        from google.oauth2.id_token import verify_oauth2_token
    except ImportError:
        print("Install google-auth[requests] in a private virtual environment first.", file=sys.stderr)
        return 2
    if not sys.stdin.isatty():
        print("Use an interactive terminal; tokens must not be supplied through files, arguments or pipes.", file=sys.stderr)
        return 2
    try:
        # Without a real terminal, getpass can echo input; treat that fallback as failure.
        with warnings.catch_warnings():
            warnings.simplefilter("error", getpass.GetPassWarning)
            token = getpass.getpass("Fresh Apps Script ID token (hidden): ")
        claims = verify_oauth2_token(token, Request(), audience=args.audience)
        if claims.get("sub") != args.subject or claims.get("email_verified") is not True or not claims.get("email"):
            raise ValueError("reporting identity mismatch")
    except (Exception, KeyboardInterrupt):
        print("Identity verification failed; do not enable reporting. Recheck consent, claims, expiry and network.", file=sys.stderr)
        return 1
    print(json.dumps({"verified": True, "aud": args.audience, "sub": args.subject, "email_verified": True}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
