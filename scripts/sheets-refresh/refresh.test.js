'use strict';
function runtimeFixture(rows = [expense(2), expense(1)]) {
  const properties = { SPREADSHEET_ID: 'spreadsheet', GUITO_BASE_URL: 'https://staging.example.invalid',
    DATA_SHEET_ID: '10', BACKUP_SHEET_ID: '11', STATUS_SHEET_ID: '12' };
  const grids = { 10: [['last good']], 11: [['older']], 12: [['OK', 'previous success', '1', 'old']] };
  const roles = ['DATA', 'BACKUP', 'STATUS'];
  const sheets = roles.map((role, index) => ({ properties: { sheetId: 10 + index, title: 'Guito ' + role,
    gridProperties: { rowCount: 1000, columnCount: 9 } }, developerMetadata: [{ metadataKey: 'guito-loader',
    metadataValue: 'script:' + role, location: { sheetId: 10 + index } }] }));
  const calls = []; const triggers = []; let locked = false;
  const response = (code, body) => ({ getResponseCode: () => code, getContentText: () => JSON.stringify(body) });
  const state = { calls, grids, properties, sheets, triggers, locked: false, reject: false, timeout: false,
    applyBeforeTimeout: false, failureStatus: false };
  function apply(requests) {
    for (const request of requests) {
      if (request.updateCells) {
        const update = request.updateCells;
        const id = update.range.sheetId;
        const values = update.rows.map(row => row.values.map(cell => cell.userEnteredValue.stringValue));
        if (update.range.startColumnIndex === 0 && update.range.endColumnIndex === 1) grids[id][0][0] = values[0][0];
        else grids[id] = values;
      }
      if (request.copyPaste) grids[request.copyPaste.destination.sheetId] = structuredClone(grids[request.copyPaste.source.sheetId]);
      if (request.addSheet) { sheets.push({ properties: request.addSheet.properties, developerMetadata: [] }); grids[request.addSheet.properties.sheetId] = []; }
      if (request.createDeveloperMetadata) {
        const metadata = request.createDeveloperMetadata.developerMetadata;
        sheets.find(sheet => sheet.properties.sheetId === metadata.location.sheetId).developerMetadata.push(metadata);
      }
      if (request.updateSheetProperties) Object.assign(sheets.find(sheet => sheet.properties.sheetId === request.updateSheetProperties.properties.sheetId).properties.gridProperties, request.updateSheetProperties.properties.gridProperties);
    }
  }
  const globals = {
    PropertiesService: { getScriptProperties: () => ({ getProperties: () => ({ ...properties }),
      setProperties: values => Object.assign(properties, values) }) },
    LockService: { getScriptLock: () => ({ tryLock: () => { if (state.locked || locked) return false; locked = true; return true; }, releaseLock: () => { locked = false; } }) },
    Utilities: { getUuid: () => 'new-marker' },
    ScriptApp: { getIdentityToken: () => 'ephemeral-id-token', getOAuthToken: () => 'google-only-access-token',
      getScriptId: () => 'script', EventType: { ON_OPEN: 'open' },
      getProjectTriggers: () => triggers, deleteTrigger: trigger => triggers.splice(triggers.indexOf(trigger), 1),
      newTrigger: handler => ({ forSpreadsheet: id => ({ onOpen: () => ({ create: () => {
        const trigger = { getHandlerFunction: () => handler, getTriggerSourceId: () => id, getEventType: () => 'open' };
        triggers.push(trigger); return trigger;
      } }) }) }) },
    UrlFetchApp: { fetch(url, options) {
      calls.push({ url, options });
      if (url.startsWith(properties.GUITO_BASE_URL)) {
        if (state.fetchError) throw new Error('provider secret payload');
        if (state.authError) return response(401, { secret: 'never expose' });
        const index = Number(new URL(url).searchParams.get('pageIndex'));
        const page = { pageSize: 100, pageIndex: index, totalCount: rows.length,
          hasMore: (index + 1) * 100 < rows.length, expenses: rows.slice(index * 100, (index + 1) * 100) };
        if (state.pageChange) state.pageChange(page);
        return response(200, page);
      }
      if (url.includes(':batchUpdate')) {
        const requests = JSON.parse(options.payload).requests;
        const isCommit = requests.some(request => request.copyPaste);
        if (isCommit && state.reject) return response(400, { secret: 'never expose' });
        if (!isCommit && state.failureStatus) throw new Error('status secret');
        if (isCommit && state.timeout) {
          if (state.applyBeforeTimeout) apply(requests);
          throw new Error('timeout with provider secret');
        }
        apply(requests); return response(200, {});
      }
      if (url.includes('/values/')) return response(200, { values: grids[12] });
      return response(200, { sheets });
    } }
  };
  return { context: loader(globals), state };
}

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
function loader(globals = {}) {
  const context = vm.createContext({ console, ...globals });
  vm.runInContext(fs.readFileSync(path.join(__dirname, 'Code.js'), 'utf8'), context);
  return context;
}
function expense(id, overrides = {}) {
  return { id, amount: '-123456789012345678901234567890.000001', currency: 'EUR',
    bookingDate: '2026-07-02T00:30:00.123456+01:00', category: 'Food',
    description: '=IMPORTXML("unsafe")', creatorEmail: 'owner@example.invalid',
    creationDate: '2026-07-02T00:31:00+01:00', updateDate: null, ...overrides };
}
function fixture(rows = [expense(2), expense(1)]) {
  let marker = 'old'; let data = [['last good']]; let backup = data;
  let pending = null; let error = ''; let lastSuccess = 'previous success';
  const calls = []; let locked = false;
  const deps = {
    lock: { acquire() { if (locked) return false; locked = true; return true; }, release() { locked = false; } },
    now: () => '2026-10-12T10:00:00+01:00', token: () => 'new-marker',
    pending: { get: () => pending, set: value => { pending = value; }, clear: () => { pending = null; } },
    api: { page(index) { calls.push(index); return { pageSize: 100, pageIndex: index,
      totalCount: rows.length, hasMore: (index + 1) * 100 < rows.length,
      expenses: rows.slice(index * 100, (index + 1) * 100) }; } },
    sheet: { prepare() {}, readMarker: () => marker,
      commit(next, status) { data = next; backup = next; marker = status.marker; lastSuccess = status.lastSuccess; error = ''; },
      failure(message) { error = message; } },
    triggers: { ensure() { calls.push('trigger'); } }
  };
  return { deps, calls, state: () => ({ marker, data, backup, pending, error, lastSuccess }) };
}
test('runtime refresh separates credentials and atomically backs up, publishes literal text and status', () => {
  const f = runtimeFixture();
  assert.equal(f.context.refreshExpenses().ok, true);
  const api = f.state.calls.find(call => call.url.includes('/Expense/history'));
  assert.equal(api.options.headers.Authorization, 'Bearer ephemeral-id-token');
  assert.equal(api.options.headers['x-google-idtoken'], 'ephemeral-id-token');
  const writes = f.state.calls.filter(call => call.url.includes(':batchUpdate'));
  assert.equal(writes.length, 1);
  assert.equal(writes[0].options.headers.Authorization, 'Bearer google-only-access-token');
  const requests = JSON.parse(writes[0].options.payload).requests;
  assert.ok(requests.some(request => request.copyPaste));
  assert.equal(f.state.grids[10][1][1], '-123456789012345678901234567890.000001');
  assert.equal(f.state.grids[10][1][5], '=IMPORTXML("unsafe")');
  assert.deepEqual(f.state.grids[11], [['last good']]);
  assert.equal(f.state.grids[12][0][3], 'new-marker');
  assert.ok(!writes[0].options.payload.includes('formulaValue'));
  assert.ok(!JSON.stringify(f.state.properties).includes('token'));
});
test('ambiguous applied batch is confirmed by marker without retry or failure overwrite', () => {
  const f = runtimeFixture();
  f.state.timeout = true; f.state.applyBeforeTimeout = true;
  assert.equal(f.context.refreshExpenses().ok, true);
  assert.equal(f.state.calls.filter(call => call.url.includes(':batchUpdate')).length, 1);
  assert.equal(f.state.grids[12][0][0], 'OK');
  assert.ok(f.state.calls.some(call => call.url.includes('/values/')));
});
test('unconfirmed timeout preserves pending marker and blocks another refresh without any writes', () => {
  const f = runtimeFixture(); f.state.timeout = true;
  const result = f.context.refreshExpenses();
  assert.equal(result.ok, false);
  assert.match(result.error, /unknown/i);
  assert.deepEqual(f.state.grids[10], [['last good']]);
  assert.equal(f.state.grids[12][0][0], 'OK');
  assert.equal(f.state.properties.PENDING_COMMIT_MARKER, 'new-marker');
  f.state.calls.length = 0;
  assert.equal(f.context.refreshExpenses().ok, false);
  assert.ok(!f.state.calls.some(call => call.url.includes(':batchUpdate') || call.url.includes('/Expense/history')));
});
test('install authenticates before creating owned tabs and enables exactly one own open trigger after refresh', () => {
  const f = runtimeFixture();
  delete f.state.properties.DATA_SHEET_ID; delete f.state.properties.BACKUP_SHEET_ID; delete f.state.properties.STATUS_SHEET_ID;
  f.state.sheets.splice(0, 3, { properties: { sheetId: 1, title: 'Original archive', gridProperties: { rowCount: 1000, columnCount: 9 } } });
  assert.equal(f.context.installExpenses().ok, true);
  assert.equal(f.state.triggers.length, 1);
  assert.equal(f.state.sheets[0].properties.title, 'Original archive');
  assert.equal(f.state.sheets.length, 4);
  assert.equal(f.context.installExpenses().ok, true);
  assert.equal(f.state.triggers.length, 1);
  const failed = runtimeFixture(); failed.state.authError = true;
  assert.equal(failed.context.installExpenses().ok, false);
  assert.equal(failed.state.triggers.length, 0);
  assert.ok(!failed.state.calls.some(call => call.url.includes(':batchUpdate')));
});
test('owner recovery atomically restores prior dataset without contacting Guito', () => {
  const f = runtimeFixture();
  assert.equal(f.context.refreshExpenses().ok, true);
  f.state.calls.length = 0;
  assert.equal(f.context.restorePreviousExpenses().ok, true);
  assert.deepEqual(f.state.grids[10], [['last good']]);
  assert.ok(!f.state.calls.some(call => call.url.includes('/Expense/history')));
  assert.equal(f.state.calls.filter(call => call.url.includes(':batchUpdate')).length, 1);
  assert.equal(f.state.grids[12][0][0], 'RESTORED BACKUP');
  assert.equal(f.state.grids[12][0][1], '');
});
test('rejected atomic write preserves data and last success, even if failure-status write also fails', () => {
  for (const failureStatus of [false, true]) {
    const f = runtimeFixture(); f.state.reject = true; f.state.failureStatus = failureStatus;
    const result = f.context.refreshExpenses();
    assert.equal(result.ok, false); assert.match(result.error, /write rejected/i);
    assert.deepEqual(f.state.grids[10], [['last good']]);
    assert.deepEqual(f.state.grids[11], [['older']]);
    assert.equal(f.state.grids[12][0][1], 'previous success');
    assert.equal(f.state.grids[12][0][3], 'old');
    assert.ok(!result.error.includes('secret'));
    f.state.reject = false; f.state.failureStatus = false;
    assert.equal(f.context.refreshExpenses().ok, true);
  }
});
test('concurrent loser performs neither network requests nor status or property writes', () => {
  const f = runtimeFixture(); f.state.locked = true;
  const before = JSON.stringify(f.state.properties);
  assert.equal(f.context.refreshExpenses().ok, false);
  assert.equal(f.state.calls.length, 0);
  assert.equal(JSON.stringify(f.state.properties), before);
});
test('runtime validates every page before replacing and handles empty history as header only', () => {
  const rows = Array.from({ length: 101 }, (_, i) => expense(101 - i));
  const mutations = [page => { if (page.pageIndex) page.totalCount++; },
    page => { if (page.pageIndex) page.expenses = []; }, page => { page.hasMore = 'true'; },
    page => { page.pageSize = 99; }, page => { page.pageIndex++; }];
  for (const mutate of mutations) {
    const f = runtimeFixture(rows); f.state.pageChange = mutate;
    assert.equal(f.context.refreshExpenses().ok, false);
    assert.deepEqual(f.state.grids[10], [['last good']]);
    assert.ok(!f.state.calls.some(call => call.url.includes(':batchUpdate') && JSON.parse(call.options.payload).requests.some(request => request.copyPaste)));
  }
  const empty = runtimeFixture([]);
  assert.equal(empty.context.refreshExpenses().count, 0);
  assert.equal(empty.state.grids[10].length, 1);
});
test('invalid DTO fields, decimal values and calendar timestamps never replace previous history', () => {
  for (const overrides of [{ amount: 0 }, { amount: 'NaN' }, { amount: '1e2' }, { currency: 'USD' },
    { id: 2147483648 }, { category: 1 }, { creatorEmail: undefined },
    { bookingDate: '2026-02-29T00:00:00Z' }, { bookingDate: '2026-07-02T24:00:00+01:00' },
    { creationDate: 'not a date' }, { updateDate: '2026-01-01' }]) {
    const f = runtimeFixture([expense(2, overrides)]);
    assert.equal(f.context.refreshExpenses().ok, false);
    assert.deepEqual(f.state.grids[10], [['last good']]);
  }
  const zero = runtimeFixture([expense(2, { amount: '0.000000', bookingDate: '2026-07-01T23:30:00.123456Z' }), expense(1)]);
  assert.equal(zero.context.refreshExpenses().ok, true);
  assert.equal(zero.state.grids[10][1][1], '0.000000');
});
test('ownership checks and title collisions prevent clobbering unrelated sheets', () => {
  const f = runtimeFixture(); f.state.sheets[0].developerMetadata = [];
  assert.equal(f.context.refreshExpenses().ok, false);
  assert.ok(!f.state.calls.some(call => call.url.includes(':batchUpdate')));
  const collision = runtimeFixture();
  collision.state.sheets.forEach(sheet => { sheet.developerMetadata = []; });
  collision.state.sheets[0].properties.title = 'Guito Expenses';
  assert.equal(collision.context.installExpenses().ok, false);
  assert.ok(!collision.state.calls.some(call => call.url.includes(':batchUpdate')));
});
test('authentication and transport failures return sanitized status and do not leak provider errors', () => {
  for (const flag of ['authError', 'fetchError']) {
    const f = runtimeFixture(); f.state[flag] = true;
    const result = f.context.refreshExpenses();
    assert.equal(result.ok, false); assert.ok(!result.error.includes('secret'));
    assert.deepEqual(f.state.grids[10], [['last good']]);
    assert.equal(f.state.grids[12][0][1], 'previous success');
  }
});
test('claim discovery prints only public aud, sub and email_verified from a newly minted token', () => {
  const f = runtimeFixture(); const logged = [];
  const claims = { aud: 'discovered-client', sub: 'discovered-subject', email_verified: true,
    email: 'private@example.invalid', other: 'never print' };
  const encoded = Buffer.from(JSON.stringify(claims)).toString('base64url');
  f.context.ScriptApp.getIdentityToken = () => 'header.' + encoded + '.signature';
  f.context.Utilities.base64DecodeWebSafe = text => Buffer.from(text, 'base64url');
  f.context.Utilities.newBlob = bytes => ({ getDataAsString: () => bytes.toString('utf8') });
  f.context.console = { log: text => logged.push(text) };
  const result = f.context.discoverReportingIdentity();
  assert.deepEqual(JSON.parse(JSON.stringify(result)), { aud: claims.aud, sub: claims.sub, email_verified: true });
  assert.deepEqual(JSON.parse(logged[0]), { aud: claims.aud, sub: claims.sub, email_verified: true });
  assert.equal(f.state.calls.length, 0);
  assert.ok(!JSON.stringify(f.state.properties).includes('discovered'));
});
test('trigger installation failure reports completed publication without exposing raw provider errors', () => {
  const f = runtimeFixture();
  f.context.ScriptApp.newTrigger = () => { throw new Error('trigger secret'); };
  const result = f.context.installExpenses();
  assert.equal(result.ok, false);
  assert.equal(result.publicationSucceeded, true);
  assert.match(result.error, /refreshed.*trigger/i);
  assert.ok(!result.error.includes('secret'));
  assert.equal(f.state.grids[12][0][0], 'OK');
});
test('aliased sheet ownership never permits failure status to overwrite the data tab', () => {
  const f = runtimeFixture();
  f.state.properties.STATUS_SHEET_ID = '10';
  f.state.sheets[0].developerMetadata.push({ metadataKey: 'guito-loader', metadataValue: 'script:STATUS', location: { sheetId: 10 } });
  assert.equal(f.context.refreshExpenses().ok, false);
  assert.deepEqual(f.state.grids[10], [['last good']]);
  assert.ok(!f.state.calls.some(call => call.url.includes(':batchUpdate')));
});
test('manifest declares explicit identity, Sheets and trigger scopes without an advanced service', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, 'appsscript.json'), 'utf8'));
  assert.equal(manifest.runtimeVersion, 'V8');
  assert.deepEqual(manifest.oauthScopes, ['openid', 'https://www.googleapis.com/auth/userinfo.email',
    'https://www.googleapis.com/auth/script.external_request', 'https://www.googleapis.com/auth/spreadsheets',
    'https://www.googleapis.com/auth/script.scriptapp']);
  assert.ok(!manifest.dependencies);
});
test('real runtime retrieves all pages with redirects disabled and drops stale tails in the atomic range', () => {
  const rows = Array.from({ length: 101 }, (_, i) => expense(101 - i));
  const f = runtimeFixture(rows);
  f.state.grids[10] = Array.from({ length: 500 }, () => ['old']);
  assert.equal(f.context.refreshExpenses().count, 101);
  const reads = f.state.calls.filter(call => call.url.includes('/Expense/history'));
  assert.equal(reads.length, 2);
  assert.ok(reads[1].url.endsWith('pageIndex=1'));
  for (const call of f.state.calls) assert.equal(call.options.followRedirects, false);
  const requests = JSON.parse(f.state.calls.find(call => call.url.includes(':batchUpdate')).options.payload).requests;
  const update = requests.find(request => request.updateCells && request.updateCells.range.sheetId === 10).updateCells;
  assert.equal(update.range.endRowIndex, 1000);
  assert.equal(update.rows.length, 102);
  assert.equal(update.fields, 'userEnteredValue');
  assert.equal(f.state.grids[10].length, 102);
});
test('missing runtime identity fails without sending the Google access token to Guito', () => {
  const f = runtimeFixture(); f.context.ScriptApp.getIdentityToken = () => null;
  assert.equal(f.context.refreshExpenses().ok, false);
  assert.ok(!f.state.calls.some(call => call.url.includes('/Expense/history')));
  assert.deepEqual(f.state.grids[10], [['last good']]);
});
test('complete owned metadata recovers missing public IDs without creating duplicate tabs', () => {
  const f = runtimeFixture();
  delete f.state.properties.DATA_SHEET_ID; delete f.state.properties.BACKUP_SHEET_ID; delete f.state.properties.STATUS_SHEET_ID;
  assert.equal(f.context.installExpenses().ok, true);
  assert.equal(f.state.sheets.length, 3);
  assert.equal(f.state.properties.DATA_SHEET_ID, '10');
  assert.ok(!f.state.calls.some(call => call.url.includes(':batchUpdate') && JSON.parse(call.options.payload).requests.some(request => request.addSheet)));
});
test('confirmed publication remains successful if clearing public pending metadata fails', () => {
  const f = runtimeFixture();
  const getProperties = f.context.PropertiesService.getScriptProperties;
  f.context.PropertiesService.getScriptProperties = () => {
    const properties = getProperties(); const set = properties.setProperties;
    properties.setProperties = values => {
      if (values.PENDING_COMMIT_MARKER === '') throw new Error('property provider secret');
      return set(values);
    };
    return properties;
  };
  assert.equal(f.context.refreshExpenses().ok, true);
  assert.equal(f.state.grids[12][0][0], 'OK');
  assert.equal(f.state.properties.PENDING_COMMIT_MARKER, 'new-marker');
});
test('explicit reinstallation cannot bypass an unresolved publication marker', () => {
  const f = runtimeFixture(); f.state.timeout = true;
  assert.equal(f.context.refreshExpenses().ok, false);
  f.state.calls.length = 0;
  assert.equal(f.context.installExpenses().ok, false);
  assert.ok(!f.state.calls.some(call => call.url.includes('/Expense/history') || call.url.includes(':batchUpdate')));
});
test('duplicate IDs and sub-millisecond ascending occurrence reject the entire refresh', () => {
  for (const rows of [[expense(2), expense(2)], [expense(2, { bookingDate: '2026-07-02T00:30:00.123455+01:00' }), expense(1)]]) {
    const f = fixture(rows);
    assert.equal(loader().refreshExpenses(f.deps).ok, false);
    assert.deepEqual(f.state().data, [['last good']]);
  }
});
test('all live pages are validated before a single replacement; truncated last page preserves data', () => {
  const rows = Array.from({ length: 101 }, (_, i) => expense(101 - i));
  const f = fixture(rows);
  assert.equal(loader().refreshExpenses(f.deps).count, 101);
  assert.deepEqual(f.calls, [0, 1]);
  const broken = fixture(rows);
  const page = broken.deps.api.page;
  broken.deps.api.page = index => { const result = page(index); if (index === 1) result.expenses = []; return result; };
  assert.equal(loader().refreshExpenses(broken.deps).ok, false);
  assert.deepEqual(broken.state().data, [['last good']]);
  assert.equal(broken.state().lastSuccess, 'previous success');
  assert.match(broken.state().error, /validation/i);
});
test('refresh entry point publishes complete history as exact text and last success', () => {
  const f = fixture();
  const result = loader().refreshExpenses(f.deps);
  assert.equal(result.ok, true);
  assert.deepEqual(f.calls, [0]);
  assert.deepEqual(Array.from(f.state().data[1]), ['2', '-123456789012345678901234567890.000001', 'EUR',
    '2026-07-02T00:30:00.123456+01:00', 'Food', '=IMPORTXML("unsafe")', 'owner@example.invalid',
    '2026-07-02T00:31:00+01:00', '']);
  assert.equal(f.state().lastSuccess, '2026-10-12T10:00:00+01:00');
});
