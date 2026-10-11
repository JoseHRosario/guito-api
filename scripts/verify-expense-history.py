#!/usr/bin/env python3
"""Read-only staging acceptance; runner supplies resolved auth credentials.

Live page-index reads are not snapshots. Source reads bracket BOTH auth walks;
any observed source drift fails acceptance, without writing or freezing data.
Only counts/status may reach CI logs; subprocess bodies and errors stay private.
"""
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone
from urllib.parse import urlsplit
from zoneinfo import ZoneInfo

PAGE_SIZE = 7
CLUSTER = "arn:aws:rds:eu-west-1:497087877832:cluster:db-cluster"
FIELDS = ("id", "bookingDate", "creationDate", "updateDate", "amount",
          "currency", "description", "category", "creatorEmail")
SQL = """SELECT e.id,
 to_char(e.booking_date AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
 to_char(e.create_date AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
 to_char(e.update_date AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
 e.amount::text, 'EUR', e.description, c.name, e.creator_email
 FROM expenses e LEFT JOIN categories c ON c.id = e.category_id
 ORDER BY e.booking_date DESC, e.id DESC"""


class VerificationError(Exception):
    """Messages contain contract labels only, never source values."""


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def run_private(arguments, **kwargs):
    result = subprocess.run(arguments, capture_output=True, text=True,
                            timeout=90, **kwargs)
    require(result.returncode == 0, "read transport failed")
    return result.stdout


def aws(*arguments):
    return run_private(["aws", "--region", "eu-west-1", *arguments,
                        "--no-cli-pager", "--output", "json"])


def stored_rows(secret):
    result = json.loads(aws("rds-data", "execute-statement", "--resource-arn",
                            CLUSTER, "--secret-arn", secret, "--database",
                            "guito_staging", "--sql", SQL))
    rows = []
    for record in result["records"]:
        require(len(record) == len(FIELDS), "source column count mismatch")
        values = []
        for cell in record:
            if cell == {"isNull": True}:
                values.append(None)
            else:
                require(len(cell) == 1 and next(iter(cell)) in
                        ("longValue", "stringValue"), "source field type mismatch")
                values.append(next(iter(cell.values())))
        rows.append(dict(zip(FIELDS, values)))
    return rows


def instant(value, wire=False):
    require(isinstance(value, str) and re.fullmatch(
        r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?(?:Z|[+-]\d{2}:\d{2})",
        value) is not None, "timestamp format mismatch")
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if wire:
        require(parsed.utcoffset() == parsed.astimezone(ZoneInfo("Europe/Lisbon")).utcoffset(),
                "timestamp Lisbon offset mismatch")
    return parsed.astimezone(timezone.utc)


def validate_row(row, wire=False):
    require(isinstance(row, dict) and set(row) == set(FIELDS), "expense fields mismatch")
    require(type(row["id"]) is int and -(2**31) <= row["id"] < 2**31,
            "expense id must be Int32")
    require(isinstance(row["amount"], str) and re.fullmatch(
        r"-?\d+(?:\.\d+)?", row["amount"]) is not None, "amount must be exact decimal text")
    require(row["currency"] == "EUR", "currency mismatch")
    for field in ("description", "category", "creatorEmail"):
        require(isinstance(row[field], str), "expense text field mismatch")
    for field in ("bookingDate", "creationDate", "updateDate"):
        if field == "updateDate" and row[field] is None:
            continue
        instant(row[field], wire)


def ordering(row):
    return instant(row["bookingDate"]), row["id"]


def get_page(base, headers, index):
    url = f"{base}/Expense/history?pageSize={PAGE_SIZE}&pageIndex={index}"
    # Headers go through stdin, not command-line args or an on-disk config.
    config = '\n'.join([f'url = {json.dumps(url)}'] +
                       [f'header = {json.dumps(name + ": " + value)}'
                        for name, value in headers.items()])
    return json.loads(run_private(["curl", "--disable", "--silent", "--show-error", "--fail-with-body",
                                  "--max-time", "60", "--config", "-"], input=config))


def read_all(base, headers, count):
    rows = []
    for index in range((count + PAGE_SIZE - 1) // PAGE_SIZE):
        page = get_page(base, headers, index)
        require(isinstance(page, dict) and set(page) ==
                {"pageSize", "pageIndex", "totalCount", "hasMore", "expenses"},
                "page fields mismatch")
        for name, expected in (("pageSize", PAGE_SIZE), ("pageIndex", index), ("totalCount", count)):
            require(type(page[name]) is int and page[name] == expected, "page metadata mismatch")
        more = (index + 1) * PAGE_SIZE < count
        require(type(page["hasMore"]) is bool and page["hasMore"] == more, "hasMore mismatch")
        require(isinstance(page["expenses"], list) and len(page["expenses"]) ==
                min(PAGE_SIZE, count - index * PAGE_SIZE), "page record count mismatch")
        for row in page["expenses"]:
            validate_row(row, wire=True)
        rows.extend(page["expenses"])
    require(len({row["id"] for row in rows}) == count, "duplicate expense id")
    require(rows == sorted(rows, key=ordering, reverse=True), "expense order mismatch")
    return rows


def compare(expected, actual):
    require(len(expected) == len(actual), "expense count mismatch")
    for source, wire in zip(expected, actual):
        for field in FIELDS:
            left, right = source[field], wire[field]
            if field in ("bookingDate", "creationDate", "updateDate"):
                left = instant(left) if left is not None else None
                right = instant(right, wire=True) if right is not None else None
            require(left == right, f"expense parity mismatch: {field}")


def verify(environment):
    require(environment == "staging", "history acceptance is staging-only")
    base = os.environ["GUITO_TARGET_BASE_URL"].rstrip("/")
    parsed = urlsplit(base)
    require(parsed.scheme == "https" and parsed.hostname and not parsed.username
            and not parsed.password and not parsed.query and not parsed.fragment
            and parsed.path in ("", "/"), "invalid staging base URL")
    key = os.environ["GUITO_TARGET_AGENT_KEY"]
    token = os.environ["GUITO_TARGET_GOOGLE_ID_TOKEN"]
    require(bool(key) and bool(token) and not any(c in key + token for c in '\r\n'),
            "missing or invalid auth credentials")
    # Discover only metadata; never retrieve database credentials into this process.
    secret = json.loads(aws("secretsmanager", "describe-secret", "--secret-id",
                            "guito-api/db-staging"))["ARN"]
    before = stored_rows(secret)
    require(bool(before), "staging seeded history must not be empty")
    for row in before:
        validate_row(row)
    require(len({row["id"] for row in before}) == len(before), "duplicate source id")
    for headers in ({"Authorization": key, "X-Api-Key": key},
                    {"Authorization": f"Bearer {token}", "x-google-idtoken": token}):
        compare(before, read_all(base, headers, len(before)))
    require(before == stored_rows(secret), "staging source drift during acceptance")
    pages = (len(before) + PAGE_SIZE - 1) // PAGE_SIZE
    print(f"Expense history parity OK: {len(before)} records, {pages} pages per auth path; source unchanged.")


def main():
    try:
        verify(sys.argv[1] if len(sys.argv) == 2 else "")
    except VerificationError as error:
        print(f"Expense history acceptance FAILED: {error}", file=sys.stderr)
        return 1
    except Exception:
        # JSON/transport exceptions can embed private payloads; never print them.
        print("Expense history acceptance FAILED: invalid response or read failure", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
