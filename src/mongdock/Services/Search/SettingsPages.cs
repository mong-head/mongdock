namespace Mongdock.Services.Search;

/// <summary>윈도우 설정 페이지 하나: 표시 이름, 검색 별칭(한/영), ms-settings: URI, 아이콘 글리프(Segoe Fluent Icons).</summary>
public sealed record SettingsPage(string Name, string Uri, string Glyph, params string[] Aliases);

/// <summary>
/// Spotlight "시스템 설정" 검색 대상. URI 는 마이크로소프트 문서("Launch the Windows Settings app")에 있는 것만 —
/// Windows 10/11 모두 열리는 것 위주 (11 에서 위치가 바뀐 페이지는 윈도우가 알아서 새 위치로 연다).
/// </summary>
public static class SettingsPages
{
    public static readonly IReadOnlyList<SettingsPage> All = new SettingsPage[]
    {
        new(Loc.T("설정"), "ms-settings:", "", Loc.T("윈도우 설정"), Loc.T("제어판"), "settings"),
        new(Loc.T("블루투스 및 장치"), "ms-settings:bluetooth", "", Loc.T("블루투스"), Loc.T("장치"), Loc.T("이어폰"), Loc.T("헤드셋"), "bluetooth"),
        new(Loc.T("디스플레이"), "ms-settings:display", "", Loc.T("화면"), Loc.T("해상도"), Loc.T("배율"), Loc.T("모니터"), Loc.T("밝기"), "display"),
        new(Loc.T("야간 모드"), "ms-settings:nightlight", "", Loc.T("야간 조명"), Loc.T("블루라이트"), "night light"),
        new(Loc.T("소리"), "ms-settings:sound", "", Loc.T("사운드"), Loc.T("볼륨"), Loc.T("스피커"), Loc.T("마이크"), Loc.T("오디오"), "sound"),
        new("Wi-Fi", "ms-settings:network-wifi", "", Loc.T("와이파이"), Loc.T("무선"), "wifi"),
        new(Loc.T("네트워크 및 인터넷"), "ms-settings:network-status", "", Loc.T("네트워크"), Loc.T("인터넷"), Loc.T("이더넷"), "network"),
        new("VPN", "ms-settings:network-vpn", "", Loc.T("가상 사설망"), "vpn"),
        new(Loc.T("비행기 모드"), "ms-settings:network-airplanemode", "", "airplane"),
        new(Loc.T("모바일 핫스팟"), "ms-settings:network-mobilehotspot", "", Loc.T("핫스팟"), Loc.T("테더링"), "hotspot"),
        new(Loc.T("프록시"), "ms-settings:network-proxy", "", "proxy"),
        new(Loc.T("개인 설정"), "ms-settings:personalization", "", Loc.T("꾸미기"), "personalization"),
        new(Loc.T("배경"), "ms-settings:personalization-background", "", Loc.T("배경화면"), Loc.T("바탕화면"), Loc.T("월페이퍼"), "wallpaper", "background"),
        new(Loc.T("색"), "ms-settings:personalization-colors", "", Loc.T("다크 모드"), Loc.T("어두운 모드"), Loc.T("테마 색"), Loc.T("강조색"), "colors", "dark mode"),
        new(Loc.T("테마"), "ms-settings:themes", "", "theme"),
        new(Loc.T("잠금 화면"), "ms-settings:lockscreen", "", "lock screen"),
        new(Loc.T("작업 표시줄"), "ms-settings:taskbar", "", Loc.T("태스크바"), "taskbar"),
        new(Loc.T("시작 메뉴"), "ms-settings:personalization-start", "", Loc.T("시작"), "start"),
        new(Loc.T("알림"), "ms-settings:notifications", "", "notifications"),
        new(Loc.T("방해 금지·집중"), "ms-settings:quiethours", "", Loc.T("방해 금지"), Loc.T("집중 지원"), Loc.T("집중"), Loc.T("포커스"), "focus", "do not disturb"),
        new(Loc.T("전원 및 절전"), "ms-settings:powersleep", "", Loc.T("전원"), Loc.T("절전"), Loc.T("화면 끄기"), Loc.T("잠자기"), "power", "sleep"),
        new(Loc.T("배터리"), "ms-settings:batterysaver", "", Loc.T("배터리 절약"), "battery"),
        new(Loc.T("저장소"), "ms-settings:storagesense", "", Loc.T("저장 공간"), Loc.T("디스크"), Loc.T("용량"), Loc.T("저장소 센스"), "storage"),
        new(Loc.T("설치된 앱"), "ms-settings:appsfeatures", "", Loc.T("앱"), Loc.T("앱 및 기능"), Loc.T("프로그램 제거"), Loc.T("삭제"), Loc.T("언인스톨"), "apps"),
        new(Loc.T("시작 프로그램"), "ms-settings:startupapps", "", Loc.T("시작 앱"), Loc.T("자동 실행"), "startup"),
        new(Loc.T("기본 앱"), "ms-settings:defaultapps", "", Loc.T("기본 프로그램"), Loc.T("기본 브라우저"), Loc.T("연결 프로그램"), "default apps"),
        new(Loc.T("Windows 업데이트"), "ms-settings:windowsupdate", "", Loc.T("업데이트"), Loc.T("윈도우 업데이트"), "update"),
        new(Loc.T("날짜 및 시간"), "ms-settings:dateandtime", "", Loc.T("날짜"), Loc.T("시간"), Loc.T("시계"), Loc.T("시간대"), "date", "time"),
        new(Loc.T("언어 및 지역"), "ms-settings:regionlanguage", "", Loc.T("언어"), Loc.T("한국어"), Loc.T("지역"), "language", "region"),
        new(Loc.T("입력"), "ms-settings:typing", "", Loc.T("키보드"), Loc.T("입력기"), Loc.T("자동 고침"), Loc.T("한영"), "typing", "keyboard"),
        new(Loc.T("마우스"), "ms-settings:mousetouchpad", "", Loc.T("포인터"), Loc.T("스크롤"), "mouse"),
        new(Loc.T("터치패드"), "ms-settings:devices-touchpad", "", Loc.T("트랙패드"), "touchpad"),
        new(Loc.T("프린터 및 스캐너"), "ms-settings:printers", "", Loc.T("프린터"), Loc.T("스캐너"), Loc.T("인쇄"), "printer"),
        new(Loc.T("계정 정보"), "ms-settings:yourinfo", "", Loc.T("계정"), Loc.T("내 정보"), "account"),
        new(Loc.T("로그인 옵션"), "ms-settings:signinoptions", "", Loc.T("비밀번호"), Loc.T("암호"), "PIN", Loc.T("지문"), Loc.T("얼굴 인식"), "Windows Hello", "sign-in"),
        new(Loc.T("가족 및 다른 사용자"), "ms-settings:otherusers", "", Loc.T("다른 사용자"), Loc.T("사용자 추가"), "users"),
        new(Loc.T("개인 정보 및 보안"), "ms-settings:privacy", "", Loc.T("개인 정보"), Loc.T("보안"), Loc.T("권한"), "privacy"),
        new(Loc.T("카메라 권한"), "ms-settings:privacy-webcam", "", Loc.T("카메라"), Loc.T("웹캠"), "camera"),
        new(Loc.T("마이크 권한"), "ms-settings:privacy-microphone", "", Loc.T("마이크 접근"), "microphone"),
        new(Loc.T("위치"), "ms-settings:privacy-location", "", Loc.T("위치 서비스"), "GPS", "location"),
        new(Loc.T("Windows 보안"), "ms-settings:windowsdefender", "", Loc.T("백신"), Loc.T("디펜더"), Loc.T("바이러스"), Loc.T("방화벽"), "defender", "security"),
        new(Loc.T("멀티태스킹"), "ms-settings:multitasking", "", Loc.T("창 끌기"), Loc.T("스냅"), Loc.T("가상 데스크톱"), "snap", "multitasking"),
        new(Loc.T("클립보드"), "ms-settings:clipboard", "", Loc.T("클립보드 기록"), "clipboard"),
        new(Loc.T("글꼴"), "ms-settings:fonts", "", Loc.T("폰트"), "fonts"),
        new(Loc.T("텍스트 크기"), "ms-settings:easeofaccess-display", "", Loc.T("접근성"), Loc.T("글자 크기"), Loc.T("글씨 크기"), "text size"),
        new(Loc.T("게임 모드"), "ms-settings:gaming-gamemode", "", Loc.T("게임"), "game mode"),
        new(Loc.T("복구"), "ms-settings:recovery", "", Loc.T("초기화"), Loc.T("PC 초기화"), "recovery", "reset"),
        new(Loc.T("문제 해결"), "ms-settings:troubleshoot", "", Loc.T("문제 해결사"), "troubleshoot"),
        new(Loc.T("정보"), "ms-settings:about", "", Loc.T("PC 이름"), Loc.T("사양"), Loc.T("시스템 정보"), Loc.T("윈도우 버전"), "about"),
    };
}
