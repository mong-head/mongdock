"""
mongdock 지원 메일함 MCP 서버 (stdio, 표준 라이브러리만).

- 몽독 전용 지원 메일함 하나에만 연결한다 (IMAP/SMTP, Gmail 앱 비밀번호).
- 주소: 환경 변수 MONGDOCK_SUPPORT_EMAIL. 비밀번호: 윈도우 자격 증명 관리자의 일반 자격 증명
  "mongdock-support-mail" (set-password.cmd 로 사용자가 직접 입력 — 이 코드는 읽기만 한다).
- 할 수 있는 일: 메일 목록·검색·읽기, 답장 초안 저장, **정해진 형식(templates.json)의 답장 보내기**.
  자유 문장으로 보내기·새 수신자에게 보내기·삭제는 없다. 보낸 메일은 send-log.jsonl 에 남는다.
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import email
import email.header
import email.policy
import imaplib
import json
import os
import re
import smtplib
import sys
import time
from datetime import datetime, timezone
from email.message import EmailMessage
from email.utils import formataddr, getaddresses, make_msgid, parseaddr

HERE = os.path.dirname(os.path.abspath(__file__))
CRED_TARGET = "mongdock-support-mail"
IMAP_HOST = os.environ.get("MONGDOCK_SUPPORT_IMAP", "imap.gmail.com")
SMTP_HOST = os.environ.get("MONGDOCK_SUPPORT_SMTP", "smtp.gmail.com")
DISPLAY_NAME = os.environ.get("MONGDOCK_SUPPORT_NAME", "몽독 지원 (mongdock support)")
LOG_PATH = os.environ.get("MONGDOCK_SUPPORT_LOG", r"C:\dev\mongdock-team\support-send-log.jsonl")
MAX_SENDS_PER_DAY = int(os.environ.get("MONGDOCK_SUPPORT_MAX_PER_DAY", "30"))
MAX_VAR_LEN = 300
BODY_PREVIEW = 4000

# ───────────────────────── 자격 증명 (윈도우 자격 증명 관리자, 읽기만) ─────────────────────────


class CREDENTIAL(ctypes.Structure):
    _fields_ = [
        ("Flags", wt.DWORD), ("Type", wt.DWORD), ("TargetName", wt.LPWSTR), ("Comment", wt.LPWSTR),
        ("LastWritten", wt.FILETIME), ("CredentialBlobSize", wt.DWORD), ("CredentialBlob", ctypes.POINTER(ctypes.c_byte)),
        ("Persist", wt.DWORD), ("AttributeCount", wt.DWORD), ("Attributes", ctypes.c_void_p),
        ("TargetAlias", wt.LPWSTR), ("UserName", wt.LPWSTR),
    ]


def read_credential() -> tuple[str, str] | None:
    """(사용자 이름, 비밀번호) 또는 None. 비밀번호는 로그·응답에 절대 넣지 않는다."""
    advapi = ctypes.WinDLL("advapi32", use_last_error=True)
    pcred = ctypes.POINTER(CREDENTIAL)()
    if not advapi.CredReadW(CRED_TARGET, 1, 0, ctypes.byref(pcred)):  # 1 = CRED_TYPE_GENERIC
        return None
    try:
        c = pcred.contents
        blob = ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize)
        # cmdkey 는 UTF-16LE 로 저장
        try:
            secret = blob.decode("utf-16-le")
        except UnicodeDecodeError:
            secret = blob.decode("utf-8", "replace")
        return (c.UserName or "", secret.replace(" ", ""))
    finally:
        advapi.CredFree(pcred)


def account() -> tuple[str, str]:
    addr = os.environ.get("MONGDOCK_SUPPORT_EMAIL", "").strip()
    cred = read_credential()
    if not addr and cred:
        addr = cred[0]
    if not addr or not cred:
        raise RuntimeError(
            "지원 메일함이 아직 연결되지 않았어요. tools/support-mail-mcp/set-password.cmd 를 실행해 "
            "주소와 앱 비밀번호를 사용자 본인이 입력해 주세요.")
    return addr, cred[1]


# ───────────────────────── IMAP ─────────────────────────


def imap() -> imaplib.IMAP4_SSL:
    addr, pw = account()
    m = imaplib.IMAP4_SSL(IMAP_HOST, 993)
    m.login(addr, pw)
    return m


def special_folder(m: imaplib.IMAP4_SSL, flag: str, fallback: str) -> str:
    """\\Drafts, \\Sent 같은 특수 폴더 이름 (Gmail 은 UI 언어에 따라 이름이 다름)."""
    typ, data = m.list()
    if typ == "OK":
        for raw in data or []:
            line = raw.decode("utf-8", "replace") if isinstance(raw, bytes) else str(raw)
            if flag in line:
                mm = re.search(r'"([^"]+)"\s*$', line) or re.search(r"\s(\S+)\s*$", line)
                if mm:
                    return mm.group(1)
    return fallback


def decode_header(v: str | None) -> str:
    if not v:
        return ""
    return str(email.header.make_header(email.header.decode_header(v)))


def text_body(msg: email.message.Message) -> str:
    if msg.is_multipart():
        for part in msg.walk():
            if part.get_content_type() == "text/plain" and not part.get_filename():
                return part.get_content() if hasattr(part, "get_content") else part.get_payload(decode=True).decode("utf-8", "replace")
        for part in msg.walk():
            if part.get_content_type() == "text/html" and not part.get_filename():
                html = part.get_content()
                return re.sub(r"<[^>]+>", " ", html)
        return ""
    try:
        return msg.get_content()
    except Exception:
        payload = msg.get_payload(decode=True) or b""
        return payload.decode("utf-8", "replace")


def fetch(m: imaplib.IMAP4_SSL, uid: str) -> email.message.EmailMessage:
    typ, data = m.uid("fetch", uid, "(BODY.PEEK[])")
    if typ != "OK" or not data or not isinstance(data[0], tuple):
        raise RuntimeError(f"메일을 찾을 수 없어요 (uid {uid})")
    return email.message_from_bytes(data[0][1], policy=email.policy.default)


def summary(uid: str, msg: email.message.Message) -> dict:
    return {
        "uid": uid,
        "from": decode_header(msg.get("From")),
        "subject": decode_header(msg.get("Subject")),
        "date": msg.get("Date", ""),
    }


def tool_list_messages(args: dict) -> dict:
    folder = args.get("folder") or "INBOX"
    unread = bool(args.get("unread_only", True))
    limit = max(1, min(int(args.get("limit", 20)), 100))
    query = args.get("gmail_query")
    m = imap()
    try:
        m.select(f'"{folder}"' if " " in folder else folder, readonly=True)
        if query:
            typ, data = m.uid("search", None, "X-GM-RAW", f'"{query}"')
        else:
            typ, data = m.uid("search", None, "UNSEEN" if unread else "ALL")
        uids = (data[0].split() if typ == "OK" and data and data[0] else [])[-limit:]
        out = []
        for u in reversed(uids):
            typ, d = m.uid("fetch", u, "(BODY.PEEK[HEADER.FIELDS (FROM SUBJECT DATE)])")
            if typ == "OK" and d and isinstance(d[0], tuple):
                out.append(summary(u.decode(), email.message_from_bytes(d[0][1], policy=email.policy.default)))
        return {"folder": folder, "count": len(out), "messages": out}
    finally:
        m.logout()


def tool_get_message(args: dict) -> dict:
    uid = str(args["uid"])
    m = imap()
    try:
        m.select("INBOX", readonly=True)
        msg = fetch(m, uid)
        body = text_body(msg)
        attachments = [p.get_filename() for p in msg.walk() if p.get_filename()]
        return {**summary(uid, msg), "body": body[:BODY_PREVIEW], "truncated": len(body) > BODY_PREVIEW,
                "attachments": attachments}
    finally:
        m.logout()


def tool_mark_read(args: dict) -> dict:
    uid = str(args["uid"])
    m = imap()
    try:
        m.select("INBOX")
        m.uid("store", uid, "+FLAGS", "(\\Seen)")
        return {"uid": uid, "seen": True}
    finally:
        m.logout()


# ───────────────────────── 답장 만들기 ─────────────────────────


def load_templates() -> dict:
    with open(os.path.join(HERE, "templates.json"), encoding="utf-8") as f:
        return json.load(f)


def reply_to(msg: email.message.Message) -> str:
    """답장 받을 주소 = 원래 보낸 사람(Reply-To 우선) 한 명. 우리 주소면 거부."""
    own = account()[0].lower()
    cands = getaddresses([msg.get("Reply-To", "")]) or []
    cands = [a for _, a in cands if a] or [parseaddr(msg.get("From", ""))[1]]
    addr = (cands[0] if cands else "").strip()
    if not addr or "@" not in addr:
        raise RuntimeError("답장 받을 주소를 찾지 못했어요")
    if addr.lower() == own:
        raise RuntimeError("우리 지원 주소로는 답장하지 않아요")
    return addr


def build_reply(msg: email.message.Message, body: str) -> EmailMessage:
    addr = account()[0]
    r = EmailMessage()
    subj = decode_header(msg.get("Subject")) or "mongdock"
    r["Subject"] = subj if subj.lower().startswith("re:") else f"Re: {subj}"
    r["From"] = formataddr((DISPLAY_NAME, addr))
    r["To"] = reply_to(msg)
    r["Message-ID"] = make_msgid(domain=addr.split("@")[-1])
    if msg.get("Message-ID"):
        r["In-Reply-To"] = msg["Message-ID"]
        r["References"] = (msg.get("References", "") + " " + msg["Message-ID"]).strip()
    r.set_content(body)
    return r


def render(template_id: str, variables: dict, lang: str) -> str:
    templates = load_templates()
    t = templates.get("templates", {}).get(template_id)
    if not t:
        raise RuntimeError(f"없는 형식이에요: {template_id}. 맞는 형식이 없으면 보내지 말고 PM 에게 넘기세요.")
    text = t.get(lang) or t.get("ko")
    needed = set(re.findall(r"\{(\w+)\}", text))
    missing = [k for k in needed if not str(variables.get(k, "")).strip()]
    if missing:
        raise RuntimeError(f"빈 값: {', '.join(missing)}")
    extra = [k for k in variables if k not in needed]
    if extra:
        raise RuntimeError(f"이 형식에 없는 값: {', '.join(extra)}")
    for k, v in variables.items():
        v = str(v)
        if len(v) > MAX_VAR_LEN or "\n\n" in v or "http" in v.lower() and k != "link":
            raise RuntimeError(f"값 '{k}' 가 너무 길거나 형식에 맞지 않아요 (짧은 한 줄만, 링크는 link 값에만)")
    body = text.format(**{k: str(v).strip() for k, v in variables.items()})
    footer = templates.get("footer", {}).get(lang) or templates.get("footer", {}).get("ko", "")
    return body.rstrip() + ("\n\n" + footer if footer else "")


def sends_today() -> int:
    if not os.path.exists(LOG_PATH):
        return 0
    today = datetime.now().strftime("%Y-%m-%d")
    with open(LOG_PATH, encoding="utf-8") as f:
        return sum(1 for line in f if f'"day": "{today}"' in line)


def log_send(entry: dict) -> None:
    os.makedirs(os.path.dirname(LOG_PATH), exist_ok=True)
    with open(LOG_PATH, "a", encoding="utf-8") as f:
        f.write(json.dumps(entry, ensure_ascii=False) + "\n")


def tool_list_templates(args: dict) -> dict:
    t = load_templates()
    return {"templates": {k: {"use_when": v.get("use_when", ""), "variables": sorted(set(re.findall(r"\{(\w+)\}", v.get("ko", ""))))}
                          for k, v in t.get("templates", {}).items()},
            "rule": t.get("rule", "")}


def tool_preview_template_reply(args: dict) -> dict:
    m = imap()
    try:
        m.select("INBOX", readonly=True)
        msg = fetch(m, str(args["uid"]))
        body = render(args["template_id"], args.get("variables", {}), args.get("lang", "ko"))
        r = build_reply(msg, body)
        return {"to": r["To"], "subject": r["Subject"], "body": body}
    finally:
        m.logout()


def tool_send_template_reply(args: dict) -> dict:
    if sends_today() >= MAX_SENDS_PER_DAY:
        raise RuntimeError(f"오늘 보낼 수 있는 답장 수({MAX_SENDS_PER_DAY})를 넘었어요. PM 에게 알리세요.")
    uid = str(args["uid"])
    m = imap()
    try:
        m.select("INBOX")
        msg = fetch(m, uid)
        body = render(args["template_id"], args.get("variables", {}), args.get("lang", "ko"))
        r = build_reply(msg, body)
        addr, pw = account()
        with smtplib.SMTP_SSL(SMTP_HOST, 465) as s:
            s.login(addr, pw)
            s.send_message(r)
        m.uid("store", uid, "+FLAGS", "(\\Seen \\Answered)")
    finally:
        m.logout()
    log_send({"day": datetime.now().strftime("%Y-%m-%d"), "at": datetime.now(timezone.utc).isoformat(),
              "uid": uid, "to": r["To"], "subject": r["Subject"], "template": args["template_id"],
              "variables": args.get("variables", {})})
    return {"sent": True, "to": r["To"], "subject": r["Subject"], "template": args["template_id"]}


def tool_create_draft_reply(args: dict) -> dict:
    """형식에 안 맞는 경우 사람이 고쳐 보낼 초안 (보내지 않음)."""
    uid = str(args["uid"])
    body = str(args["body"])
    m = imap()
    try:
        m.select("INBOX", readonly=True)
        msg = fetch(m, uid)
        r = build_reply(msg, body)
        drafts = special_folder(m, "\\Drafts", "[Gmail]/Drafts")
        m.append(f'"{drafts}"', "(\\Draft)", imaplib.Time2Internaldate(time.time()), r.as_bytes())
        return {"draft_saved": True, "folder": drafts, "to": r["To"], "subject": r["Subject"]}
    finally:
        m.logout()


def tool_status(args: dict) -> dict:
    try:
        addr, _ = account()
    except RuntimeError as e:
        return {"connected": False, "message": str(e)}
    try:
        m = imap()
        m.logout()
        return {"connected": True, "address": addr, "sends_today": sends_today(), "max_per_day": MAX_SENDS_PER_DAY}
    except Exception as e:  # 비밀번호는 넣지 않음
        return {"connected": False, "address": addr, "message": f"로그인 실패: {type(e).__name__}"}


TOOLS = {
    "status": (tool_status, "지원 메일함 연결 상태와 오늘 보낸 답장 수.", {}),
    "list_messages": (tool_list_messages, "받은 메일 목록 (기본: 안 읽은 메일). gmail_query 로 Gmail 검색식 사용 가능.",
                      {"folder": {"type": "string"}, "unread_only": {"type": "boolean"}, "limit": {"type": "integer"},
                       "gmail_query": {"type": "string"}}),
    "get_message": (tool_get_message, "메일 하나 읽기 (읽음 표시 안 함).", {"uid": {"type": "string"}}),
    "mark_read": (tool_mark_read, "메일을 읽음으로 표시.", {"uid": {"type": "string"}}),
    "list_templates": (tool_list_templates, "보낼 수 있는 답장 형식과 각 형식의 값 목록.", {}),
    "preview_template_reply": (tool_preview_template_reply, "형식 답장을 보내기 전에 미리 보기 (보내지 않음).",
                               {"uid": {"type": "string"}, "template_id": {"type": "string"},
                                "variables": {"type": "object"}, "lang": {"type": "string", "enum": ["ko", "en"]}}),
    "send_template_reply": (tool_send_template_reply,
                            "원래 보낸 사람에게 정해진 형식의 답장을 보냄. 형식에 맞지 않는 경우엔 쓰지 말고 PM 에게 넘길 것.",
                            {"uid": {"type": "string"}, "template_id": {"type": "string"},
                             "variables": {"type": "object"}, "lang": {"type": "string", "enum": ["ko", "en"]}}),
    "create_draft_reply": (tool_create_draft_reply, "형식에 안 맞는 답장의 초안을 임시보관함에 저장 (보내지 않음, 사람이 확인 후 보냄).",
                           {"uid": {"type": "string"}, "body": {"type": "string"}}),
}

REQUIRED = {"get_message": ["uid"], "mark_read": ["uid"], "preview_template_reply": ["uid", "template_id"],
            "send_template_reply": ["uid", "template_id"], "create_draft_reply": ["uid", "body"]}

# ───────────────────────── MCP (JSON-RPC over stdio) ─────────────────────────


def respond(id_, result=None, error=None):
    msg = {"jsonrpc": "2.0", "id": id_}
    if error is not None:
        msg["error"] = error
    else:
        msg["result"] = result
    sys.stdout.write(json.dumps(msg, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def handle(req: dict):
    method = req.get("method")
    id_ = req.get("id")
    if method == "initialize":
        respond(id_, {"protocolVersion": req.get("params", {}).get("protocolVersion", "2024-11-05"),
                      "capabilities": {"tools": {}},
                      "serverInfo": {"name": "mongdock-support-mail", "version": "1.0.0"}})
    elif method == "tools/list":
        respond(id_, {"tools": [
            {"name": n, "description": d, "inputSchema": {"type": "object", "properties": p, "required": REQUIRED.get(n, [])}}
            for n, (_, d, p) in TOOLS.items()]})
    elif method == "tools/call":
        params = req.get("params", {})
        name = params.get("name")
        args = params.get("arguments") or {}
        if name not in TOOLS:
            respond(id_, error={"code": -32601, "message": f"unknown tool {name}"})
            return
        try:
            out = TOOLS[name][0](args)
            respond(id_, {"content": [{"type": "text", "text": json.dumps(out, ensure_ascii=False, indent=2)}]})
        except Exception as e:
            respond(id_, {"content": [{"type": "text", "text": f"실패: {e}"}], "isError": True})
    elif method == "ping":
        respond(id_, {})
    elif id_ is not None:
        respond(id_, error={"code": -32601, "message": f"unknown method {method}"})
    # 알림(notifications/*)은 응답 없음


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            handle(json.loads(line))
        except Exception as e:
            sys.stderr.write(f"bad request: {e}\n")


if __name__ == "__main__":
    main()
