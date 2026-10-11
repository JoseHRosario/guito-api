#!/usr/bin/env python3
"""Frozen Sheets JSON -> exact rows -> operator-run SQL and read-only comparison.

This program NEVER executes imports. José runs the generated SQL in the target database.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal, localcontext
from pathlib import Path
from zoneinfo import ZoneInfo

ROOT = Path(__file__).resolve().parents[1]
CLUSTER = 'arn:aws:rds:eu-west-1:497087877832:cluster:db-cluster'
STAGING_SHEET = '1kSiNmd_aaVMMNsDA9W7lUoKmX3jHrpSK75ypU5_G5Zk'


def loads(text):
    return json.loads(text, parse_float=Decimal, parse_int=Decimal)


def dump(value):
    # Raw numeric cells remain numeric, rather than becoming quoted/rounded doubles.
    if isinstance(value, Decimal):
        return str(value)
    if isinstance(value, dict):
        return '{' + ','.join(json.dumps(str(k)) + ':' + dump(v) for k, v in value.items()) + '}'
    if isinstance(value, list):
        return '[' + ','.join(dump(v) for v in value) + ']'
    return json.dumps(value, ensure_ascii=False)


def amount(text, separator):
    integer = r'(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)' if separator == '.' else r'[0-9]+'
    pattern = rf'\s*(EUR\s+|€\s*)?([+-]?{integer}(?:{re.escape(separator)}[0-9]+)?(?:[eE][+-]?[0-9]+)?)(\s*€|\s+EUR)?\s*'
    match = re.fullmatch(pattern, str(text))
    if not match or (match[1] and match[3]):
        raise ValueError('Unsupported currency or amount format')
    value = Decimal(match[2].replace(',', '') if separator == '.' else match[2].replace(',', '.'))
    if not value.is_finite():
        raise ValueError('Non-finite amount')
    sign, digits, exponent = value.as_tuple()
    exponent = int(exponent)
    coefficient = int(''.join(map(str, digits)))
    while coefficient and exponent < 0 and coefficient % 10 == 0:
        coefficient //= 10
        exponent += 1
    if exponent < -28 or exponent > 28 or coefficient * 10 ** max(exponent, 0) > 2 ** 96 - 1:
        raise ValueError('Amount is not exactly representable by the Expense decimal contract')
    return value


def original_day(text, convention):
    formats = {'yyyy-MM-dd': r'(\d{4})-(\d{2})-(\d{2})', 'MM/dd/yyyy': r'(\d{2})/(\d{2})/(\d{4})',
               'M/d/yyyy': r'(\d{1,2})/(\d{1,2})/(\d{4})', 'dd/MM/yyyy': r'(\d{2})/(\d{2})/(\d{4})',
               'd/M/yyyy': r'(\d{1,2})/(\d{1,2})/(\d{4})'}
    match = re.fullmatch(formats[convention], str(text))
    if not match:
        raise ValueError('Date does not match the declared source convention')
    a, b, c = map(int, match.groups())
    return date(a, b, c) if convention == 'yyyy-MM-dd' else date(c, b, a) if convention.startswith('d') else date(c, a, b)


def convert_row(snapshot, offset):
    formatted, underlying = snapshot['Rows'][offset], snapshot['UnformattedRows'][offset]
    row = {'sourceRow': int(snapshot['FirstRow']) + offset, 'raw': {'Formatted': formatted, 'Unformatted': underlying}, 'classification': 'Malformed'}
    try:
        if not isinstance(formatted, list) or not isinstance(underlying, list) or max(len(formatted), len(underlying)) > 7:
            raise ValueError('Expected frozen B:H layout')
        cells = [str(v) if v is not None else '' for v in formatted] + [''] * (7 - len(formatted))
        values = underlying + [''] * (7 - len(underlying))
        if any(not cells[i].strip() and str(values[i] if values[i] is not None else '').strip() for i in (0, 3, 4, 5, 6)):
            raise ValueError('Hidden underlying business cell')
        if not any(c.strip() for c in cells):
            row['classification'] = 'Blank'
            return row
        if not any(cells[i].strip() for i in (0, 3, 4, 5, 6)):
            row['classification'] = 'NonExpense'
            return row
        day = original_day(cells[0], snapshot['DateFormat'])
        if isinstance(values[0], Decimal):
            if values[0] != Decimal((day - date(1899, 12, 30)).days):
                raise ValueError('Underlying date differs or contains a historical time')
        elif values[0] != cells[0]:
            raise ValueError('Underlying date differs')
        if any(values[i] != cells[i] for i in (4, 5, 6)) or not cells[5].strip() or not cells[6].strip():
            raise ValueError('Missing or inconsistent category/creator attribution')
        display = amount(cells[3], snapshot['DecimalSeparator'])
        exact = amount(values[3], '.') if isinstance(values[3], Decimal) else amount(values[3], snapshot['DecimalSeparator'])
        if not isinstance(values[3], Decimal) and exact != display:
            raise ValueError('Underlying text disagrees with display')
        row.update(classification='Expense', amount=str(exact), bookingDate=datetime.combine(day, datetime.min.time(), ZoneInfo('Europe/Lisbon')).isoformat(),
                   description=cells[4], category=cells[5], creator=cells[6])
        if exact != display:
            row['formattingDifference'] = 'Exact stored numeric amount preserved; currency display differs.'
    except (ValueError, KeyError, ArithmeticError) as error:
        row['exception'] = str(error)
    return row


def prepare(snapshot_file, output, database, approval_file=None):
    if database not in ('guito_staging', 'guito_prod'):
        raise ValueError('Explicit staging or production target required')
    raw = Path(snapshot_file).read_bytes()
    snapshot = loads(raw)
    config = ROOT / ('src/guito-api/appsettings.Production.json' if database == 'guito_prod' else 'src/guito-api/appsettings.Staging.json')
    configured = json.loads(config.read_text())['AppConfiguration']['Googlesheets']
    tab, anchor = configured['ExpensesRange'].split('!')
    if snapshot['SpreadsheetId'] != configured['SpreadsheetId'] or snapshot['Tab'] != tab or int(snapshot['FirstRow']) != int(anchor[1:]):
        raise ValueError('Snapshot source does not match the selected environment')
    first, last = int(snapshot['FirstRow']), int(snapshot['LastRow'])
    if snapshot['Currency'] != 'EUR' or snapshot['DecimalSeparator'] not in ('.', ',') or not 1 <= first <= last <= first + 100000:
        raise ValueError('Invalid bounded EUR source')
    if len(snapshot['Rows']) != last - first + 1 or len(snapshot['UnformattedRows']) != len(snapshot['Rows']):
        raise ValueError('Incomplete bounded snapshot')
    identity = hashlib.sha256(raw).hexdigest()
    approval = loads(Path(approval_file).read_bytes()) if approval_file else {}
    if approval and approval.get('snapshotId') != identity:
        raise ValueError('Approval does not name the exact archive SHA-256')
    rows = [convert_row(snapshot, i) for i in range(len(snapshot['Rows']))]
    for row in rows:
        exclusion = approval.get('exclusions', {}).get(str(row['sourceRow']))
        if exclusion:
            if not exclusion.get('approvedBy') or not exclusion.get('reason'):
                raise ValueError('Exclusion needs an approver and reason')
            row.update(classification='Excluded', exclusion=exclusion)
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True, mode=0o700)
    output.chmod(0o700)
    archive = output / f'{identity}.snapshot.json'
    if archive.exists() and archive.read_bytes() != raw:
        raise ValueError('Archive identity collision')
    if not archive.exists():
        with archive.open('xb') as file:
            file.write(raw)
    archive.chmod(0o600)
    package = {'snapshotId': identity, 'sourceKey': f"{snapshot['SpreadsheetId']}:{snapshot['Tab']}:{first}", 'database': database, 'snapshot': snapshot, 'rows': rows, 'approval': approval}
    write_private(output / 'staging-rows.json', package)
    write_sql(output / 'import.sql', package)
    return package


def write_private(path, value):
    path.write_text(dump(value), encoding='utf-8')
    path.chmod(0o600)


def literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def render(sql, parameters):
    # Match whole parameter tokens; data is never interpolated as executable SQL.
    return re.sub(r':(source_key|snapshot_id|row)\b', lambda m: literal(parameters[m[1]]), sql)


def write_sql(path, package):
    template = (ROOT / 'scripts/import-history.sql').read_text()
    identity, source = literal(package['snapshotId']), literal(package['sourceKey'])
    metadata = {k: v for k, v in package['snapshot'].items() if k not in ('Rows', 'UnformattedRows')}
    statements = ['BEGIN;', 'SET TRANSACTION ISOLATION LEVEL READ COMMITTED;', 'SET LOCAL standard_conforming_strings = on;',
                  f"SELECT 1 / CASE WHEN current_database() = {literal(package['database'])} THEN 1 ELSE 0 END;",
                  f"SELECT 1 / CASE WHEN {sum(r['classification'] == 'Malformed' for r in package['rows'])}=0 THEN 1 ELSE 0 END;",
                  (ROOT / 'db/migrations/007_historical_expense_import.sql').read_text(),
                  (ROOT / 'db/migrations/008_historical_expense_categories.sql').read_text(),
                  f"INSERT INTO expense_import_sources VALUES ({source}, {identity}) ON CONFLICT DO NOTHING;",
                  f"SELECT source_key FROM expense_import_sources WHERE source_key={source} FOR UPDATE;",
                  f"SELECT 1 / CASE WHEN current_snapshot_id={identity} THEN 1 ELSE 0 END FROM expense_import_sources WHERE source_key={source};",
                  f"INSERT INTO expense_import_snapshots (id,source_key,metadata,captured_at) VALUES ({identity},{source},{literal(dump(metadata))}::jsonb,{literal(package['snapshot']['CapturedAt'])}::timestamptz) ON CONFLICT DO NOTHING;"]
    categories = sorted({r['category'] for r in package['rows'] if r['classification'] == 'Expense'})
    if categories:
        statements.append(f"SELECT 1 / CASE WHEN count(*)={len(categories)} THEN 1 ELSE 0 END FROM categories WHERE name IN ({','.join(map(literal, categories))});")
    for row in package['rows']:
        parameters = {'source_key': package['sourceKey'], 'snapshot_id': package['snapshotId'], 'row': dump(row)}
        if row['classification'] == 'Excluded':
            statements.append(f"UPDATE expense_import_rows SET classification='Excluded', exclusion={literal(dump(row['exclusion']))}::jsonb WHERE snapshot_id={identity} AND source_row={row['sourceRow']} AND expense_id IS NULL AND raw_json={literal(dump(row['raw']))}::jsonb AND (exclusion IS NULL OR exclusion={literal(dump(row['exclusion']))}::jsonb);")
        if row['classification'] != 'Blank':
            statements.append(render(template, parameters))
    blanks = [r for r in package['rows'] if r['classification'] == 'Blank']
    if blanks:
        statements.append(f"INSERT INTO expense_import_rows (snapshot_id,source_row,raw_json,classification) SELECT {identity}, (v->>'sourceRow')::integer, v->'raw', 'Blank' FROM jsonb_array_elements({literal(dump(blanks))}::jsonb) v ON CONFLICT DO NOTHING;")
    statements.append('COMMIT;')
    path.write_text('\n'.join(statements), encoding='utf-8')
    path.chmod(0o600)


def value(result):
    records = result.get('records', [])
    return loads(records[0][0]['stringValue']) if records else None


def persisted(database, identity):
    rows = []
    for start in range(0, 100001, 200):
        result = value(database.execute('''SELECT COALESCE(jsonb_agg(r), '[]'::jsonb)::text FROM (
            SELECT i.source_row, i.raw_json, i.classification, i.expense_id, i.exclusion,
              e.amount::text AS amount, e.booking_date::text AS booking_date,
              e.description, c.name AS category, e.creator_email AS creator,
              e.create_date::text AS create_date, e.update_date::text AS update_date
            FROM expense_import_rows i LEFT JOIN expenses e ON e.id = i.expense_id
            LEFT JOIN categories c ON c.id = e.category_id WHERE i.snapshot_id = :snapshot_id
            ORDER BY i.source_row LIMIT 200 OFFSET ''' + str(start) + ') r', {'snapshot_id': identity}))
        rows.extend(result or [])
        if len(result or []) < 200:
            break
    return rows


def compare(package, actual, baseline=None):
    lookup = {int(row['source_row']): row for row in actual}
    report = {'snapshotId': package['snapshotId'], 'rows': [], 'exceptions': [], 'readyForCutover': True}
    with localcontext() as context:
        context.prec = 100
        source_total = imported_total = persisted_total = Decimal(0)
        for expected in package['rows']:
            found = lookup.get(expected['sourceRow'])
            comparison = {'sourceRow': expected['sourceRow'], 'classification': expected['classification'], 'expected': expected, 'actual': found, 'parity': False}
            if 'amount' in expected:
                source_total += Decimal(expected['amount'])
            if found and found['amount'] is not None:
                persisted_total += Decimal(found['amount'])
            same = found and found['raw_json'] == expected['raw'] and found['classification'] == expected['classification']
            if expected['classification'] == 'Expense':
                same = same and found['expense_id'] is not None and Decimal(found['amount']) == Decimal(expected['amount'])
                same = same and all(found[k] == expected[k] for k in ('description', 'category', 'creator'))
                same = same and datetime.fromisoformat(found['booking_date']) == datetime.fromisoformat(expected['bookingDate'])
                same = same and found['create_date'] is not None and found['update_date'] is None
                if same:
                    created = datetime.fromisoformat(found['create_date'])
                    same = datetime.fromisoformat(package['snapshot']['CapturedAt']) <= created <= datetime.now(timezone.utc)
            elif expected['classification'] == 'Excluded':
                same = same and found['exclusion'] == expected['exclusion'] and found['expense_id'] is None
            elif expected['classification'] == 'Malformed':
                same = False
            else:
                same = same and found['expense_id'] is None and found['amount'] is None
            if same and baseline and expected['classification'] == 'Expense':
                previous = next((r['actual'] for r in baseline['rows'] if r['sourceRow'] == expected['sourceRow']), None)
                same = previous is not None and all(found[k] == previous[k] for k in ('expense_id', 'create_date', 'update_date'))
            comparison['parity'] = bool(same)
            if same and expected['classification'] == 'Expense':
                imported_total += Decimal(found['amount'])
            if not same:
                report['exceptions'].append({'sourceRow': expected['sourceRow'], 'reason': expected.get('exception', 'Missing record or parity mismatch; verify category mirror and database report')})
            report['rows'].append(comparison)
        report.update(sourceTotal=str(source_total), importedTotal=str(imported_total), persistedTotal=str(persisted_total))
    report['counts'] = {status: sum(r['classification'] == status for r in package['rows']) for status in ('Expense', 'Blank', 'NonExpense', 'Malformed', 'Excluded')}
    report['readyForCutover'] = not report['exceptions'] and len(actual) == len(package['rows']) and imported_total == persisted_total
    return report


class ReadOnlyDataApi:
    def __init__(self, database, profile):
        identity = json.loads(subprocess.check_output(['aws', '--profile', profile, 'sts', 'get-caller-identity'], text=True))['Arn']
        if not identity.startswith('arn:aws:sts::497087877832:assumed-role/MinervaAIAgent/'):
            raise ValueError('Comparison requires MinervaAIAgent')
        self.database, self.profile = database, profile
        environment = 'prod' if database == 'guito_prod' else 'staging'
        self.secret = subprocess.check_output(['aws', '--profile', profile, '--region', 'eu-west-1',
            'secretsmanager', 'describe-secret', '--secret-id', f'guito-api/db-{environment}', '--query', 'ARN', '--output', 'text'], text=True).strip()

    def execute(self, sql, parameters):
        if not sql.lstrip().startswith('SELECT') or ';' in sql:
            raise ValueError('Comparison transport accepts only one SELECT')
        args = ['aws', '--profile', self.profile, '--region', 'eu-west-1', 'rds-data', 'execute-statement',
                '--resource-arn', CLUSTER, '--secret-arn', self.secret, '--database', self.database, '--sql', sql,
                '--parameters', json.dumps([{'name': k, 'value': {'stringValue': v}} for k, v in parameters.items()])]
        result = subprocess.run(args, capture_output=True)
        if result.returncode:
            raise RuntimeError('Read-only database comparison failed')
        return loads(result.stdout)


def main(argv=None, database=None):
    parser = argparse.ArgumentParser(description='Generate operator-run SQL or compare imported data read-only.')
    parser.add_argument('operation', choices=('convert', 'compare'))
    parser.add_argument('snapshot')
    parser.add_argument('output')
    parser.add_argument('--database', choices=('guito_staging', 'guito_prod'), required=True)
    parser.add_argument('--profile', default='minerva-agent')
    parser.add_argument('--approvals')
    args = parser.parse_args(argv)
    package = prepare(args.snapshot, args.output, args.database, args.approvals)
    if args.operation == 'convert':
        malformed = sum(row['classification'] == 'Malformed' for row in package['rows'])
        print(f'Prepared SQL and frozen archive; malformed rows: {malformed}. No database writes.')
        return 2 if malformed else 0
    actual = persisted(database or ReadOnlyDataApi(args.database, args.profile), package['snapshotId'])
    baseline_path = Path(args.output) / 'verified-baseline.json'
    baseline = loads(baseline_path.read_bytes()) if baseline_path.exists() else None
    if baseline and baseline['snapshotId'] != package['snapshotId']:
        raise ValueError('Existing verification baseline names another snapshot')
    report = compare(package, actual, baseline)
    if report['readyForCutover'] and baseline is None:
        write_private(baseline_path, report)
    write_private(Path(args.output) / 'report.json', report)
    print(f"Read-only comparison complete; readiness={report['readyForCutover']}, exceptions={len(report['exceptions'])}")
    return 0 if report['readyForCutover'] else 2


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ValueError, RuntimeError, KeyError) as error:
        print(f'Blocked: {error}')
        raise SystemExit(1)


