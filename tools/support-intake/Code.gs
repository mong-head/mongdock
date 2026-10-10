/**
 * mongdock "문제 신고하기" 받는 창구 (Google Apps Script 웹 앱).
 * 몽독 앱이 JSON 을 POST 하면 지원 주소(mongdock+help@gmail.com)로 메일 한 통을 만든다.
 * mongdock@gmail.com 계정에서 배포: 실행 = 나, 액세스 = 모든 사용자. 앱에는 비밀번호가 들어가지 않는다.
 *
 * 요청 본문 (application/json):
 * { "token": "...", "clientId": "신고마다 새 무작위 번호 (PC 를 잇지 않음)", "kind": "bug|question|idea",
 *   "message": "사용자가 쓴 내용", "contact": "답장 받을 이메일(선택)",
 *   "appVersion": "0.4.2", "lang": "ko|en|ja|zh-Hans|zh-Hant|de|fr|es", "diagnostics": "개인정보를 가린 진단 정보" }
 */

var SUPPORT_TO = 'mongdock+help@gmail.com';
// 앱에 같이 들어가는 값 — 비밀이 아니라 아무나 막 보내는 걸 조금 걸러내는 용도.
var APP_TOKEN = 'mongdock-report-v1';
var MAX_PER_CLIENT_PER_DAY = 5;
var MAX_TOTAL_PER_DAY = 80; // Gmail 일반 계정의 Apps Script 발송 한도(하루 100통) 안쪽
var MAX_MESSAGE = 4000;
var MAX_DIAG = 12000;

function doPost(e) {
  try {
    var body = JSON.parse((e && e.postData && e.postData.contents) || '{}');
    if (body.token !== APP_TOKEN) return reply_(403, 'bad token');
    if (body.type === 'stats') return handleStats_(body);

    var message = String(body.message || '').trim();
    if (message.length < 3) return reply_(400, 'empty message');
    message = message.substring(0, MAX_MESSAGE);
    var diag = String(body.diagnostics || '').substring(0, MAX_DIAG);
    var kind = ({ bug: '버그', question: '질문', idea: '제안' })[body.kind] || '기타';
    var version = String(body.appVersion || '?').substring(0, 40);
    var clientId = String(body.clientId || 'unknown').replace(/[^A-Za-z0-9-]/g, '').substring(0, 64) || 'unknown';
    var contact = String(body.contact || '').trim();
    if (contact && !/^[^\s@<>()"',;]+@[^\s@<>()"',;]+\.[^\s@<>()"',;]+$/.test(contact)) contact = '';
    contact = contact.substring(0, 254);

    // 하루 한도 (번호별 — 앱이 신고마다 새 번호라 사실상 전체 한도만 의미, 앱도 하루 한도를 따로 셈)
    var cache = CacheService.getScriptCache();
    var day = Utilities.formatDate(new Date(), 'Asia/Seoul', 'yyyyMMdd');
    var lock = LockService.getScriptLock();
    lock.waitLock(5000);
    try {
      var kc = 'c:' + day + ':' + clientId, kt = 't:' + day;
      var nc = Number(cache.get(kc) || 0), nt = Number(cache.get(kt) || 0);
      if (nc >= MAX_PER_CLIENT_PER_DAY) return reply_(429, 'too many reports today');
      if (nt >= MAX_TOTAL_PER_DAY) return reply_(429, 'daily limit');
      cache.put(kc, String(nc + 1), 21600);
      cache.put(kt, String(nt + 1), 21600);
    } finally {
      lock.releaseLock();
    }

    var firstLine = message.split(/\r?\n/)[0].substring(0, 60);
    var subject = '[몽독 신고][' + kind + '] ' + firstLine + ' (v' + version + ')';
    var text =
      '종류: ' + kind + '\n' +
      '버전: ' + version + '\n' +
      '답장 받을 주소: ' + (contact || '(없음 — 답장하지 않음)') + '\n' +
      '신고 번호: ' + clientId + '\n' +
      '언어: ' + String(body.lang || 'ko').substring(0, 5) + '\n' +
      '\n── 내용 ──\n' + message + '\n' +
      '\n── 진단 정보 (앱이 개인정보를 가린 뒤 보냄) ──\n' + (diag || '(없음)') + '\n';

    var opts = { name: '몽독 문제 신고' };
    if (contact) opts.replyTo = contact;
    MailApp.sendEmail(SUPPORT_TO, subject, text, opts);
    return reply_(200, 'ok');
  } catch (err) {
    return reply_(500, 'error');
  }
}

