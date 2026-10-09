# 없는 장치 흉내 내기 (시험용 스위치)

배터리·밝기 조절·카메라/마이크·작은 노트북 화면·터치가 없는 PC 에서 그 기능을 시험하는 방법.
환경 변수는 **설정돼 있을 때만** 동작하고, 없으면 아무 영향이 없다 (릴리스 빌드에 그대로 있어도 무해).
가짜 값을 쓰기 시작하면 로그(`%APPDATA%\mongdock\logs\mongdock.log`)에 `가짜 … 사용` 줄이 남는다.

## 스위치 목록

| 이름 | 값 형식 | 흉내 내는 것 | 흉내 못 내는 것 |
|---|---|---|---|
| `MONGDOCK_FAKE_BATTERY` | `57` · `57,charging` · `20,ac` · `100,full` · `15,saver` · `57,notime` · `none` (쉼표/공백 구분) | 상단바 배터리 아이콘·%, 배터리 패널(남은 시간·충전 중·절약 모드), 저전력 배너(20·10·5% 단계, 방전 중일 때), "노트북" 판정 | 실제 전원 이벤트(절전·플러그 뽑기 알림), 값은 실행 내내 고정 |
| `MONGDOCK_FAKE_PRIVACY` | `camera:Zoom,mic:Discord` (`cam`/`webcam`, `mic`/`microphone`, `location`) · 끝에 `,blink` 를 붙이면 6초 사용 → 1초 끊김 반복 | 상단바 카메라(초록)·마이크(주황) 점, 점을 눌렀을 때 카드("마이크 사용 중: Discord"), 마이크가 잠깐 끊겼다 다시 켜지는 상황(blink) | 실제 레지스트리 사용 기록, 앱 아이콘/실제 앱 이름 찾기. 카드의 "개인 정보 설정…" 링크는 **실제로 설정 앱을 연다** |
| `MONGDOCK_FAKE_BRIGHTNESS` | `내장:70,DELL U2720Q:40` (이름:퍼센트, 퍼센트 생략 = 50). 이름에 `내장`/`internal`/`built-in` 이 있으면 내장 화면(`내장` 만 쓰면 "내장 디스플레이") | 제어 센터 "디스플레이" 타일의 화면별 밝기 슬라이더(내장 먼저). 슬라이더로 바꾼 값은 메모리에만 저장되고 로그에 `가짜 밝기 설정 (실제 화면은 그대로)` | **실제 밝기 변화** (WMI·DDC/CI 쓰기를 하지 않음), 모니터를 꽂고 뺄 때의 목록 변화, DDC 의 느린 응답 |
| `MONGDOCK_FAKE_SCREEN` | `1366x768@125` · `1080x1920@150` · `1280x800` (해상도@배율%, 배율 생략 = 100, 100~500) | 주 모니터를 그 화면의 **DIP 크기**로 줄여서: 상단바 폭·접기(TopBarFit), 독 아이콘 자동 축소(DockFit), 상태 패널 최대 높이·스크롤(PanelFit), 코치마크·확인 카드·알림 배너 위치, 작업 영역 | 실제 DPI(글자·아이콘은 이 PC 배율로 그려져 작게 보임), **Spotlight 폭·높이**(아래 참고), 설정 창 크기, 보조 모니터 |
| (도구) `tools/touch-test` | `touch-test tap X Y` · `hold X Y [ms]` · `drag X1 Y1 X2 Y2 [ms] [먼저누름ms]` | 손가락 탭·길게 누르기·끌기 (진짜 터치 입력으로 주입) | 핀치·두 손가락, 펜 호버, 몽독 "터치 장치" 판정(행 높이 +4, `SM_MAXIMUMTOUCHES` 로 판단 — 이 개발 PC 는 원격 프로그램 때문인지 10 으로 나와 이미 터치 장치로 판정됨) — [사용법](../tools/touch-test/README.md) |
| (없음) 야간 모드·전원 모드 | | 스위치 없음 — 디스플레이 타일의 야간 모드 동그라미, 전원 모드 세그먼트는 **실제 시스템 설정을 바꾼다** | 야간 모드 색 변화를 흉내 내지 않음. 시험했다면 바로 원래대로 돌릴 것 |
| (인자) `mongdock.exe --tour` | | 첫 설치 둘러보기(코치마크) 다시 보기 — 몽독이 이미 실행 중이면 그 몽독이 둘러보기를 띄움 | |
| (인자) `mongdock.exe --exit` | | 실행 중인 몽독을 정상 종료 (작업 표시줄·AppBar 복원) | |

## MONGDOCK_FAKE_SCREEN 설계와 한계

창의 실제 DPI 는 프로그램이 바꿀 수 없으므로 **배율은 이 PC 그대로 두고, 주 모니터 영역만 가짜 화면의 DIP 크기로 줄인다.**

- 예: 실제 1920×1080 @100% 에서 `1366x768@125` → DIP 1093×614 → 주 모니터 = 왼쪽 위부터 1093×614 px.
  몽독의 배치 계산은 모두 DIP 기준이라 그 노트북과 같은 결과가 나오고, 화면에는 왼쪽 위 1093×614 영역 안에 그려진다.
- 이 PC 가 125% 인 상태(원격 접속 중)에서 `1366x768@125` 를 주면 정확히 1366×768 px 영역이 된다.
- 작업 영역: 위·왼쪽은 실제 값(몽독 상단바 예약 포함), 오른쪽·아래는 가짜 영역 안에 예약(몽독 독)이 있으면 그 값,
  아니면 실제 화면 가장자리의 예약 두께(작업 표시줄)를 가짜 가장자리에서 뺀다.
