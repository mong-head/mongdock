# mongdock "문제 신고하기" 받는 창구 (Google Apps Script)

몽독 앱의 "문제 신고하기…"가 보낸 내용을 받아 지원 주소 `mongdock+help@gmail.com` 으로 메일을 만든다. 사용자는 메일 계정이 없어도 신고할 수 있고, 모든 신고는 지원 메일함 한 곳(→ `tools/support-mail-mcp`)으로 모인다.

## 배포 (mongdock@gmail.com 으로 한 번)
1. https://script.google.com 에 `mongdock@gmail.com` 으로 로그인 → **새 프로젝트**. 이름: `mongdock-report`.
2. 기본 `Code.gs` 내용을 지우고 이 폴더의 `Code.gs` 를 통째로 붙여 넣기 → 저장(Ctrl+S).
3. 오른쪽 위 **배포 → 새 배포** → 톱니바퀴에서 **웹 앱** 선택.
   - 설명: `v1`
   - 다음 사용자 인증 정보로 실행: **나 (mongdock@gmail.com)**
   - 액세스 권한이 있는 사용자: **모든 사용자**
4. **배포** → 권한 확인 창에서 mongdock 계정 선택 → "확인되지 않은 앱" 이 나오면 **고급 → mongdock-report(으)로 이동** → **허용** (자기 계정의 메일 보내기 권한).
5. 나오는 **웹 앱 URL**(`https://script.google.com/macros/s/.../exec`)을 PM 세션에 알려 준다 → 앱 설정에 들어감.

코드를 고친 뒤에는 **배포 → 배포 관리 → 수정(연필) → 버전: 새 버전 → 배포** 로 같은 URL 을 유지한다.

## 동작
- 앱이 보내는 JSON: `token`, `clientId`(PC별 무작위), `kind`(bug/question/idea), `message`, `contact`(선택), `appVersion`, `lang`, `diagnostics`(앱이 개인정보를 가린 뒤).
- 하루 한도: PC 당 5건, 전체 80건(Gmail 일반 계정 Apps Script 발송 한도 100통 안쪽). 넘으면 429.
- 답장 주소를 적은 신고만 `Reply-To` 가 붙어서, 지원 MCP 의 형식 답장이 그 사람에게 간다. 안 적었으면 답장하지 않는다(지원 MCP 가 자기 주소로는 답장을 거부).
- `token` 은 비밀이 아니다(앱에 들어 있음). 아무나 막 보내는 걸 조금 거르는 용도이고, 실제 보호는 하루 한도다.