function doGet(e) {
  if (e && e.parameter && e.parameter.view === 'stats') return statsPage_();
  return reply_(200, 'mongdock report intake');
}

// ───────────────────────── 사용 통계 (#20) ─────────────────────────
// 앱(Services/UsageStatsService.cs)이 { token, type:"stats", days:[ {하루치}, ... 최대 7개 ] } 를 보냄.
// PC 번호·이름·IP 는 없다(Apps Script 는 IP 를 모름). 아래 STATS_COLUMNS 에 있는 칸만 시트에 남기고 나머지는 버린다.

var MAX_STATS_ROWS_PER_6H = 20000; // 6시간 전체 상한 (막 보내는 것 거르기 — CacheService 는 최대 6시간까지만 기억)
var STATS_SHEET_PROP = 'STATS_SHEET_ID';

// [칸 이름, 검사 함수] — 검사에 걸리면 그 칸은 빈칸
var STATS_COLUMNS = [
  ['date', function (v) { return /^\d{4}-\d{2}-\d{2}$/.test(v) ? v : null; }],
  ['appVersion', function (v) { return /^[0-9A-Za-z.\-]{1,40}$/.test(v) ? v : null; }],
  ['install', oneOf_(['store', 'installer', 'zip'])],
  ['windows', oneOf_(['10', '11'])],
  ['build', function (v) { return /^[0-9.]{1,20}$/.test(v) ? v : null; }],
  ['lang', oneOf_(['ko', 'en', 'ja', 'zh-Hans', 'zh-Hant', 'de', 'fr', 'es'])],
  ['laptop', bool_],
  ['monitors', int_(0, 16)],
  ['scale', int_(50, 500)],
  ['dockAutoHide', bool_],
  ['topBar', bool_],
  ['notifications', oneOf_(['mongdock', 'both', 'windows'])],
  ['calendar', bool_],
  ['searchButton', bool_],
  ['hideTaskbar', bool_],
  ['lightMode', bool_],
  ['errors', int_(0, 1000000)],
  ['folders', int_(0, 200)], // 독 폴더 수 (#24) — 새 칸은 시트 칸이 밀리지 않게 맨 끝에
  ['recycleBin', bool_], // 독 휴지통 보임 (#24-C)
  ['allAppsOpened', int_(0, 10000)], // 앱 모음 판 연 횟수 (#24)
  ['allAppsCustomized', bool_] // 앱 모음 묶음을 직접 바꾼 적 있음
];

function oneOf_(list) { return function (v) { v = String(v); return list.indexOf(v) >= 0 ? v : null; }; }
function bool_(v) { return v === true || v === false ? v : null; }
function int_(min, max) {
  return function (v) { return typeof v === 'number' && Math.floor(v) === v && v >= min && v <= max ? v : null; };
}

function handleStats_(body) {
  var days = Array.isArray(body.days) ? body.days.slice(0, 7) : [];
  var rows = [];
  var received = new Date();
  days.forEach(function (d) {
    if (!d || typeof d !== 'object') return;
    var row = [received];
    STATS_COLUMNS.forEach(function (c) {
      var v = c[1](d[c[0]]);
      if (v === null || v === undefined) v = '';
      // 날짜·버전·빌드는 시트가 날짜/숫자로 바꾸지 않게 글자로 ("1.0" → 1, 시트 시간대로 날짜가 밀리는 것 방지)
      else if (c[0] === 'date' || c[0] === 'appVersion' || c[0] === 'build') v = "'" + v;
      row.push(v);
    });
    if (row[1] === '') return; // 날짜 없는 건 버림
    // 같은 하루치를 다시 보낸 것(앱이 응답 시간 초과로 실패한 뒤 재전송)은 한 번만. nonce 는 시트에 남기지 않음
    var nonce = typeof d.nonce === 'string' && /^[0-9a-f]{32}$/.test(d.nonce) ? d.nonce : null;
    rows.push({ row: row, nonce: nonce });
  });
  if (rows.length === 0) return reply_(400, 'no valid days');

  var lock = LockService.getScriptLock();
  lock.waitLock(10000);
  try {
    var cache = CacheService.getScriptCache();
    var fresh = rows.filter(function (r) { return !r.nonce || !cache.get('n:' + r.nonce); }).map(function (r) { return r.row; });
    if (fresh.length > 0) {
      var key = 's:' + Math.floor(received.getTime() / 21600000); // 6시간 칸
      var n = Number(cache.get(key) || 0);
      if (n + fresh.length > MAX_STATS_ROWS_PER_6H) return reply_(429, 'limit');
      var sheet = statsSheet_();
      sheet.getRange(sheet.getLastRow() + 1, 1, fresh.length, fresh[0].length).setValues(fresh);
      cache.put(key, String(n + fresh.length), 21600);
    }
    rows.forEach(function (r) { if (r.nonce) cache.put('n:' + r.nonce, '1', 21600); });
  } finally {
    lock.releaseLock();
  }
  return reply_(200, 'ok');
}

