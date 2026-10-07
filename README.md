# MyDock

윈도우 11용 맥 스타일 독 + 상단바. C# / WPF / .NET 8, 외부 패키지 없음.

원격 데스크톱(맥 → 원격 접속 앱) 환경에서는 Ctrl+Win+화살표, 한/영, Win+A, Ctrl+Shift+T 같은 단축키가 넘어가지 않는다. 그런 기능을 **클릭만으로** 쓰는 것이 주 목적이다. 독과 상단바는 클릭해도 포커스를 가져가지 않아서, 한/영 전환이나 메뉴 단축키가 지금 쓰던 앱에 그대로 들어간다.

## 설치

### 받아서 바로 실행
1. [Releases](https://github.com/mong-head/mongdock/releases) 에서 `MyDock-<버전>-win-x64.zip` 을 받아 원하는 폴더(예: `%LOCALAPPDATA%\Programs\MyDock`)에 푼다.
2. `MyDock.exe` 실행. .NET 설치는 필요 없다.
3. 컴퓨터를 켤 때 자동으로 켜려면: 알림 영역의 MyDock 아이콘 → "로그인 시 자동 실행".

처음 실행하면 독에 Finder(파일 탐색기), Launchpad(시작 메뉴), 브라우저, 설정이 고정된다. 앱 아이콘을 오른쪽 클릭 → "독에 고정"으로 추가한다.

### 직접 빌드
.NET 8 SDK 필요.

```bash
dotnet build -c Release
```

배포용 단일 실행 파일 만들기 (`dist\MyDock-win-x64.zip`):

```bash
powershell -ExecutionPolicy Bypass -File build-release.ps1
```

## 켜고 끄기

- 알림 영역(트레이) 아이콘 왼쪽 클릭: **일시 정지** — 독·상단바를 숨기고 윈도우 기본 작업 표시줄로 돌아감. 다시 누르면 복귀
- 트레이 아이콘 오른쪽 클릭: 독 보이기 / 상단바 보이기 / 일시 정지 / 윈도우 작업 표시줄 숨기기 / 로그인 시 자동 실행 / 종료
- 상단바 로고 메뉴 → MyDock 에도 같은 항목이 있다
- "윈도우 작업 표시줄 숨기기"는 MyDock 이 켜져 있을 때만 숨기고, 일시 정지·종료하거나 오류로 꺼지면 원래대로 돌려놓는다

## 기능

### 독
- 화면 왼쪽 / 오른쪽 / 아래 / 위 어디든 배치. 빈 곳을 끌어서 옮김
- 동작: **자동 숨김**(기본, 가장자리에 마우스를 대면 나타남) / 항상 표시 / 공간 차지
- 반투명 블러 배경, 라이트 / 다크 / 시스템 테마, 맥처럼 주변 아이콘이 함께 커지는 확대
- 모든 아이콘을 macOS 규격(같은 크기·여백·둥근 사각형·그림자)으로 맞춤. 윈도우 앱은 흰 판 위에 표시
- 핀 고정 앱 + 실행 중 앱, 알림은 바운스 없이 작은 빨간 점
- 창이 여러 개인 앱: 클릭하면 실시간 미리보기로 창 선택. 다른 가상 데스크톱의 창도 표시하고, 고르면 그 데스크톱으로 이동
- 오른쪽 클릭: 열린 창 목록, 브라우저(크롬·엣지·웨일) 프로필별 새 창, 고정 / 제거 / 순서 / 아이콘 변경
- 스토어 앱은 AUMID 로 저장 → 앱 업데이트 후에도 핀이 풀리지 않음
- 커스텀 아이콘은 `%APPDATA%\MyDock\icons\` 로 복사해서 보관

### 상단바
- 윈도우 로고 메뉴: 이 PC 정보, 설정, Microsoft Store, 작업 관리자, 절전 / 다시 시작 / 시스템 종료 / 잠금 / 로그아웃(확인 후 실행)
- 앱 이름 + **그 앱의 메뉴**(파일 · 편집 · 보기 …). 일반 윈도우 메뉴가 있는 앱은 그 메뉴를, 브라우저·탐색기·노션 등은 단축키 메뉴를 보여 준다. 메뉴 하나를 연 채 옆 제목으로 마우스를 옮기면 바로 전환
- 가상 데스크톱 ‹ 1 / 2 › (숫자를 누르면 작업 보기)
- 블루투스 · Wi-Fi · 사운드 · 제어 센터 패널(볼륨, 출력 장치 전환, 지금 재생 중, 블루투스 기기, Wi-Fi 정보)
- 검색, 한/영 표시 + 클릭 전환, 시계(누르면 알림 센터 + 달력)

## 설정

`%APPDATA%\MyDock\settings.json` — 저장하면 바로 반영된다.

| 항목 | 예 |
|---|---|
| 독 위치·동작·크기·테마 | `dock.edge`, `dock.mode`, `dock.iconSize`, `dock.hoverScale`, `dock.theme` |
| 상단바 색·표시 항목 | `topBar.colorMode` (`Fixed`/`Auto`/`Transparent`/`Blur`), `topBar.background`, `topBar.showAppMenus` … |
| 글꼴 | `fontFamily` — 기본 `"Pretendard"`(내장), 설치된 글꼴 이름도 가능 |
| 앱 메뉴 직접 정의 | `appMenus` — 키는 exe 이름(예 `"chrome.exe"`), 항목마다 `text` 와 `keys`(예 `"Ctrl+Shift+T"`) |
| 핀 목록 | `pins` |

색 문자열이 `""` 이면 테마 기본값. 첫 실행 때 MyDockFinder 의 `ico.ini` 가 있으면 핀 목록을 가져온다.

로그: `%APPDATA%\MyDock\logs\mydock.log`

## 라이선스

- 내장 글꼴 [Pretendard](https://github.com/orioncactus/pretendard) — SIL Open Font License 1.1 (`src/MyDock/Fonts/OFL.txt`)
