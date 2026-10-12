'use strict';

function createRuntime() {
  var properties = PropertiesService.getScriptProperties().getProperties();
  if (!/^[A-Za-z0-9_-]+$/.test(properties.SPREADSHEET_ID || '') ||
      !/^https:\/\/[A-Za-z0-9.-]+(?::443)?(?:\/[A-Za-z0-9_-]+)*$/.test(properties.GUITO_BASE_URL || '')) throw new Error('configuration');
  var lock = LockService.getScriptLock();
  var sheet = runtimeSheet(properties);
  return {
    lock: { acquire: function () { return lock.tryLock(1); }, release: function () { lock.releaseLock(); } },
    now: function () { return new Date().toISOString(); }, token: function () { return Utilities.getUuid(); }, sheet: sheet,
    triggers: { ensure: function () { ensureOpenTrigger(properties.SPREADSHEET_ID); } },
    api: { page: function (index) {
      var token = ScriptApp.getIdentityToken();
      if (!token) throw new Error('authentication');
      return readJson(properties.GUITO_BASE_URL + '/Expense/history?pageSize=100&pageIndex=' + index,
        { method: 'get', headers: { Authorization: 'Bearer ' + token, 'x-google-idtoken': token } });
    } }
  };
}

function readJson(url, options) {
  options.muteHttpExceptions = true;
  options.followRedirects = false;
  var response;
  try { response = UrlFetchApp.fetch(url, options); } catch (ignored) { throw new Error('fetch'); }
  var code = response.getResponseCode();
  if (code !== 200) throw new Error(code === 401 || code === 403 ? 'authentication' : 'fetch');
  try { return JSON.parse(response.getContentText()); } catch (ignored) { throw new Error('validation'); }
}

