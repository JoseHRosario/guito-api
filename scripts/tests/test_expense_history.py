"""Offline synthetic fixtures only: no live HTTP, AWS credentials, or writes."""
import contextlib
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "history_acceptance", Path(__file__).parents[1] / "verify-expense-history.py")
assert SPEC is not None and SPEC.loader is not None
HISTORY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HISTORY)


class ExpenseHistoryAcceptanceTests(unittest.TestCase):
    def setUp(self):
        # Deliberately repeated purchases and tied instants, with distinct IDs.
        self.source = [{"id": i, "bookingDate": "2026-07-01T12:00:00.123456Z",
                        "creationDate": "2026-01-01T12:00:00Z", "updateDate": None,
                        "amount": "9007199254740993.2300", "currency": "EUR",
                        "description": "PRIVATE synthetic purchase", "category": "Fixture",
                        "creatorEmail": "fixture@example.invalid"}
                       for i in range(29, 0, -1)]
        self.source[0]["updateDate"] = "2026-07-02T12:00:00Z"
        self.wire = copy.deepcopy(self.source)
        for row in self.wire:
            row["bookingDate"] = "2026-07-01T13:00:00.123456+01:00"
        self.wire[0]["updateDate"] = "2026-07-02T13:00:00+01:00"
        self.after = copy.deepcopy(self.source)
        self.requests = []
        self.db_reads = 0
        self.mutate_page = lambda page, index, human: None
        self.env = {"GUITO_TARGET_BASE_URL": "https://staging.example.invalid",
                    "GUITO_TARGET_AGENT_KEY": "offline-agent", "GUITO_TARGET_GOOGLE_ID_TOKEN": "offline-token"}

    def transport(self, args, **kwargs):
        self.requests.append((args, kwargs))
        if args[0] == "curl":
            config = kwargs["input"]
            index = int(config.split("pageIndex=")[1].split('"')[0])
            human = "Bearer offline-token" in config
            self.assertIn("x-google-idtoken: offline-token" if human else
                          "X-Api-Key: offline-agent", config)
            self.assertNotIn("X-Sheet-Scope", config)
            self.assertIn("pageSize=7", config)
            page = {"pageSize": 7, "pageIndex": index, "totalCount": len(self.wire),
                    "hasMore": (index + 1) * 7 < len(self.wire),
                    "expenses": copy.deepcopy(self.wire[index*7:(index+1)*7])}
            self.mutate_page(page, index, human)
            body = json.dumps(page)
        elif "describe-secret" in args:
            self.assertEqual(args[args.index("--secret-id") + 1], "guito-api/db-staging")
            body = json.dumps({"ARN": "arn:offline:db-staging"})
        else:
            self.assertIn("execute-statement", args)
            self.assertEqual(args[args.index("--database") + 1], "guito_staging")
            self.assertEqual(args[args.index("--resource-arn") + 1], HISTORY.CLUSTER)
            sql = args[args.index("--sql") + 1]
            self.assertTrue(sql.startswith("SELECT "))
            self.assertIn("e.amount::text", sql)
            self.assertNotIn("LIMIT", sql)
            rows = self.source if self.db_reads == 0 else self.after
            self.db_reads += 1
            body = json.dumps({"records": [[{"isNull": True} if row[field] is None else
                                {"longValue" if field == "id" else "stringValue": row[field]}
                                for field in HISTORY.FIELDS] for row in rows]})
        return subprocess.CompletedProcess(args, 0, body, "")

    def invoke(self):
        output = io.StringIO()
        with patch.dict(os.environ, self.env, clear=True), \
             patch.object(HISTORY.subprocess, "run", side_effect=self.transport), \
             patch.object(HISTORY.sys, "argv", ["verify-expense-history.py", "staging"]), \
             contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            code = HISTORY.main()
        self.assertNotIn("PRIVATE", output.getvalue())
        self.assertNotIn("9007199254740993", output.getvalue())
        self.assertNotIn("offline-token", output.getvalue())
        return code, output.getvalue()

    def test_complete_both_auth_paths_and_bracketed_source_reads(self):
        self.assertEqual(self.invoke()[0], 0)
        self.assertEqual(self.db_reads, 2)
        self.assertEqual(sum(args[0] == "curl" for args, _ in self.requests), 10)
        self.assertEqual(self.requests[1][0][0], "aws")
        self.assertEqual(self.requests[-1][0][0], "aws")
        for args, _ in self.requests:
            self.assertNotIn("offline-agent", args)
            self.assertNotIn("offline-token", args)

    def test_rejects_representation_and_contract_mutations(self):
        mutations = {
            "trailing zeros": lambda p: p["expenses"][0].update(amount="9007199254740993.23"),
            "numeric amount": lambda p: p["expenses"][0].update(amount=1.23),
            "summer UTC same instant": lambda p: p["expenses"][0].update(bookingDate="2026-07-01T12:00:00.123456Z"),
            "winter wrong offset": lambda p: p["expenses"][0].update(creationDate="2026-01-01T13:00:00+01:00"),
            "update wrong offset": lambda p: p["expenses"][0].update(updateDate="2026-07-02T12:00:00Z"),
            "changed instant": lambda p: p["expenses"][0].update(bookingDate="2026-07-01T13:00:01.123456+01:00"),
            "duplicate": lambda p: p["expenses"][0].update(id=28),
            "string id": lambda p: p["expenses"][0].update(id="29"),
            "large id": lambda p: p["expenses"][0].update(id=2147483648),
            "order": lambda p: p["expenses"].reverse(),
            "missing record": lambda p: p["expenses"].pop(),
            "category": lambda p: p["expenses"][0].update(category="Wrong"),
            "creator": lambda p: p["expenses"][0].update(creatorEmail="Wrong"),
            "currency": lambda p: p["expenses"][0].update(currency="USD"),
            "total": lambda p: p.update(totalCount=28),
            "size": lambda p: p.update(pageSize=100),
            "index": lambda p: p.update(pageIndex=1),
            "more": lambda p: p.update(hasMore=False),
            "boolean total": lambda p: p.update(totalCount=True),
        }
        for name, mutate in mutations.items():
            with self.subTest(name=name):
                self.mutate_page = lambda p, i, h: mutate(p) if i == 0 else None
                self.db_reads = 0
                self.assertEqual(self.invoke()[0], 1)

    def test_human_path_cannot_silently_fail_parity(self):
        self.mutate_page = lambda p, i, h: p["expenses"][0].update(amount="0") if h and i == 0 else None
        self.assertEqual(self.invoke()[0], 1)

    def test_rejects_drift_and_source_export_count_mismatch(self):
        self.after[0]["amount"] = "0"
        self.assertEqual(self.invoke()[0], 1)
        self.source.pop()
        self.db_reads = 0
        self.assertEqual(self.invoke()[0], 1)

    def test_accepts_changed_dataset_size_without_a_fixed_seed_count(self):
        self.source = self.source[:8]
        self.wire = self.wire[:8]
        self.after = copy.deepcopy(self.source)
        code, output = self.invoke()
        self.assertEqual(code, 0)
        self.assertIn("8 records, 2 pages per auth path", output)

    def test_rejects_cross_page_duplicate_and_last_page_metadata(self):
        self.wire[7] = copy.deepcopy(self.wire[6])
        self.assertEqual(self.invoke()[0], 1)
        self.wire = copy.deepcopy(self.source)
        for row in self.wire:
            row["bookingDate"] = "2026-07-01T13:00:00.123456+01:00"
        self.wire[0]["updateDate"] = "2026-07-02T13:00:00+01:00"
        self.db_reads = 0
        self.mutate_page = lambda p, i, h: p.update(hasMore=True) if i == 4 else None
        self.assertEqual(self.invoke()[0], 1)

    def test_production_guard_precedes_transport(self):
        with patch.object(HISTORY.subprocess, "run") as transport:
            with self.assertRaises(HISTORY.VerificationError):
                HISTORY.verify("production")
            transport.assert_not_called()

    def test_transport_failure_never_logs_private_response(self):
        output = io.StringIO()
        with patch.dict(os.environ, self.env, clear=True), \
             patch.object(HISTORY.subprocess, "run", return_value=
                          subprocess.CompletedProcess([], 1, "PRIVATE financial body", "PRIVATE secret")), \
             patch.object(HISTORY.sys, "argv", ["verify-expense-history.py", "staging"]), \
             contextlib.redirect_stderr(output):
            self.assertEqual(HISTORY.main(), 1)
        self.assertNotIn("PRIVATE", output.getvalue())


if __name__ == "__main__":
    unittest.main()
