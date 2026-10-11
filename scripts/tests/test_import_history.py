"""Exercise the operator entry point with frozen Sheets and fake DB transport."""
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location('history', Path(__file__).parents[1] / 'import-history.py')
history = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(history)


class FakeDatabase:
    def __init__(self, rows):
        self.rows = rows
        self.reads = 0

    def execute(self, sql, parameters):
        if not sql.lstrip().startswith('SELECT'):
            raise AssertionError('Comparison must never write')
        self.reads += 1
        return {'records': [[{'stringValue': history.dump(self.rows)}]]}


class HistoricalSqlEntryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.snapshot = self.directory / 'frozen.json'
        self.output = self.directory / 'output'
        self.source = {'SpreadsheetId': history.STAGING_SHEET, 'Tab': 'Expenses', 'FirstRow': 5, 'LastRow': 7,
                       'Currency': 'EUR', 'DecimalSeparator': '.', 'DateFormat': 'M/d/yyyy',
                       'CapturedAt': '2026-01-01T00:00:00+00:00',
                       'Rows': [['7/10/2026', '', '', '€0.01', 'Coffee', 'Restaurants', 'creator@example.com'],
                                ['7/10/2026', '', '', '€0.01', 'Coffee', 'Restaurants', 'creator@example.com'], []],
                       'UnformattedRows': [[46213, '', '', 0.011, 'Coffee', 'Restaurants', 'creator@example.com'],
                                           [46213, '', '', 0.011, 'Coffee', 'Restaurants', 'creator@example.com'], []]}
        self.save()

    def save(self):
        self.snapshot.write_text(json.dumps(self.source))

    def run_entry(self, operation, database=None, extra=()):
        return history.main([operation, str(self.snapshot), str(self.output), '--database', 'guito_staging', *extra], database)

    def actual(self, package):
        result = []
        for index, expected in enumerate(package['rows']):
            expense = expected['classification'] == 'Expense'
            result.append({'source_row': expected['sourceRow'], 'raw_json': expected['raw'], 'classification': expected['classification'],
                           'expense_id': index + 101 if expense else None, 'exclusion': expected.get('exclusion'),
                           'amount': expected.get('amount'), 'booking_date': expected.get('bookingDate'),
                           'description': expected.get('description'), 'category': expected.get('category'),
                           'creator': expected.get('creator'), 'create_date': '2026-10-10T21:00:00+00:00' if expense else None,
                           'update_date': None})
        return result

    def test_convert_preserves_identical_purchases_and_hidden_precision(self):
        self.assertEqual(0, self.run_entry('convert'))
        rows = history.loads((self.output / 'staging-rows.json').read_text())['rows']
        self.assertEqual([5, 6, 7], [row['sourceRow'] for row in rows])
        self.assertEqual(['0.011', '0.011'], [row['amount'] for row in rows[:2]])
        self.assertEqual('2026-07-10T00:00:00+01:00', rows[0]['bookingDate'])
        self.assertIn('formattingDifference', rows[0])
        sql = (self.output / 'import.sql').read_text()
        self.assertTrue(sql.startswith('BEGIN;'))
        self.assertTrue(sql.rstrip().endswith('COMMIT;'))
        self.assertEqual(self.snapshot.read_bytes(), next(self.output.glob('*.snapshot.json')).read_bytes())

    def test_compare_checks_records_and_counts_using_read_only_transport(self):
        package = history.prepare(self.snapshot, self.output, 'guito_staging')
        database = FakeDatabase(self.actual(package))
        self.assertEqual(0, self.run_entry('compare', database))
        report = history.loads((self.output / 'report.json').read_text())
        self.assertTrue(report['readyForCutover'])
        self.assertEqual('0.022', report['importedTotal'])
        self.assertEqual(2, report['counts']['Expense'])
        self.assertEqual(1, report['counts']['Blank'])
        self.assertGreater(database.reads, 0)

    def test_compare_blocks_amount_attribution_audit_and_missing_record_mismatches(self):
        package = history.prepare(self.snapshot, self.output, 'guito_staging')
        for field, altered in [('amount', '0.01'), ('creator', 'other@example.com'), ('category', 'Other'),
                               ('booking_date', '2026-07-10T00:00:00+00:00'), ('update_date', '2026-10-11T21:00:00+00:00')]:
            rows = self.actual(package)
            rows[0][field] = altered
            with self.subTest(field=field):
                self.assertEqual(2, self.run_entry('compare', FakeDatabase(rows)))
        self.assertEqual(2, self.run_entry('compare', FakeDatabase(self.actual(package)[1:])))

    def test_malformed_rows_block_and_approved_exclusions_are_explicit(self):
        self.source['Rows'][0][3] = 'USD 1.00'
        self.save()
        self.assertEqual(2, self.run_entry('convert'))
        package = history.loads((self.output / 'staging-rows.json').read_text())
        approvals = self.directory / 'approval.json'
        approvals.write_text(json.dumps({'snapshotId': package['snapshotId'], 'exclusions': {'5': {'approvedBy': 'José', 'reason': 'Reviewed source'}}}))
        self.assertEqual(0, self.run_entry('convert', extra=('--approvals', str(approvals))))
        package = history.loads((self.output / 'staging-rows.json').read_text())
        self.assertEqual('Excluded', package['rows'][0]['classification'])
        self.assertEqual(0, self.run_entry('compare', FakeDatabase(self.actual(package)), ('--approvals', str(approvals))))

    def test_wrong_environment_incomplete_history_and_hidden_zero_are_rejected(self):
        with self.assertRaises(ValueError):
            history.main(['convert', str(self.snapshot), str(self.output), '--database', 'guito_prod'])
        self.source['LastRow'] = 8
        self.save()
        with self.assertRaises(ValueError):
            self.run_entry('convert')
        self.source['LastRow'] = 7
        self.source['UnformattedRows'][2] = ['', '', '', 0]
        self.save()
        self.assertEqual(2, self.run_entry('convert'))

    def test_review_regressions_anchor_nonexpense_links_and_retry_identity(self):
        package = history.prepare(self.snapshot, self.output, 'guito_staging')
        baseline = history.compare(package, self.actual(package))
        for field, value in [('expense_id', 999), ('create_date', '2026-10-09T21:00:00+00:00')]:
            rows = self.actual(package)
            rows[0][field] = value
            self.assertFalse(history.compare(package, rows, baseline)['readyForCutover'])
        rows = self.actual(package)
        rows[2].update(expense_id=999, amount='99')
        self.assertFalse(history.compare(package, rows)['readyForCutover'])
        self.source['FirstRow'], self.source['LastRow'] = 6, 8
        self.save()
        with self.assertRaises(ValueError):
            self.run_entry('convert')

    def test_changed_archive_has_different_identity_and_sql_stops_at_source_head(self):
        self.run_entry('convert')
        old = history.loads((self.output / 'staging-rows.json').read_text())['snapshotId']
        self.source['Rows'][0][4] = 'Changed'
        self.source['UnformattedRows'][0][4] = 'Changed'
        self.save()
        self.run_entry('convert')
        package = history.loads((self.output / 'staging-rows.json').read_text())
        self.assertNotEqual(old, package['snapshotId'])
        self.assertIn('current_snapshot_id=', (self.output / 'import.sql').read_text())


if __name__ == '__main__':
    unittest.main()
