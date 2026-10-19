"""Offline session-SQL retirement and real migration-runner CLI regressions."""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / 'db/migrations/003_categories.sql'

FAKE_AWS = r'''#!/usr/bin/env python3
import json, os, re, sys
from pathlib import Path
path = Path(os.environ['FAKE_STATE'])
s = json.loads(path.read_text())
a = sys.argv[1:]
if 'describe-secret' in a:
    print('fake-secret-arn'); sys.exit(0)
assert 'execute-statement' in a, a
sql = a[a.index('--sql') + 1]
s.setdefault('calls', []).append(sql)
if sql == 'SELECT name FROM _migrations':
    print(json.dumps({'records': [[{'stringValue': name}] for name in s['applied']]}))
elif sql.startswith('INSERT INTO _migrations'):
    s['applied'].append(re.search("VALUES \\('([^']+)'\\)", sql).group(1))
    print('{}')
elif s.get('fail') and sql.startswith('CREATE TABLE IF NOT EXISTS categories'):
    path.write_text(json.dumps(s))
    print('fake DDL failure', file=sys.stderr); sys.exit(1)
else:
    print('{}')
path.write_text(json.dumps(s))
'''


class UserSessionMigrationTests(unittest.TestCase):
    def test_feature_should_have_no_session_sql_or_cleanup_runner(self):
        for migration in sorted((ROOT / 'db/migrations').glob('*.sql')):
            self.assertNotRegex(migration.read_text(), r'(?i)\b(?:user_sessions|human_sessions)\b', migration.name)
        solution = (ROOT / 'guito-api.slnx').read_text()
        self.assertNotIn('011_user_sessions.sql', solution)
        self.assertNotIn('011_human_sessions.sql', solution)
        runner = (ROOT / 'deploy/db-apply.sh').read_text()
        self.assertNotRegex(runner, r'(?i)\b(?:user_sessions|human_sessions)\b')
        self.assertNotRegex(runner, r'(?i)\b(?:DROP|TRUNCATE)\s+TABLE\b')

    def run_runner(self, fail=False):
        temporary = tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR', os.environ.get('RUNNER_TEMP')))
        self.addCleanup(temporary.cleanup)
        directory = Path(temporary.name)
        aws = directory / 'aws'
        aws.write_text(FAKE_AWS)
        aws.chmod(0o700)
        state = directory / 'state.json'
        applied = [p.name for p in sorted((ROOT / 'db/migrations').glob('*.sql')) if p != FIXTURE]
        state.write_text(json.dumps({'applied': applied, 'fail': fail}))
        env = dict(os.environ, PATH=str(directory) + os.pathsep + os.environ['PATH'], ENV='staging', FAKE_STATE=str(state),
                   AWS_EC2_METADATA_DISABLED='true')
        result = subprocess.run(['bash', str(ROOT / 'deploy/db-apply.sh')], env=env,
                                capture_output=True, text=True, timeout=15)
        return result, state, env

    def test_runner_should_apply_remaining_fixture_boundaries_and_skip_ledger_on_second_run(self):
        result, state, env = self.run_runner()
        self.assertEqual(0, result.returncode, result.stderr)
        first = json.loads(state.read_text())
        expected = [part.strip() for part in re.sub(r'--[^\n]*', '', FIXTURE.read_text()).split(';') if part.strip()]
        calls = [sql for sql in first['calls'] if sql.startswith(('CREATE TABLE IF NOT EXISTS categories', 'ALTER TABLE bank_transactions'))]
        self.assertEqual(expected, calls)
        self.assertIn(FIXTURE.name, first['applied'])
        result = subprocess.run(['bash', str(ROOT / 'deploy/db-apply.sh')], env=env,
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stderr)
        second = json.loads(state.read_text())
        later = second['calls'][len(first['calls']):]
        self.assertFalse(any(sql.startswith(('CREATE TABLE', 'ALTER TABLE')) for sql in later))
        self.assertEqual(first['applied'], second['applied'])

    def test_runner_should_stop_failed_statement_without_second_statement_or_ledger_write(self):
        result, state, _ = self.run_runner(fail=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('fake DDL failure', result.stderr)
        captured = json.loads(state.read_text())
        self.assertNotIn(FIXTURE.name, captured['applied'])
        self.assertFalse(any(sql.startswith(('ALTER TABLE bank_transactions', 'INSERT INTO _migrations')) for sql in captured['calls']))


if __name__ == '__main__':
    unittest.main()
