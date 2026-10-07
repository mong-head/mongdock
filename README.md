# mongdock (몽독)

윈도우 11을 맥처럼 쓰게 해 주는 독 + 상단바. C# / WPF / .NET 8, 외부 패키지 없음.

![mongdock 전체 화면](docs/screenshots/overview.png)

맥에서 원격 데스크톱으로 윈도우에 접속하면 Ctrl+Win+화살표(가상 데스크톱), 한/영, Win+A, Ctrl+Shift+T 같은 단축키가 잘 넘어가지 않는다. mongdock 은 그런 기능을 **클릭만으로** 쓰게 하는 것이 주 목적이다. 독과 상단바는 클릭해도 포커스를 가져가지 않아서, 한/영 전환이나 메뉴 단축키가 지금 쓰던 앱에 그대로 들어간다.

## 설치

### 설치 프로그램 (권장)
1. [Releases](https://github.com/mong-head/mongdock/releases) 에서 `mongdock-<버전>-setup.exe` 를 받아 실행한다. 관리자 권한은 필요 없고, `%LOCALAPPDATA%\Programs\mongdock` 에 설치된다. .NET 설치도 필요 없다.
2. 설치 중 "로그인 시 자동 실행"(기본 켜짐), "바탕 화면 바로 가기"(기본 꺼짐)를 고를 수 있다. 마지막 화면의 "mongdock 실행" 으로 바로 켠다.
3. 새 버전도 같은 방법으로 설치하면 덮어쓰기로 업그레이드된다 (실행 중인 mongdock 은 설치 프로그램이 정상 종료시킨다). 설정은 그대로 남는다.

### 설치 없이 쓰기 (zip)
1. Releases 에서 `mongdock-<버전>-win-x64.zip` 을 받아 원하는 폴더(예: `%LOCALAPPDATA%\Programs\mongdock`)에 푼다.
2. `mongdock.exe` 실행.
3. 컴퓨터를 켤 때 자동으로 켜려면: 알림 영역의 mongdock 아이콘 오른쪽 클릭 → "로그인 시 자동 실행".

처음 실행하면 독에 Finder(파일 탐색기), Launchpad(시작 메뉴), 브라우저, 설정이 고정된다. 다른 앱은 실행 중일 때 독 아이콘을 오른쪽 클릭 → "독에 고정".

> 코드 서명이 없어서 처음 실행할 때 "Windows의 PC 보호" 창이 뜰 수 있다. "추가 정보" → "실행" 을 누르면 된다.

### 직접 빌드
.NET 8 SDK 필요.

```bash
dotnet build -c Release
```

배포용 파일 만들기 (`dist\mongdock-<버전>-win-x64.zip` + `dist\mongdock-<버전>-setup.exe`):

```bash
powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version v0.2.0
```

설치 프로그램은 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 이 있어야 만들어진다 (없으면 zip 만 만든다). 스크립트는 `installer\mongdock.iss`.

## 화면

### 상단바

![상단바](docs/screenshots/topbar.png)

왼쪽부터 윈도우 로고 메뉴 · 앞에 있는 앱 이름 · 그 앱의 메뉴, 오른쪽은 가상 데스크톱 ‹ 1/3 › · 블루투스 · Wi-Fi · 사운드 · 검색 · 제어 센터 · 한/영 · 시계.

| 앱 메뉴 (크롬 "파일") | 윈도우 로고 메뉴 |
|---|---|
| ![앱 메뉴](docs/screenshots/app-menu.png) | ![로고 메뉴](docs/screenshots/logo-menu.png) |

- **앱 메뉴**: 일반 윈도우 메뉴가 있는 앱은 그 메뉴를 그대로, 브라우저·탐색기·노션 등은 단축키 메뉴를 보여 준다. 누르면 그 단축키가 앱에 들어간다. 메뉴 하나를 연 채 옆 제목으로 마우스를 옮기면 바로 바뀐다.
- **로고 메뉴**: 이 PC 정보, 설정, Microsoft Store, 작업 관리자, 절전 · 다시 시작 · 시스템 종료 · 화면 잠금 · 로그아웃 (다시 시작·종료·로그아웃은 확인 후 실행).
- **가상 데스크톱**: ‹ › 로 이동, 가운데 숫자를 누르면 작업 보기(전체 데스크톱 보기).
- **시계**: 누르면 알림 센터 + 달력.

| 사운드 | Wi-Fi |
|---|---|
| ![사운드](docs/screenshots/sound.png) | ![Wi-Fi](docs/screenshots/wifi.png) |

| 블루투스 | 제어 센터 |
|---|---|
| ![블루투스](docs/screenshots/bluetooth.png) | ![제어 센터](docs/screenshots/control-center.png) |

### 독

<img src="docs/screenshots/dock.png" alt="독" width="200" align="right">

- 화면 왼쪽 / 오른쪽 / 아래 / 위 어디든 배치. 독의 빈 곳을 끌어서 옮긴다
- 아이콘(과 구분선)을 끌어서 순서를 바꾼다. 실행 중인 앱 아이콘을 고정 영역으로 끌어 놓으면 그 자리에 고정
- 고정 아이콘을 독 밖으로 끌어내 "제거" 가 보일 때 놓으면 고정 해제. 끄는 중 Esc 나 오른쪽 클릭이면 취소
- 탐색기·바탕화면의 앱(.exe), 바로 가기(.lnk), 인터넷 바로 가기(.url), 폴더를 독에 끌어 놓으면 그 자리에 고정
- 동작: **자동 숨김**(기본, 가장자리에 마우스를 대면 나타남) / 항상 표시 / 공간 차지
- 반투명 블러 배경, 라이트 / 다크 / 시스템 테마, 맥처럼 주변 아이콘이 함께 커지는 확대
- 모든 아이콘을 macOS 규격(같은 크기·여백·둥근 사각형·그림자)으로 맞춤. 윈도우 앱은 흰 판 위에 표시
- 알림은 바운스 없이 작은 빨간 점
- 스토어 앱은 AUMID 로 저장 → 앱 업데이트 후에도 핀이 풀리지 않음

<br clear="right">

| 창이 여러 개인 앱을 누르면 | 오른쪽 클릭 |
|---|---|
| ![창 선택](docs/screenshots/window-picker.png) | ![독 메뉴](docs/screenshots/dock-menu.png) |

- **창 선택**: 실시간 미리보기로 창을 고른다. 다른 가상 데스크톱의 창은 "데스크톱 N" 으로 표시되고, 고르면 그 데스크톱으로 이동한다.
- **오른쪽 클릭**: 열린 창 목록, 크롬·엣지·웨일은 프로필별 새 창, 고정 / 제거 / 순서 / 아이콘 변경.

## 켜고 끄기

- 알림 영역(트레이) 아이콘 왼쪽 클릭: **일시 정지** — 독·상단바를 숨기고 윈도우 기본 작업 표시줄로 돌아감. 다시 누르면 복귀
- 트레이 아이콘 오른쪽 클릭: 독 보이기 / 상단바 보이기 / 일시 정지 / 윈도우 작업 표시줄 숨기기 / 로그인 시 자동 실행 / 종료
- 상단바 로고 메뉴 → mongdock 에도 같은 항목이 있다
- 트레이 아이콘이 안 보이면(윈도우 11 은 새 아이콘을 "^" 안에 숨김) `mongdock.exe` 를 한 번 더 실행해도 일시 정지가 풀린다
- "윈도우 작업 표시줄 숨기기" 는 mongdock 이 켜져 있을 때만 숨기고, 일시 정지·종료하거나 오류로 꺼지면 원래대로 돌려놓는다
- 명령줄 `mongdock.exe --exit` 로 실행 중인 mongdock 을 정상 종료할 수 있다 (트레이 "종료" 와 같음)

### 지우기
설치 프로그램으로 설치했다면: **설정 → 앱 → 설치된 앱 → mongdock → 제거**. 실행 중인 mongdock 을 끄고, 자동 실행 등록과 바로 가기를 지운다. 마지막에 "설정도 지울까요?" 에서 "예" 를 고르면 `%APPDATA%\mongdock` 도 지운다 (기본은 남김).

zip 으로 썼다면:
1. 트레이 메뉴에서 "로그인 시 자동 실행" 끄기 → 종료
2. 압축을 푼 폴더 삭제
3. (선택) 설정·로그 폴더 `%APPDATA%\mongdock` 삭제

## 설정

`%APPDATA%\mongdock\settings.json` — 저장하면 바로 반영된다.

| 항목 | 예 |
|---|---|
| 독 위치·동작·크기·테마 | `dock.edge`, `dock.mode`, `dock.iconSize`, `dock.hoverScale`, `dock.theme` |
| 상단바 색·표시 항목 | `topBar.colorMode` (`Fixed`/`Auto`/`Transparent`/`Blur`), `topBar.background`, `topBar.showAppMenus` … |
| 글꼴 | `fontFamily` — 기본 `"Pretendard"`(내장), 설치된 글꼴 이름도 가능 |
| 앱 메뉴 직접 정의 | `appMenus` — 키는 exe 이름(예 `"chrome.exe"`), 항목마다 `text` 와 `keys`(예 `"Ctrl+Shift+T"`) |
| 핀 목록 | `pins` |

색 문자열이 `""` 이면 테마 기본값. 첫 실행 때 MyDockFinder 의 `ico.ini` 가 있으면 핀 목록을 가져온다.

로그: `%APPDATA%\mongdock\logs\mongdock.log`

## 라이선스

- 내장 글꼴 [Pretendard](https://github.com/orioncactus/pretendard) — SIL Open Font License 1.1 (`src/MyDock/Fonts/OFL.txt`)