- 적용 위치: `Monitors`(MonitorInfo 주 모니터) + `DesktopWindowService.GetPrimaryScreenBounds/GetPrimaryWorkArea`.
  다른 앱 창을 다루는 곳(창 밀어내기 WindowNudger, 전체 화면 판정, 윈도우 알림 숨김)은 **실제 값 그대로** — 다른 창을 잘못 옮기지 않게.

한계:

- **공간 예약(AppBar)은 실제로 등록된다.** 독 "공간 예약"이 켜져 있으면 가짜 화면 아래 끝(예: y=614)에 예약이 생겨
  이 PC 의 실제 작업 영역이 그만큼 줄고, 최대화한 다른 창이 작아진다. 시험이 끝나면 `mongdock.exe --exit` 로 정상 종료하면 복원된다.
  다른 사람이 쓰는 PC 라면 시험 전에 독 공간 예약을 끄거나 짧게 시험할 것.
- 가짜 화면이 실제보다 크면(세로 `1080x1920@150` = DIP 720×1280, 이 PC 높이 1080) **아래쪽이 화면 밖**이라 독이 보이지 않는다.
  상단바·패널·폭 계산 확인용으로 쓰고, 독까지 보려면 `1080x1920@200`(DIP 540×960)처럼 화면 안에 들어가는 배율로.
- **Spotlight** 는 커서 모니터 크기를 Win32 로 직접 읽어서 아직 가짜 화면을 따르지 않는다
  (UI 쪽에서 `Monitors.FromCursor()` 로 바꾸면 따름 — 아래 "남은 일").
- 설정 창은 `SystemParameters.WorkArea` 를 써서 따르지 않는다 (크기 상한만 관련, 배치 시험에는 영향 적음).
- 보조 모니터는 그대로. 다중 모니터 시험과 같이 쓰지 말 것.

## 예시 실행 (.cmd)

몽독은 한 번에 하나만 실행되므로, 지금 실행 중인 몽독을 먼저 정상 종료한 뒤 환경 변수를 넣어 다시 시작한다.
끝나면 같은 방법으로 종료하고 시작 메뉴/독에서 평소처럼 다시 켠다 (환경 변수는 그 .cmd 창에서 띄운 몽독에만 적용).

```bat
@echo off
chcp 65001 >nul
rem 낮은 노트북 화면 + 배터리 + 밝기 2개 + 마이크 사용 중(끊김 반복)
set "EXE=%LOCALAPPDATA%\Programs\mongdock\mongdock.exe"
"%EXE%" --exit
timeout /t 3 /nobreak >nul
set "MONGDOCK_FAKE_SCREEN=1366x768@125"
set "MONGDOCK_FAKE_BATTERY=18,ac"
set "MONGDOCK_FAKE_BRIGHTNESS=내장:70,DELL U2720Q:40"
set "MONGDOCK_FAKE_PRIVACY=mic:Discord,blink"
start "" "%EXE%"
```

```bat
@echo off
rem 세로 모니터 (독까지 화면 안에 들어가는 배율)
set "EXE=%LOCALAPPDATA%\Programs\mongdock\mongdock.exe"
"%EXE%" --exit
timeout /t 3 /nobreak >nul
set "MONGDOCK_FAKE_SCREEN=1080x1920@200"
start "" "%EXE%"
```

```bat
@echo off
rem 시험 끝: 가짜 값 없이 다시 시작
set "EXE=%LOCALAPPDATA%\Programs\mongdock\mongdock.exe"
"%EXE%" --exit
timeout /t 3 /nobreak >nul
start "" "%EXE%"
```

개발 빌드를 시험하려면 `EXE` 를 `src\mongdock\bin\Debug\net8.0-windows10.0.19041.0\mongdock.exe` 로 바꾼다.

> Claude 데스크톱 앱 안의 셸에서 몽독을 띄우면 MSIX 가상화로 %APPDATA%·HKCU 쓰기가 다른 곳으로 간다.
> 이 경우 .cmd 를 `C:\dev\mongdock-tmp\` 에 두고 `Start-Process explorer.exe -ArgumentList '<.cmd 경로>'` 로 실행한다.

## 시험 체크 (스위치별)

- 배터리: `18,ac` → 아이콘 18%·충전기 연결, `9` → 시작 직후 10% 단계 배너, `none` → 배터리 아이콘·제어 센터 전원 모드 타일 숨김.
- 카메라·마이크: `mic:Discord` → 주황 점, 누르면 "마이크 사용 중: Discord". `,blink` → 1초 끊겨도 점과 열린 카드가 그대로(3초 유지),
  끊긴 사이 "마이크 개인 정보 설정…" 을 눌러도 동작. 로그에 `개인 정보 설정 열기: ms-settings:privacy-microphone`.
- 밝기: 제어 센터 → 디스플레이에 "내장 디스플레이 70%", "DELL U2720Q 40%". 슬라이더를 움직이면 화면은 그대로, 로그에 값만.
- 화면: `1366x768@125` → 상단바 오른쪽 접힘, 독 아이콘 축소, 제어 센터·달력 카드가 614 DIP 안에서 스크롤.

## 남은 일 (UI 담당)

- `Views/SpotlightWindow.cs` `ShowOnCursorMonitor`: `DesktopApi.TryGetMonitorRects`/`GetMonitorScale` 대신
  `Monitors.FromCursor()`(BoundsRect·Scale)를 쓰면 가짜 화면을 따른다 (실제 동작은 같음).
