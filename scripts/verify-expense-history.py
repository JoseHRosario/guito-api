#!/usr/bin/env python3
"""Read-only staging export acceptance; financial payloads stay in process memory."""
import datetime
import decimal
import json
import os
import subprocess
import urllib.parse
import urllib.request


REGION = "eu-west-1"


def aws(*arguments):
    result = subprocess.run(
        ["aws", "--region", REGION, *arguments, "--output", "json", "--no-cli-pager"],
        check=True, capture_output=True, text=True,
    )
    return json.loads(result.stdout)


def instant(value):
    if value is None:
        return None
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(datetime.timezone.utc)


def canonical(expense):
    return (
        expense["id"], instant(expense["bookingDate"]),
        decimal.Decimal(expense["amount"]), expense["currency"],
        expense["description"], expense["category"], expense["creatorEmail"],
        instant(expense["creationDate"]), instant(expense["updateDate"]),
    )


def stored_history(cluster, secret):
    records = {}
    after = 0
    while True:
        result = aws(
            "rds-data", "execute-statement", "--resource-arn", cluster,
            "--secret-arn", secret, "--database", "guito_staging",
            "--sql", """SELECT e.id::text, e.booking_date::text, e.amount::text,
                e.description, c.name, e.creator_email, e.create_date::text,
                e.update_date::text FROM expenses e JOIN categories c ON c.id=e.category_id
                WHERE e.id > :after ORDER BY e.id LIMIT 100""",
            "--parameters", json.dumps([{"name": "after", "value": {"longValue": after}}]),
        )
        rows = result.get("records", [])
        if not rows:
            return records
        for row in rows:
            values = [None if field.get("isNull") else field["stringValue"] for field in row]
            expense = dict(zip(
                ["id", "bookingDate", "amount", "description", "category", "creatorEmail", "creationDate", "updateDate"],
                values,
            ))
            if expense["id"] is None:
                raise RuntimeError("Stored Expense has no identity")
            expense["id"] = int(expense["id"])
            expense["currency"] = "EUR"
            records[expense["id"]] = canonical(expense)
        after = max(records)


def export_history(human=False):
    records = {}
    cursor = None
    cursors = set()
    snapshot_id = None
    total = None
    previous_order = None
    pages = 0
    while True:
        query = {"pageSize": 37}
        if cursor is not None:
            query["cursor"] = cursor
        headers = {"Authorization": os.environ["GUITO_TARGET_AGENT_KEY"],
                   "X-Api-Key": os.environ["GUITO_TARGET_AGENT_KEY"]}
        if human:
            token = os.environ["GUITO_TARGET_GOOGLE_ID_TOKEN"]
            headers = {"Authorization": f"Bearer {token}", "x-google-idtoken": token}
        request = urllib.request.Request(
            f"{os.environ['GUITO_TARGET_BASE_URL'].rstrip('/')}/Expense/history?{urllib.parse.urlencode(query)}",
            headers=headers,
        )
        with urllib.request.urlopen(request, timeout=90) as response:
            page = json.load(response)
            if "no-store" not in response.headers.get("Cache-Control", ""):
                raise RuntimeError("Financial export response must disable caching")
        pages += 1
        if pages == 1:
            snapshot_id, total = page["snapshotId"], page["totalCount"]
        if page["snapshotId"] != snapshot_id or page["totalCount"] != total:
            raise RuntimeError("Snapshot metadata changed between pages")
        for expense in page["expenses"]:
            if not isinstance(expense["amount"], str):
                raise RuntimeError("Exact amount must be JSON text")
            record = canonical(expense)
            order = (record[1], record[0])
            if previous_order is not None and order >= previous_order:
                raise RuntimeError("Export ordering is not strictly occurrence/identity descending")
            previous_order = order
            if record[0] in records:
                raise RuntimeError("Export returned duplicate identity")
            records[record[0]] = record
        cursor = page["nextCursor"]
        if cursor is None:
            if len(records) != total:
                raise RuntimeError("Export total does not match collected history")
            return records, pages
        if not page["expenses"] or cursor in cursors:
            raise RuntimeError("Export did not make progress")
        cursors.add(cursor)


def main():
    cluster = os.environ.get("GUITO_HISTORY_CLUSTER_ARN", "arn:aws:rds:eu-west-1:497087877832:cluster:db-cluster")
    secret = aws("secretsmanager", "describe-secret", "--secret-id", "guito-api/db-staging")["ARN"]
    before = stored_history(cluster, secret)
    exported, pages = export_history()
    human_exported, human_pages = export_history(human=True)
    after = stored_history(cluster, secret)
    if before != after:
        raise RuntimeError("Stored Expenses changed during acceptance; rerun against quiescent staging")
    if not before:
        raise RuntimeError("Staging seeded history is empty; empty export is not full-history acceptance")
    if exported != before or human_exported != before:
        raise RuntimeError("Export/stored Expense per-record parity failed (payload withheld)")
    print(f"Expense-history acceptance passed: {len(exported)} stored Expenses, agent {pages} pages / human {human_pages} pages, exact per-record parity; no Expense writes.")


if __name__ == "__main__":
    main()
