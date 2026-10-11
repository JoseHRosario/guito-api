#!/usr/bin/env python3
"""Read-only bounded Sheets capture. No source edits or database operations."""
import argparse
import base64
import importlib.util
import json
import os
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path

import requests
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('history', ROOT / 'scripts/import-history.py')
assert SPEC and SPEC.loader
history = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(history)


def aws(profile, *arguments):
    return json.loads(subprocess.check_output(['aws', '--profile', profile, '--region', 'eu-west-1', *arguments], stderr=subprocess.PIPE))


def capture(environment, output, convention, separator, profile):
    os.umask(0o077)
    identity = aws(profile, 'sts', 'get-caller-identity')['Arn']
    if not identity.startswith('arn:aws:sts::497087877832:assumed-role/MinervaAIAgent/'):
        raise ValueError('Read-only capture requires MinervaAIAgent')
    filename = 'appsettings.Production.json' if environment == 'prod' else 'appsettings.Staging.json'
    source = json.loads((ROOT / 'src/guito-api' / filename).read_text())['AppConfiguration']['Googlesheets']
    payload = json.loads(aws(profile, 'ssm', 'get-parameter', '--name', f'/guito-api/{environment}', '--with-decryption')['Parameter']['Value'])
    key = payload['GoogleServiceAccount']
    key = json.loads(key) if isinstance(key, str) else key
    encode = lambda value: base64.urlsafe_b64encode(value).rstrip(b'=')
    now = int(time.time())
    claims = {'iss': key['client_email'], 'scope': 'https://www.googleapis.com/auth/spreadsheets.readonly',
              'aud': 'https://oauth2.googleapis.com/token', 'iat': now, 'exp': now + 3600}
    message = encode(json.dumps({'alg': 'RS256', 'typ': 'JWT'}).encode()) + b'.' + encode(json.dumps(claims).encode())
    private_key = serialization.load_pem_private_key(key['private_key'].encode(), password=None)
    signed = message + b'.' + encode(private_key.sign(message, padding.PKCS1v15(), hashes.SHA256()))
    response = requests.post('https://oauth2.googleapis.com/token', data={'grant_type': 'urn:ietf:params:oauth:grant-type:jwt-bearer', 'assertion': signed.decode()}, timeout=60)
    response.raise_for_status()
    with requests.Session() as session:
        session.headers['Authorization'] = 'Bearer ' + response.json()['access_token']
        url = 'https://sheets.googleapis.com/v4/spreadsheets/' + source['SpreadsheetId']
        metadata = session.get(url, params={'fields': 'sheets(properties)'}, timeout=120)
        metadata.raise_for_status()
        tab, anchor = source['ExpensesRange'].split('!')
        first = int(anchor[1:])
        last = next(s['properties']['gridProperties']['rowCount'] for s in metadata.json()['sheets'] if s['properties']['title'] == tab)
        if last - first > 100000:
            raise ValueError('Source grid exceeds supported bound')
        span = f"'{tab}'!B{first}:H{last}"
        def read(render):
            result = session.get(url + '/values/' + requests.utils.quote(span, safe=''),
                params={'valueRenderOption': render, 'dateTimeRenderOption': 'SERIAL_NUMBER'}, timeout=120)
            result.raise_for_status()
            rows = history.loads(result.text).get('values', [])
            return rows + [[] for _ in range(last - first + 1 - len(rows))]
        formatted, underlying = read('FORMATTED_VALUE'), read('UNFORMATTED_VALUE')
        if formatted != read('FORMATTED_VALUE') or underlying != read('UNFORMATTED_VALUE'):
            raise ValueError('Source changed during capture; pause edits and retry')
    snapshot = {'SpreadsheetId': source['SpreadsheetId'], 'Tab': tab, 'FirstRow': first, 'LastRow': last,
                'Currency': 'EUR', 'DecimalSeparator': separator, 'DateFormat': convention,
                'CapturedAt': datetime.now(timezone.utc).isoformat(), 'Rows': formatted, 'UnformattedRows': underlying}
    path = Path(output)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.exists():
        raise ValueError('Refusing to overwrite an existing frozen snapshot')
    history.write_private(path, snapshot)
    print(f'Read-only capture saved: {last - first + 1} bounded source rows. No source/database writes.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('environment', choices=('staging', 'prod'))
    parser.add_argument('output')
    parser.add_argument('--date-format', required=True, choices=('yyyy-MM-dd', 'M/d/yyyy', 'MM/dd/yyyy', 'd/M/yyyy', 'dd/MM/yyyy'))
    parser.add_argument('--decimal-separator', required=True, choices=('.', ','))
    parser.add_argument('--profile', default='minerva-agent')
    args = parser.parse_args()
    capture(args.environment, args.output, args.date_format, args.decimal_separator, args.profile)
