"""send_template_reply 의 「같은 건에 같은 형식 두 번 금지」 시험 (메일·네트워크 없음, 임시 기록 파일).
실행: python tools/support-mail-mcp/test_guard.py  → 실패 0 이면 종료 코드 0.
"""
import json
import os
import sys
import tempfile

log = os.path.join(tempfile.mkdtemp(prefix="support-guard-"), "send-log.jsonl")
os.environ["MONGDOCK_SUPPORT_LOG"] = log
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server  # noqa: E402  (LOG_PATH 는 import 때 환경 변수에서 읽음)

rows = [
    # 옛 기록 (issue 칸 없음)
    {"day": "2026-10-15", "uid": "28", "to": "a@example.com", "template": "feature_request_received"},
    # 새 기록 (issue 있음)
    {"day": "2026-10-22", "uid": "34", "issue": "41", "to": "b@example.com", "template": "how_to_use"},
]
with open(log, "w", encoding="utf-8") as f:
    for r in rows:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")

bad = 0


def ok(what, cond):
    global bad
    print(("  통과  " if cond else "  실패  ") + what)
    if not cond:
        bad += 1


ok("같은 원 메일(uid 28)에 같은 형식 → 막음", server.already_sent("feature_request_received", "28") is not None)
ok("같은 원 메일(uid 28)에 다른 형식(반영 알림 how_to_use) → 통과", server.already_sent("how_to_use", "28") is None)
ok("같은 이슈(#41)에 같은 형식, 다른 메일(uid 36) → 막음", server.already_sent("how_to_use", "36", "#41") is not None)
ok("이슈 표기가 달라도 같은 이슈 → 막음", server.already_sent("how_to_use", "37", "mong-head/mongdock-issues#41") is not None)
ok("같은 사람이라도 다른 이슈(#42)·다른 메일 → 통과", server.already_sent("how_to_use", "36", "42") is None)
ok("이슈를 안 알려 주고 다른 메일 → 통과 (이슈 판단 못 함)", server.already_sent("how_to_use", "36") is None)
ok("기록 파일이 없으면 → 통과", (lambda: (os.rename(log, log + ".x"), server.already_sent("how_to_use", "34"))[1])() is None)
os.rename(log + ".x", log)
ok("issue_key: '#41'·'41'·'repo#41'·'41번'·URL 모두 '41'", {server.issue_key(x) for x in ("#41", "41", "mong-head/mongdock-issues#41", "41번", "https://github.com/mong-head/mongdock-issues/issues/41")} == {"41"})
print("  모두 통과" if bad == 0 else f"  실패 {bad}개")
sys.exit(1 if bad else 0)
