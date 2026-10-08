# 윈도우 10 에서의 몽독

몽독은 윈도우 11 (빌드 26200) 에서 개발·실측했다. 윈도우 10 은 **2004 이상(빌드 19041~19045)** 에서 설치·실행되며
(설치 프로그램 `MinVersion=10.0.19041`, 프로젝트 `SupportedOSPlatformVersion=10.0.19041.0`), 그보다 오래된 1909 이하는 설치가 막힌다.
아래 표는 코드·공식 문서·알려진 오픈소스 구현을 기준으로 점검한 결과다. **실제 윈도우 10 PC 에서는 아직 확인하지 않았다** (맨 아래 "확인할 것" 참고).
코드에서 윈도우 10 판정은 `Environment.OSVersion.Version.Build < 22000` (앱 매니페스트에 Windows 10 supportedOS 가 있어 실제 빌드가 나온다).

| 기능 | 윈도우 10 | 비고 / 근거 |
|---|---|---|
| 설치 프로그램·.NET 8 Desktop Runtime 자동 설치 | 동일 | .NET 8 은 윈도우 10 1607+ 지원. 런타임 설치 때만 UAC. [.NET 8 지원 OS](https://github.com/dotnet/core/blob/main/release-notes/8.0/supported-os.md) |
| 독·상단바 AppBar (SHAppBarMessage) | 동일 | 문서화된 API. |
| 윈도우 작업 표시줄 숨기기 / 자동 숨김(ABM_SETSTATE) | 동일 | Shell_TrayWnd·Shell_SecondaryTrayWnd 숨김 + ABS_AUTOHIDE 는 윈도우 10 에서도 같은 구조. 작업 표시줄의 "찾기" 검색 상자·Cortana 단추는 Shell_TrayWnd 의 자식이라 함께 숨겨진다. |
| 트레이 아이콘 가로채기 (숨은 Shell_TrayWnd + explorer 로 전달) | 동일 | ManagedShell/RetroBar 가 윈도우 10 에서 쓰는 방식과 같다. [ManagedShell](https://github.com/cairoshell/ManagedShell) |
| 트레이 아이콘 바/⌃ 나누기 (윈도우 설정 따르기) | **다름 → 지원 추가** | 윈도우 11 의 `HKCU\Control Panel\NotifyIconSettings` 가 없다. 대신 `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify\IconStreams` (비공식 바이너리: 머리글 20바이트 + 1640바이트 항목, 경로는 ROT13 UTF-16, +528 = 표시 설정 2 "항상 표시") 를 읽는다. "알림 영역에 항상 모든 아이콘 표시"(`Explorer\EnableAutoTray=0`) 면 모두 상단바. explorer 가 이 값을 늦게 기록하므로 윈도우 설정에서 바꾼 직후 반영이 늦을 수 있다 — 몽독에서 직접 끌어 옮기면 즉시. 근거: [sevenforums 스크립트](https://www.sevenforums.com/customization/162395-vbscript-add-program-notification-area.html), 윈도우 11 에 남은 같은 값 덤프로 uID(+0x428)·GUID(+0x42C) 위치 확인. |
| 윈도우 기본 알림 팝업 숨기기 (NativeToastSuppressor) | **보강, 미확인** | 윈도우 10 토스트도 ShellExperienceHost 의 `Windows.UI.Core.CoreWindow` "New notification"/"새 알림" 창으로 알려져 있으나, 대기 중에 클로킹 대신 숨김일 수 있어 윈도우 10 에서는 EVENT_OBJECT_SHOW 도 받고 보이지 않는 창은 대기 상태로 본다. 판별이 안 맞으면 아무것도 옮기지 않아 "윈도우 팝업과 몽독 배너가 둘 다 보이는" 정도로 끝난다. |
| 알림 배너·알림 목록 (wpndatabase.db) | 동일 | 윈도우 10 1607+ 에 같은 DB(`%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db`, Notification/NotificationHandler 표). TransientTable 이 없으면 이미 대체 쿼리로 읽는다. winsqlite3.dll 은 윈도우 10 기본 포함. |
| 알림 센터 열기 | **다름 → 수정** | 윈도우 10 에는 Win+N 이 없고 알림이 관리 센터(Win+A) 안에 있다 → 윈도우 10 에서는 Win+A 를 보낸다. 빠른 설정(Win+A) 은 윈도우 10 관리 센터가 열린다. |
| 블러(아크릴, SetWindowCompositionAttribute ACCENT 4) | 동일(성능 주의) | 윈도우 10 1803+ 지원. 윈도우 10 1903+ 에서 아크릴 창을 끌거나 크기를 바꿀 때 느려지는 문제가 알려져 있다 (독 배경은 확대 애니메이션 때 크기가 바뀜). |
| 둥근 모서리 (DWMWA_WINDOW_CORNER_PREFERENCE=33, BORDER_COLOR=34) | **다름 → 대체** | 윈도우 11(22000+) 전용이라 윈도우 10 에서는 호출이 실패(무시)한다. 윈도우 10 에서는 블러 창에 둥근 창 영역(SetWindowRgn, 반경 8 DIP)을 대신 건다 (화면 너비 전체인 상단바 제외). 아크릴이 region 으로 잘리는지는 미확인 — 안 잘려도 지금보다 나빠지지 않음. [DWMWINDOWATTRIBUTE](https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute) |
| 설정 창 어두운 제목 표시줄 (DWMWA_USE_IMMERSIVE_DARK_MODE=20) | 동일 | 20 은 윈도우 10 20H1(19041)+ 에서 동작 (그 이전은 19 — 몽독은 19041 미만 설치 불가라 해당 없음). 실패는 무시. |
| Mica / DWMWA_SYSTEMBACKDROP_TYPE | 해당 없음 | 몽독은 쓰지 않는다. |
| 아이콘 글꼴 (Segoe Fluent Icons) | 대체 | 윈도우 10 에는 없어 `Segoe MDL2 Assets` 로 대체(이미 글꼴 목록에 있음). 윈도우 11 에서 새로 생긴 일부 글리프는 빈 칸으로 보일 수 있다. |
| 가상 데스크톱 | 동일 | 전환은 Ctrl+Win+←/→/D 키 입력(SendInput), 창 ↔ 데스크톱은 문서화된 IVirtualDesktopManager, 목록·현재 번호는 레지스트리(`VirtualDesktopIDs`, 윈도우 10 은 `SessionInfo\<세션>\VirtualDesktops\CurrentVirtualDesktop` 대체 경로까지 이미 처리). 빌드마다 IID 가 바뀌는 비공식 IVirtualDesktopManagerInternal 은 쓰지 않는다. |
| 한/영 표시·전환 (IMM32, VK_HANGUL) | 동일 | |
| 검색 (Spotlight) "Windows 검색에 넘기기" | 동일 | 포그라운드 판정에 윈도우 10 의 SearchApp(2004+)·SearchUI(1909 이하) 가 이미 들어 있다. |
| 파일 검색 (Windows Search 색인, Search.CollatorDSO) | 동일 | 윈도우 10 에 같은 OLE DB 공급자. 색인 서비스(WSearch)가 꺼져 있으면 파일 결과만 빠진다. |
| 블루투스 상태 (Windows.Devices.Radios) / 블루투스 오디오 연결 (KSPROPSETID_BtAudio) | 동일 | Radio API 는 윈도우 10 1507+, BtAudio KS 속성은 윈도우 10 드라이버 스택에서 지원 (ToothTray 등 윈도우 10 용 도구가 같은 방식). |
| 앱 메뉴 (상단바 메뉴 읽기) | 더 잘 됨 | 윈도우 10 의 메모장·그림판은 옛날식 Win32 메뉴라 표준 메뉴 경로로 읽힌다 (윈도우 11 새 메모장용 UIA 경로는 필요 없음). |
| 캡처 도구 창 내리기 (WindowNudger) | 동일 | 일반 규칙(작업 영역 위로 뜬 창)이라 윈도우 10 의 캡처 도구·캡처 및 스케치에도 같은 판정. |
| 앱 이름·아이콘 (shell:AppsFolder, AUMID) | 동일 | |

## 이번에 고친 것

- `Services/NotifyIconSettingsReader.cs`: 윈도우 10 이면 TrayNotify\IconStreams 를 읽어 "항상 표시" 를 따른다 (읽기만, 변경 감시도 그 키로).
- `Services/NativeToastSuppressor.cs`: 윈도우 10 에서 EVENT_OBJECT_SHOW 도 "새 토스트" 로 받고, 보이지 않는 창은 대기 상태로 판정.
- `Services/ShellActions.cs`: 알림 센터 열기를 윈도우 10 에서는 Win+A 로.
- `Services/DesktopWindowService.cs` + `Native/Desktop.cs`: 윈도우 10 블러 창에 SetWindowRgn 둥근 모서리 대체.

## 실제 윈도우 10 PC 에서 확인할 것

1. 트레이: 윈도우 설정 "작업 표시줄에 표시할 아이콘 선택" 에서 켠 앱이 상단바에, 끈 앱이 ⌃ 안에 가는지. 로그(`%APPDATA%\mongdock\logs`)의 "트레이 아이콘 배치: … (Windows, 윈도우 ID n)" 줄.
2. 알림: 알림이 왔을 때 윈도우 팝업이 숨고 몽독 배너만 보이는지, 로그의 "윈도우 기본 알림 팝업 숨김/그대로 둠". 팝업이 둘 다 보이면 Spy++/Inspect 로 토스트 창의 클래스·제목·프로세스를 알려 주기.
3. 독·스포트라이트·창 선택기 배경의 모서리가 둥근지, 아크릴 블러가 보이는지(검게 나오지 않는지), 독 확대 애니메이션이 끊기지 않는지.
4. 작업 표시줄 숨기기: "찾기" 검색 상자를 포함한 작업 표시줄이 사라지고 아래 빈 띠가 없는지, 몽독 종료 후 원래대로 돌아오는지.
5. 상단바의 알림 센터 / 빠른 설정 단추가 관리 센터를 여는지.
6. 아이콘 글리프가 빈 네모로 보이는 곳이 있는지.
