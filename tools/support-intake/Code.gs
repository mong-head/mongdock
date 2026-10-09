/**
 * mongdock "문제 신고하기" 받는 창구 (Google Apps Script 웹 앱).
 * 몽독 앱이 JSON 을 POST 하면 지원 주소(mongdock+help@gmail.com)로 메일 한 통을 만든다.
 * mongdock@gmail.com 계정에서 배포: 실행 = 나, 액세스 = 모든 사용자. 앱에는 비밀번호가 들어가지 않는다.
 *
 * 요청 본문 (application/json):
 * { "token": "...", "clientId": "PC별 무작위 id", "kind": "bug|question|idea",
 *   "message": "사용자가 쓴 내용", "contact": "답장 받을 이메일(선택)",
 *   "appVersion": "0.4.2", "lang": "ko|en", "diagnostics": "개인정보를 가린 진단 정보" }
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

    // 하루 한도 (PC별, 전체)
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
      'PC id: ' + clientId + '\n' +
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

function doGet() {
  return reply_(200, 'mongdock report intake');
}

function reply_(code, msg) {
  return ContentService.createTextOutput(JSON.stringify({ status: code, message: msg }))
    .setMimeType(ContentService.MimeType.JSON);
}