function runtimeSheet(properties) {
  var base = 'https://sheets.googleapis.com/v4/spreadsheets/' + properties.SPREADSHEET_ID;
  var owned, ownershipVerified = false;
  function options(method, payload) {
    var result = { method: method, headers: { Authorization: 'Bearer ' + ScriptApp.getOAuthToken() },
      muteHttpExceptions: true, followRedirects: false };
    if (payload) { result.contentType = 'application/json'; result.payload = JSON.stringify(payload); }
    return result;
  }
  function prepare() {
    Object.assign(properties, PropertiesService.getScriptProperties().getProperties());
    ownershipVerified = false;
    var result = readJson(base + '?fields=sheets(properties,developerMetadata)', options('get'));
    owned = {};
    ['DATA', 'BACKUP', 'STATUS'].forEach(function (role) {
      var id = properties[role + '_SHEET_ID'];
      var matches = (result.sheets || []).filter(function (sheet) {
        return String(sheet.properties.sheetId) === id && (sheet.developerMetadata || []).some(function (metadata) {
          return metadata.metadataKey === 'guito-loader' && metadata.metadataValue === ScriptApp.getScriptId() + ':' + role;
        });
      });
      if (!/^\d+$/.test(id || '') || matches.length !== 1) throw new Error('ownership');
      owned[role] = matches[0].properties;
    });
    if (new Set(Object.keys(owned).map(function (role) { return owned[role].sheetId; })).size !== 3) throw new Error('ownership');
    ownershipVerified = true;
    if (properties.PENDING_COMMIT_MARKER) {
      try { if (readMarker() !== properties.PENDING_COMMIT_MARKER) throw new Error('ambiguous'); }
      catch (ignored) { throw new Error('ambiguous'); }
      pending('');
    }
  }
  function readMarker() {
    var range = "'" + owned.STATUS.title.replace(/'/g, "''") + "'!A1:D1";
    var value = readJson(base + '/values/' + encodeURIComponent(range), options('get'));
    return value.values && value.values[0] && value.values[0][3];
  }
  function batch(requests) {
    var response;
    try { response = UrlFetchApp.fetch(base + ':batchUpdate', options('post', { requests: requests })); }
    catch (ignored) { throw new Error('ambiguous'); }
    var code = response.getResponseCode();
    if (code >= 200 && code < 300) return;
    throw new Error(code >= 500 || code === 408 ? 'ambiguous' : 'write');
  }
  function resize(sheet, rows, columns) {
    return { updateSheetProperties: { properties: { sheetId: sheet.sheetId, gridProperties: {
      rowCount: Math.max(sheet.gridProperties.rowCount, rows), columnCount: Math.max(sheet.gridProperties.columnCount, columns) } },
      fields: 'gridProperties.rowCount,gridProperties.columnCount' } };
  }
  function pending(marker) {
    PropertiesService.getScriptProperties().setProperties({ PENDING_COMMIT_MARKER: marker });
    properties.PENDING_COMMIT_MARKER = marker;
  }
  function commit(rows, status) {
    var count = Math.max(owned.DATA.gridProperties.rowCount, owned.BACKUP.gridProperties.rowCount, rows.length);
    var requests = [resize(owned.DATA, count, 9), resize(owned.BACKUP, count, 9),
      cells(owned.BACKUP.sheetId, [], count, 9),
      { copyPaste: { source: { sheetId: owned.DATA.sheetId, startRowIndex: 0, endRowIndex: owned.DATA.gridProperties.rowCount,
        startColumnIndex: 0, endColumnIndex: 9 }, destination: { sheetId: owned.BACKUP.sheetId, startRowIndex: 0,
        endRowIndex: owned.DATA.gridProperties.rowCount, startColumnIndex: 0, endColumnIndex: 9 }, pasteType: 'PASTE_VALUES' } },
      cells(owned.DATA.sheetId, rows, count, 9),
      cells(owned.STATUS.sheetId, [['OK', status.lastSuccess, String(status.count), status.marker]], 1, 4)];
    confirmedBatch(requests, status.marker);
  }
  function confirmedBatch(requests, marker) {
    pending(marker);
    try { batch(requests); }
    catch (error) {
      if (error.message !== 'ambiguous') { pending(''); throw error; }
      try { if (readMarker() === marker) { clearConfirmedPending(); return; } } catch (ignored) { /* Outcome stays unknown. */ }
      throw new Error('ambiguous');
    }
    clearConfirmedPending();
  }
  function clearConfirmedPending() {
    // Keeping a public pending marker is safe; misreporting a committed batch is not.
    try { pending(''); } catch (ignored) { /* A later prepare reconciles the matching marker. */ }
  }
  function restore(marker) {
    var count = Math.max(owned.DATA.gridProperties.rowCount, owned.BACKUP.gridProperties.rowCount);
    confirmedBatch([resize(owned.DATA, count, 9), cells(owned.DATA.sheetId, [], count, 9),
      { copyPaste: { source: { sheetId: owned.BACKUP.sheetId, startRowIndex: 0, endRowIndex: owned.BACKUP.gridProperties.rowCount,
        startColumnIndex: 0, endColumnIndex: 9 }, destination: { sheetId: owned.DATA.sheetId, startRowIndex: 0,
        endRowIndex: owned.BACKUP.gridProperties.rowCount, startColumnIndex: 0, endColumnIndex: 9 }, pasteType: 'PASTE_VALUES' } },
      cells(owned.STATUS.sheetId, [['RESTORED BACKUP', '', '', marker]], 1, 4)], marker);
  }
  function initialize() {
    var result = readJson(base + '?fields=sheets(properties,developerMetadata)', options('get'));
    var roles = ['DATA', 'BACKUP', 'STATUS'];
    var titles = ['Guito Expenses', 'Guito Previous', 'Guito Refresh Status'];
    var existing = roles.map(function (role) {
      return (result.sheets || []).filter(function (sheet) {
        return (sheet.developerMetadata || []).some(function (metadata) {
          return metadata.metadataKey === 'guito-loader' && metadata.metadataValue === ScriptApp.getScriptId() + ':' + role;
        });
      });
    });
    if (existing.some(function (matches) { return matches.length > 0; })) {
      if (!existing.every(function (matches) { return matches.length === 1; })) throw new Error('ownership');
    } else {
      if ((result.sheets || []).some(function (sheet) { return titles.indexOf(sheet.properties.title) >= 0; })) throw new Error('ownership');
      var id = Math.max.apply(null, [0].concat((result.sheets || []).map(function (sheet) { return sheet.properties.sheetId; }))) + 1;
      var requests = [];
      roles.forEach(function (role, index) {
        var tab = { sheetId: id + index, title: titles[index], gridProperties: { rowCount: 1000, columnCount: 9 } };
        existing[index] = [{ properties: tab }];
        requests.push({ addSheet: { properties: tab } });
        requests.push({ createDeveloperMetadata: { developerMetadata: { metadataKey: 'guito-loader',
          metadataValue: ScriptApp.getScriptId() + ':' + role, location: { sheetId: id + index }, visibility: 'DOCUMENT' } } });
      });
      batch(requests);
    }
    var ids = {};
    roles.forEach(function (role, index) { ids[role + '_SHEET_ID'] = String(existing[index][0].properties.sheetId); });
    PropertiesService.getScriptProperties().setProperties(ids);
    Object.assign(properties, ids);
  }
  return { prepare: prepare, initialize: initialize, restore: restore, commit: commit, readMarker: readMarker,
    guardPending: function () { if (PropertiesService.getScriptProperties().getProperties().PENDING_COMMIT_MARKER) prepare(); },
    failure: function (message) { if (ownershipVerified) batch([cells(owned.STATUS.sheetId, [[message]], 1, 1)]); } };
}

// An updateCells range clears leftover userEnteredValue cells not covered by rows.
// Explicit stringValue preserves decimal text and never interprets formula prefixes.
function cells(id, rows, rowCount, columnCount) {
  return { updateCells: { range: { sheetId: id, startRowIndex: 0, endRowIndex: rowCount,
    startColumnIndex: 0, endColumnIndex: columnCount }, fields: 'userEnteredValue',
    rows: rows.map(function (row) { return { values: row.map(function (value) {
      return { userEnteredValue: { stringValue: value } };
    }) }; }) } };
}


var EXPENSE_COLUMNS = ['id', 'amount', 'currency', 'bookingDate', 'category', 'description',
  'creatorEmail', 'creationDate', 'updateDate'];

/** Manual refresh and authorized installable open-trigger entry point. */
function refreshExpenses(adapters, installing) {
  var deps;
  try { deps = adapters && adapters.api ? adapters : createRuntime(); }
  catch (ignored) { return { ok: false, error: 'Loader configuration unavailable; no data written.' }; }
  if (!deps.lock.acquire()) return { ok: false, error: 'Refresh already running; try again later.' };
  try {
    if (installing && deps.sheet.guardPending) deps.sheet.guardPending();
    if (!installing && deps.sheet.prepare) deps.sheet.prepare();
    var expenses = fetchHistory(deps.api);
    var rows = [EXPENSE_COLUMNS.slice()].concat(expenses.map(function (expense) {
      return EXPENSE_COLUMNS.map(function (column) { return expense[column] == null ? '' : String(expense[column]); });
    }));
    if (installing) { deps.sheet.initialize(); deps.sheet.prepare(); }
    deps.sheet.commit(rows, { marker: deps.token(), lastSuccess: deps.now(), count: expenses.length });
    if (installing) {
      try { deps.triggers.ensure(); }
      catch (ignored) { return { ok: false, publicationSucceeded: true, count: expenses.length,
        error: 'Data refreshed; open trigger installation failed. Retry installation as the same owner.' }; }
    }
    return { ok: true, count: expenses.length };
  } catch (error) {
    var unknown = error.message === 'ambiguous';
    var message = unknown ? 'Sheets commit outcome unknown; inspect marker before retrying.' :
      error.message === 'write' ? 'Sheets write rejected; previous data retained.' :
      error.message === 'authentication' ? 'Reporting authentication failed; previous data retained.' :
      'History validation or fetch failed; previous data retained.';
    if (!unknown && !installing) { try { deps.sheet.failure(message); } catch (ignored) { /* Keep original sanitized outcome. */ } }
    return { ok: false, error: message };
  } finally { deps.lock.release(); }
}

/** Explicit, owner-run installation only; never called from a spreadsheet event. */
function installExpenses() { return refreshExpenses(null, true); }

/** Owner-only recovery; disable the open trigger and inspect the backup first. */
function restorePreviousExpenses() {
  var deps;
  try { deps = createRuntime(); } catch (ignored) { return { ok: false, error: 'Loader configuration unavailable.' }; }
  if (!deps.lock.acquire()) return { ok: false, error: 'Refresh already running; try again later.' };
  try { deps.sheet.prepare(); deps.sheet.restore(deps.token()); return { ok: true }; }
  catch (error) { return { ok: false, error: error.message === 'ambiguous' ?
    'Sheets commit outcome unknown; inspect marker before retrying.' : 'Backup restore failed; no successful restore confirmed.' }; }
  finally { deps.lock.release(); }
}

/** Discovery only, not token verification. Never log the JWT or other claims. */
function discoverReportingIdentity() {
  var claims;
  try {
    var token = ScriptApp.getIdentityToken();
    if (!token) throw new Error('authentication');
    claims = JSON.parse(Utilities.newBlob(Utilities.base64DecodeWebSafe(token.split('.')[1])).getDataAsString());
    if (typeof claims.aud !== 'string' || typeof claims.sub !== 'string' || claims.email_verified !== true) throw new Error('claims');
  } catch (ignored) { throw new Error('Reporting identity unavailable; grant openid and email scopes as the installer.'); }
  var publicClaims = { aud: claims.aud, sub: claims.sub, email_verified: claims.email_verified };
  console.log(JSON.stringify(publicClaims));
  return publicClaims;
}

function ensureOpenTrigger(spreadsheetId) {
  function matching() {
    return ScriptApp.getProjectTriggers().filter(function (trigger) {
      return trigger.getHandlerFunction() === 'refreshExpenses' && trigger.getTriggerSourceId() === spreadsheetId &&
        trigger.getEventType() === ScriptApp.EventType.ON_OPEN;
    });
  }
  var triggers = matching();
  triggers.slice(1).forEach(function (trigger) { ScriptApp.deleteTrigger(trigger); });
  if (!triggers.length) ScriptApp.newTrigger('refreshExpenses').forSpreadsheet(spreadsheetId).onOpen().create();
  if (matching().length !== 1) throw new Error('trigger');
}

function fetchHistory(api) {
  var expenses = [], total = null, index = 0;
  do {
    var page = api.page(index);
    if (!page || page.pageSize !== 100 || page.pageIndex !== index ||
        !Number.isSafeInteger(page.totalCount) || page.totalCount < 0 ||
        (total !== null && page.totalCount !== total) || !Array.isArray(page.expenses)) throw new Error('validation');
    total = page.totalCount;
    var expected = Math.min(100, Math.max(0, total - index * 100));
    if (page.expenses.length !== expected || page.hasMore !== ((index + 1) * 100 < total)) throw new Error('validation');
    expenses = expenses.concat(page.expenses);
    index++;
  } while (page.hasMore);
  if (expenses.length !== total) throw new Error('validation');
  var seen = Object.create(null), previous = null;
  expenses.forEach(function (expense) {
    if (!expense || !Number.isInteger(expense.id) || expense.id < -2147483648 || expense.id > 2147483647 || seen[expense.id]) throw new Error('validation');
    seen[expense.id] = true;
    if (typeof expense.amount !== 'string' || !/^[+-]?\d+(?:\.\d+)?$/.test(expense.amount) || expense.currency !== 'EUR') throw new Error('validation');
    ['description', 'category', 'creatorEmail'].forEach(function (key) {
      if (!(key in expense) || (expense[key] !== null && typeof expense[key] !== 'string')) throw new Error('validation');
    });
    ['creationDate', 'updateDate'].forEach(function (key) {
      if (!(key in expense)) throw new Error('validation');
      if (expense[key] !== null) timestampKey(expense[key]);
    });
    var instant = timestampKey(expense.bookingDate);
    if (previous && (instant[0] > previous.instant[0] ||
        (instant[0] === previous.instant[0] && (instant[1] > previous.instant[1] ||
        (instant[1] === previous.instant[1] && expense.id >= previous.id))))) throw new Error('validation');
    previous = { instant: instant, id: expense.id };
  });
  return expenses;
}

// Compare whole UTC seconds and padded fractional text separately: Date loses microseconds.
function timestampKey(value) {
  var match = typeof value === 'string' && /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|([+-])(\d{2}):(\d{2}))$/.exec(value);
  if (!match) throw new Error('validation');
  var year = Number(match[1]), month = Number(match[2]), day = Number(match[3]);
  var hour = Number(match[4]), minute = Number(match[5]), second = Number(match[6]);
  var offsetHour = Number(match[10] || 0), offsetMinute = Number(match[11] || 0);
  if (year < 1 || month < 1 || month > 12 || day < 1 || hour > 23 || minute > 59 || second > 59 ||
      offsetHour > 14 || offsetMinute > 59 || (offsetHour === 14 && offsetMinute !== 0)) throw new Error('validation');
  var date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(hour, minute, second, 0);
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) throw new Error('validation');
  var offset = (offsetHour * 60 + offsetMinute) * (match[9] === '-' ? -1 : 1);
  return [date.getTime() / 1000 - offset * 60, (match[7] || '').padEnd(7, '0')];
}
