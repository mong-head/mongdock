# tools — 개발·시험 도구

앱(`src/mongdock`)과 따로 도는 도구 모음. 모두 `mongdock.sln` 밖에 있고 외부 NuGet 을 쓰지 않는다.
C# 도구는 `dotnet build tools/<이름>/<이름>.csproj` 또는 `dotnet run --project tools/<이름> -c Release -- <옵션>`.
자세한 사용법은 각 폴더의 README (없는 것은 파일 맨 위 주석).

| 폴더 | 하는 일 | 사용자 PC 에 미치는 영향 |
|---|---|---|
| [`report-test`](report-test/README.md) | "문제 신고하기" 개인정보 가리기 단위 시험, 신고 창 PNG(라이트/다크·영어), 앱 메뉴 규칙 영어 시험 | 없음 (보내지 않음) |
| [`startup-test`](startup-test/README.md) | 로그인 시 자동 실행(Run 키·StartupTask)·설치 방식 판별 시험, `--transfer` 설정 옮기기 시험, `--pins` 첫 독 구성 미리 보기 | Run 키에 시험용 값 `mongdock-selftest` 를 잠깐 쓰고 지움 |
| [`taskbar-watch`](taskbar-watch/README.md) | 작업 표시줄이 언제 다시 보였고 몇 ms 만에 숨었는지 기록 (#13) | 없음 (이벤트만 읽음) |
| [`touch-test`](touch-test/README.md) | 터치 없는 PC 에서 손가락 입력(탭·길게 누르기·끌기) 흉내 | **실제 화면에 입력이 들어감** — 먼저 `--dry-run` |
| [`i18n`](#i18n) | 번역(영어) 키 뽑기·검사, 감싸지 않은 한글 찾기 | 없음 |
| [`msix`](msix/build-msix.ps1) | 스토어용 MSIX 패키지 만들기 (`-Version 0.6.0`, 시험 서명 `-CertThumbprint`) | 없음 (`dist\msix` 에 파일만) |
| [`make-icon`](make-icon/make-icon.ps1) | 앱 아이콘(.ico)·미리 보기 PNG 만들기 | 없음 |
| [`support-intake`](support-intake/README.md) | 신고·사용 통계를 받는 Google Apps Script(웹 앱) 코드와 배포 순서, 통계 대시보드 | 없음 (배포는 사용자가 script.google.com 에서) |
| [`support-mail-mcp`](support-mail-mcp/README.md) | 지원 메일함(Gmail) MCP 서버 — 형식 답장 보내기·초안 | 지원 메일함에서 메일을 보냄 (형식 답장만) |

없는 장치(배터리·밝기·카메라·작은 화면·스토어 체험판)를 흉내 내는 환경 변수는 [`docs/testing.md`](../docs/testing.md).
사용 통계는 `MONGDOCK_STATS=log`(보내지 않고 로그에만), 시험용 데이터 폴더는 `MONGDOCK_DATA_DIR`.

## i18n

화면 문구는 한국어 원문이 키다(`Loc.T("…")`, 보간은 `Loc.F($"…")` → 키는 `"{0}…"` 서식). 영어는 `src/mongdock/i18n/en.json`.

```
python tools/i18n/extract.py --check     # 키 수·번역 수·빠진 것·안 쓰는 것 (빠진 게 있으면 종료 코드 1)
python tools/i18n/extract.py --missing   # 빠진 키만 JSON 으로 (번역 채울 때)
python tools/i18n/leftover.py            # Loc 로 감싸지 않은 한글 리터럴 (로그·진단은 일부러 남긴 것 — 사람이 판단)
python tools/i18n/wrap.py <파일>...      # 한글 리터럴을 Loc.T/F 로 감싸기 (한 번 쓰고 검토)
python tools/i18n/unwrap.py <파일> <조각>... # 잘못 감싼 것(로그·비교 키) 되돌리기
```

- 한국어 콘솔(cp949)에서 extract.py 출력이 깨지면 `PYTHONIOENCODING=utf-8` 을 앞에 붙인다.
- `const string` 이나 변수에 담은 문구는 extract.py 가 못 본다 → 쓰는 곳에서 `Loc.T(변수)` 로 감싸고 en.json 에 손으로 넣는다 (예: 공휴일 이름, 캘린더 색 이름).
- 같은 한국어가 뜻이 다르게 두 군데 쓰이면(예: "입력" = 윈도우 설정 Typing / 소리 패널 Input) 한쪽은 `Loc.IsEnglish ? … : …` 로 따로.
- 변경 내역(`Changelog.json`)은 `text_en`·`headline_en`·코치 `title_en`/`body_en`·`knownIssues_en`, 앱 메뉴 규칙은 `title_en`·`text_en` (없으면 한국어).
