#!/usr/bin/env python3
"""One-time migration for ADR 0010: flip negative Expense amounts to positive.

Reads a numeric column (default E = Amount) on a spreadsheet tab, negates every
negative value, and writes the corrected cells back. Defaults to DRY-RUN; the
write only happens with --apply. A JSON backup of the whole tab is written
before any write.

Row addressing is absolute (values.get/values.update include hidden rows), so
the hidden-rows append bug that motivated ADR 0009 cannot bite here: a hidden
row is still read and still corrected at its true row index.

Usage:
  python3 scripts/migrate-amounts-positive.py \
      --key-file src/guito-api/google-spreadsheets-dev.json \
      --spreadsheet-id <id> \
      [--tab Expenses] [--column E] [--apply]

Requires: python3 with `cryptography` and `requests`.

Note: the pre-apply backup is a plaintext JSON file written to --backup-dir
(default CWD); it contains the tab's full contents including any formulas.
Keep it out of any committed or shared location.
"""
import argparse
import base64
import json
import os
import sys
import time
from datetime import datetime, timezone

import requests
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import padding
from cryptography.hazmat.primitives.serialization import load_pem_private_key

TOKEN_URL = "https://oauth2.googleapis.com/token"
SCOPE = "https://www.googleapis.com/auth/spreadsheets"


def sa_access_token(key_file: str) -> str:
    with open(key_file, "r", encoding="utf-8") as fh:
        sa = json.load(fh)
    header = {"alg": "RS256", "typ": "JWT"}
    now = int(time.time())
    claims = {
        "iss": sa["client_email"],
        "scope": SCOPE,
        "aud": TOKEN_URL,
        "iat": now,
        "exp": now + 3600,
    }

    def b64(obj):
        raw = json.dumps(obj, separators=(",", ":")).encode()
        return base64.urlsafe_b64encode(raw).rstrip(b"=")

    signing_input = b64(header) + b"." + b64(claims)
    key = load_pem_private_key(sa["private_key"].encode(), password=None)
    sig = key.sign(signing_input, padding.PKCS1v15(), hashes.SHA256())
    assertion = signing_input + b"." + base64.urlsafe_b64encode(sig).rstrip(b"=")
    resp = requests.post(
        TOKEN_URL,
        data={
            "grant_type": "urn:ietf:params:oauth:grant-type:jwt-bearer",
            "assertion": assertion.decode(),
        },
        timeout=30,
    )
    resp.raise_for_status()
    return resp.json()["access_token"]


def sheets_get(token: str, spreadsheet_id: str, range_a1: str, render: str = "FORMATTED_VALUE"):
    url = f"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheet_id}/values/{requests.utils.quote(range_a1)}"
    resp = requests.get(
        url,
        headers={"Authorization": f"Bearer {token}"},
        params={"valueRenderOption": render},
        timeout=30,
    )
    resp.raise_for_status()
    return resp.json().get("values", [])


def sheets_batch_update(token: str, spreadsheet_id: str, data_rows, value_input_option: str):
    url = f"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheet_id}/values:batchUpdate"
    body = {
        "valueInputOption": value_input_option,
        "data": data_rows,
    }
    resp = requests.post(
        url, headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
        json=body, timeout=30,
    )
    resp.raise_for_status()
    return resp.json()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--key-file", required=True, help="Google service-account JSON key file")
    parser.add_argument("--spreadsheet-id", required=True, help="Target spreadsheet id (explicit; never defaulted)")
    parser.add_argument("--tab", default="Expenses", help="Tab name (default: Expenses)")
    parser.add_argument("--column", default="E", help="Amount column letter (default: E per the row layout)")
    parser.add_argument("--apply", action="store_true", help="Actually write. Omit for dry-run.")
    parser.add_argument("--backup-dir", default=".", help="Directory for the pre-apply tab backup (default: CWD)")
    args = parser.parse_args()

    if not os.path.isfile(args.key_file):
        print(f"FATAL: key file not found: {args.key_file}")
        return 2

    column = args.column.upper()
    if not (len(column) == 1 and "A" <= column <= "Z"):
        print(f"FATAL: unsupported column letter: {column}")
        return 2

    # UNFORMATTED_VALUE: the scan must see the raw number (a formatted cell
    # renders '€55.39' as a string and would silently skip negatives).
    token = sa_access_token(args.key_file)
    rng = f"{args.tab}!{column}1:{column}"
    values = sheets_get(token, args.spreadsheet_id, rng, render="UNFORMATTED_VALUE")

    fixes = []  # (row_index, old, new)
    for i, row in enumerate(values):
        raw = row[0] if row else ""
        try:
            number = float(raw)
        except (TypeError, ValueError):
            continue  # header text, blanks, non-numeric cells
        if number < 0:
            fixes.append((i + 1, raw, -number))

    print(f"Target: spreadsheet {args.spreadsheet_id} tab '{args.tab}' column {column}")
    print(f"Scanned {len(values)} rows; found {len(fixes)} negative amount(s)")
    for row_index, old, new in fixes:
        print(f"  row {row_index}: {old} -> {new}")

    if not fixes:
        print("Nothing to do.")
        return 0

    if not args.apply:
        print("DRY-RUN: no writes performed. Re-run with --apply to migrate.")
        return 0

    os.makedirs(args.backup_dir, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    backup_path = os.path.join(args.backup_dir, f"{args.tab}-backup-{stamp}.json")
    full_tab = sheets_get(token, args.spreadsheet_id, f"{args.tab}!A1:Z100000", render="FORMULA")
    with open(backup_path, "w", encoding="utf-8") as fh:
        json.dump({"spreadsheetId": args.spreadsheet_id, "tab": args.tab, "values": full_tab}, fh, indent=1)
    print(f"Backup written: {backup_path}")

    data_rows = [
        {"range": f"{args.tab}!{column}{row_index}", "values": [[new]]}
        for row_index, _, new in fixes
    ]
    result = sheets_batch_update(token, args.spreadsheet_id, data_rows, "USER_ENTERED")
    print(f"Updated {result.get('totalUpdatedCells', '?')} cell(s). Migration complete.")

    after = sheets_get(token, args.spreadsheet_id, rng, render="UNFORMATTED_VALUE")
    remaining = 0
    for row in after:
        raw = row[0] if row else ""
        try:
            if float(raw) < 0:
                remaining += 1
        except (TypeError, ValueError):
            pass
    if remaining:
        print(f"WARNING: {remaining} negative value(s) remain after the write — investigate before proceeding.")
        return 1
    print("Verification: no negative amounts remain in the column.")
    return 0


if __name__ == "__main__":
    sys.exit(main())