/** 통계 시트 (처음이면 스크립트 소유자 드라이브에 "mongdock-stats" 를 만들고 id 를 스크립트 속성에 기억). */
// 칸이 새로 생기면(예: folders) 이미 있는 시트 머리줄 끝에 이름을 붙임 — 보고서가 머리줄 이름으로 칸을 찾으므로.
function ensureStatsHeader_(sheet) {
  var want = ['received'].concat(STATS_COLUMNS.map(function (c) { return c[0]; }));
  var lastCol = Math.max(sheet.getLastColumn(), 1);
  var have = sheet.getRange(1, 1, 1, lastCol).getValues()[0];
  if (have.length < want.length || have.slice(0, want.length).join('|') !== want.join('|'))
    if (want.slice(0, have.length).join('|') === have.join('|'))
      sheet.getRange(1, 1, 1, want.length).setValues([want]).setFontWeight('bold');
  return sheet;
}

function statsSheet_() {
  var props = PropertiesService.getScriptProperties();
  var id = props.getProperty(STATS_SHEET_PROP);
  // 한 번 만든 뒤에는 열기 실패(일시적 드라이브 오류 등)에 새로 만들지 않음 → 예외 → 500 → 앱이 나중에 다시 보냄.
  // 시트를 일부러 지웠으면 스크립트 속성 STATS_SHEET_ID 를 지우면 다음 신호 때 새로 만든다.
  if (id) return ensureStatsHeader_(SpreadsheetApp.openById(id).getSheets()[0]);
  var ss = SpreadsheetApp.create('mongdock-stats');
  var sheet = ss.getSheets()[0];
  sheet.setName('stats');
  var header = ['received'].concat(STATS_COLUMNS.map(function (c) { return c[0]; }));
  sheet.getRange(1, 1, 1, header.length).setValues([header]).setFontWeight('bold');
  sheet.setFrozenRows(1);
  props.setProperty(STATS_SHEET_PROP, ss.getId());
  return sheet;
}

/** 재배포 때 편집기에서 한 번 실행: 시트 권한 허용 + 통계 시트 만들기. ("_" 로 끝나는 함수는 실행 목록에 안 보여서 따로 둠) */
function setupStats() {
  Logger.log('mongdock-stats: ' + statsSheet_().getParent().getUrl());
}

/**
 * 본인만 보는 대시보드: .../exec?view=stats
 * "실행 = 나" 웹 앱에서 Session.getActiveUser() 는 소유자가 직접 열 때만 이메일이 나오고, 다른 사람·로그아웃 상태는 빈 문자열 → 거부.
 */
function isOwner_() {
  var active = Session.getActiveUser().getEmail();
  return !!active && active === Session.getEffectiveUser().getEmail();
}

