# mongdock 지원 메일함 MCP

몽독 대표 Gmail(`mongdock@gmail.com`) **하나에만** 연결하고, 그중 지원 주소 **`mongdock+help@gmail.com`** 으로 온 메일만 다루는 MCP 서버(대표 메일함의 다른 메일은 열지 않음). 이 저장소의 `.mcp.json` 으로 몽독 프로젝트 세션에서만 보인다. 표준 라이브러리만 쓴다(Python 3.9+).

## 연결 (사용자가 한 번)
1. 몽독 전용 Gmail 계정을 만들고 2단계 인증을 켠다.
2. Google 계정 → 보안 → 앱 비밀번호에서 16자리 비밀번호를 만든다.
3. `set-password.cmd` 를 더블클릭해 주소와 앱 비밀번호를 **직접** 입력한다 → 윈도우 자격 증명 관리자(`mongdock-support-mail`)에 저장. AI 와 저장소에는 남지 않는다.
4. Claude 세션을 다시 열고 `status` 도구로 연결 확인.

## 도구
| 도구 | 하는 일 |
|---|---|
| `status` | 연결 상태, 오늘 보낸 답장 수 |
| `list_messages` / `get_message` / `mark_read` | 받은 메일 보기 (Gmail 검색식 가능) |
| `list_templates` / `preview_template_reply` | 답장 형식 목록, 보내기 전 미리 보기 |
| `send_template_reply` | **`templates.json` 의 형식으로만**, 원래 보낸 사람에게만 답장 (하루 30통 한도, `C:\dev\mongdock-team\support-send-log.jsonl` 에 기록) |
| `create_draft_reply` | 형식에 안 맞는 답장은 임시보관함에 초안만 (사람이 보냄) |

자유 문장 보내기, 새 수신자에게 보내기, 삭제는 없다. 운영 규칙은 `.claude/agents/mongdock-support.md`.