function statsPage_() {
  if (!isOwner_()) {
    return HtmlService.createHtmlOutput('<p style="font-family:sans-serif">권한 없음</p>').setTitle('mongdock');
  }
  var props = PropertiesService.getScriptProperties();
  var id = props.getProperty(STATS_SHEET_PROP);
  var values = [];
  if (id) {
    try { values = SpreadsheetApp.openById(id).getSheets()[0].getDataRange().getValues(); } catch (err) { values = []; }
  }
  var header = values.shift() || [];
  var col = {};
  header.forEach(function (h, i) { col[h] = i; });
  var tz = 'Asia/Seoul';
  var today = new Date();
  function dayStr(offset) { return Utilities.formatDate(new Date(today.getTime() - offset * 86400000), tz, 'yyyy-MM-dd'); }
  function cell(r, name) { return col[name] === undefined ? '' : r[col[name]]; }
  function dateOf(r) {
    var v = cell(r, 'date');
    return v instanceof Date ? Utilities.formatDate(v, tz, 'yyyy-MM-dd') : String(v);
  }

  // 날짜별 신호 수 (최근 30일)
  var perDay = {};
  values.forEach(function (r) { var d = dateOf(r); perDay[d] = (perDay[d] || 0) + 1; });
  var days30 = [];
  for (var i = 29; i >= 0; i--) days30.push(dayStr(i));
  var maxDay = Math.max.apply(null, [1].concat(days30.map(function (d) { return perDay[d] || 0; })));

  // 최근 7일 분포
  var from7 = dayStr(6);
  var recent = values.filter(function (r) { return dateOf(r) >= from7; });
  function dist(name) {
    var m = {};
    recent.forEach(function (r) { var v = String(cell(r, name)); if (v === '') v = '(없음)'; m[v] = (m[v] || 0) + 1; });
    return Object.keys(m).sort(function (a, b) { return m[b] - m[a]; }).map(function (k) { return [k, m[k]]; });
  }
  function onRate(name) {
    var on = recent.filter(function (r) { return cell(r, name) === true; }).length;
    return recent.length ? Math.round(on * 100 / recent.length) + '%' : '-';
  }
  var errSum = 0, errRows = 0;
  recent.forEach(function (r) { var n = Number(cell(r, 'errors')) || 0; errSum += n; if (n > 0) errRows++; });

  var h = [];
  h.push('<style>body{font-family:-apple-system,"Segoe UI","Malgun Gothic",sans-serif;margin:24px;color:#222}' +
    'h1{font-size:20px}h2{font-size:15px;margin-top:28px}table{border-collapse:collapse;font-size:13px}' +
    'td,th{padding:3px 10px;border-bottom:1px solid #eee;text-align:left}.bar{background:#0a66d8;height:10px;border-radius:3px}' +
    '.grid{display:flex;flex-wrap:wrap;gap:28px}.muted{color:#888;font-size:12px}</style>');
  h.push('<h1>mongdock 사용 통계</h1><p class="muted">신호 ' + values.length + '개 · 최근 7일(' + esc_(from7) + '~) ' + recent.length +
    '개 · 신호 1개 = PC 1대의 하루 (PC 를 구분할 수 없어 같은 PC 가 여러 날이면 여러 개)</p>');
  h.push('<h2>날짜별 신호 (최근 30일)</h2><table>');
  days30.forEach(function (d) {
    var n = perDay[d] || 0;
    h.push('<tr><td>' + esc_(d) + '</td><td style="width:320px"><div class="bar" style="width:' + Math.round(n * 300 / maxDay) +
      'px"></div></td><td>' + n + '</td></tr>');
  });
  h.push('</table>');

  h.push('<h2>최근 7일</h2><div class="grid">');
  [['appVersion', '앱 버전'], ['install', '설치 방식'], ['windows', '윈도우'], ['lang', '언어'], ['monitors', '모니터 수'],
    ['scale', '주 모니터 배율'], ['notifications', '알림 표시'], ['folders', '독 폴더 수']].forEach(function (p) {
    h.push('<table><tr><th colspan="2">' + esc_(p[1]) + '</th></tr>');
    dist(p[0]).forEach(function (kv) { h.push('<tr><td>' + esc_(kv[0]) + '</td><td>' + kv[1] + '</td></tr>'); });
    h.push('</table>');
  });
  h.push('<table><tr><th colspan="2">켜짐 비율</th></tr>');
  [['laptop', '노트북'], ['dockAutoHide', '독 자동 숨김'], ['topBar', '상단바'], ['calendar', '캘린더 연결'],
    ['searchButton', '검색 버튼'], ['hideTaskbar', '작업 표시줄 숨기기'], ['lightMode', '가벼운 모드'], ['recycleBin', '독 휴지통'], ['allAppsCustomized', '앱 모음 묶음 바꿈']].forEach(function (p) {
    h.push('<tr><td>' + esc_(p[1]) + '</td><td>' + onRate(p[0]) + '</td></tr>');
  });
  h.push('</table>');
  h.push('<table><tr><th colspan="2">오류</th></tr><tr><td>오류 합계</td><td>' + errSum + '</td></tr><tr><td>오류 있었던 신호</td><td>' +
    errRows + ' / ' + recent.length + '</td></tr></table>');
  h.push('</div>');
  if (id) h.push('<p class="muted"><a href="https://docs.google.com/spreadsheets/d/' + esc_(id) + '/edit" target="_blank">시트 열기</a></p>');
  return HtmlService.createHtmlOutput(h.join('')).setTitle('mongdock 사용 통계');
}

function esc_(s) {
  return String(s).replace(/[&<>"']/g, function (c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
  });
}

function reply_(code, msg) {
  return ContentService.createTextOutput(JSON.stringify({ status: code, message: msg }))
    .setMimeType(ContentService.MimeType.JSON);
}
