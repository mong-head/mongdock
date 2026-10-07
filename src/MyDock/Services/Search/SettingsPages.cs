namespace MyDock.Services.Search;

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
        new("설정", "ms-settings:", "", "윈도우 설정", "제어판", "settings"),
        new("블루투스 및 장치", "ms-settings:bluetooth", "", "블루투스", "장치", "이어폰", "헤드셋", "bluetooth"),
        new("디스플레이", "ms-settings:display", "", "화면", "해상도", "배율", "모니터", "밝기", "display"),
        new("야간 모드", "ms-settings:nightlight", "", "야간 조명", "블루라이트", "night light"),
        new("소리", "ms-settings:sound", "", "사운드", "볼륨", "스피커", "마이크", "오디오", "sound"),
        new("Wi-Fi", "ms-settings:network-wifi", "", "와이파이", "무선", "wifi"),
        new("네트워크 및 인터넷", "ms-settings:network-status", "", "네트워크", "인터넷", "이더넷", "network"),
        new("VPN", "ms-settings:network-vpn", "", "가상 사설망", "vpn"),
        new("비행기 모드", "ms-settings:network-airplanemode", "", "airplane"),
        new("모바일 핫스팟", "ms-settings:network-mobilehotspot", "", "핫스팟", "테더링", "hotspot"),
        new("프록시", "ms-settings:network-proxy", "", "proxy"),
        new("개인 설정", "ms-settings:personalization", "", "꾸미기", "personalization"),
        new("배경", "ms-settings:personalization-background", "", "배경화면", "바탕화면", "월페이퍼", "wallpaper", "background"),
        new("색", "ms-settings:personalization-colors", "", "다크 모드", "어두운 모드", "테마 색", "강조색", "colors", "dark mode"),
        new("테마", "ms-settings:themes", "", "theme"),
        new("잠금 화면", "ms-settings:lockscreen", "", "lock screen"),
        new("작업 표시줄", "ms-settings:taskbar", "", "태스크바", "taskbar"),
        new("시작 메뉴", "ms-settings:personalization-start", "", "시작", "start"),
        new("알림", "ms-settings:notifications", "", "notifications"),
        new("방해 금지·집중", "ms-settings:quiethours", "", "방해 금지", "집중 지원", "집중", "포커스", "focus", "do not disturb"),
        new("전원 및 절전", "ms-settings:powersleep", "", "전원", "절전", "화면 끄기", "잠자기", "power", "sleep"),
        new("배터리", "ms-settings:batterysaver", "", "배터리 절약", "battery"),
        new("저장소", "ms-settings:storagesense", "", "저장 공간", "디스크", "용량", "저장소 센스", "storage"),
        new("설치된 앱", "ms-settings:appsfeatures", "", "앱", "앱 및 기능", "프로그램 제거", "삭제", "언인스톨", "apps"),
        new("시작 프로그램", "ms-settings:startupapps", "", "시작 앱", "자동 실행", "startup"),
        new("기본 앱", "ms-settings:defaultapps", "", "기본 프로그램", "기본 브라우저", "연결 프로그램", "default apps"),
        new("Windows 업데이트", "ms-settings:windowsupdate", "", "업데이트", "윈도우 업데이트", "update"),
        new("날짜 및 시간", "ms-settings:dateandtime", "", "날짜", "시간", "시계", "시간대", "date", "time"),
        new("언어 및 지역", "ms-settings:regionlanguage", "", "언어", "한국어", "지역", "language", "region"),
        new("입력", "ms-settings:typing", "", "키보드", "입력기", "자동 고침", "한영", "typing", "keyboard"),
        new("마우스", "ms-settings:mousetouchpad", "", "포인터", "스크롤", "mouse"),
        new("터치패드", "ms-settings:devices-touchpad", "", "트랙패드", "touchpad"),
        new("프린터 및 스캐너", "ms-settings:printers", "", "프린터", "스캐너", "인쇄", "printer"),
        new("계정 정보", "ms-settings:yourinfo", "", "계정", "내 정보", "account"),
        new("로그인 옵션", "ms-settings:signinoptions", "", "비밀번호", "암호", "PIN", "지문", "얼굴 인식", "Windows Hello", "sign-in"),
        new("가족 및 다른 사용자", "ms-settings:otherusers", "", "다른 사용자", "사용자 추가", "users"),
        new("개인 정보 및 보안", "ms-settings:privacy", "", "개인 정보", "보안", "권한", "privacy"),
        new("카메라 권한", "ms-settings:privacy-webcam", "", "카메라", "웹캠", "camera"),
        new("마이크 권한", "ms-settings:privacy-microphone", "", "마이크 접근", "microphone"),
        new("위치", "ms-settings:privacy-location", "", "위치 서비스", "GPS", "location"),
        new("Windows 보안", "ms-settings:windowsdefender", "", "백신", "디펜더", "바이러스", "방화벽", "defender", "security"),
        new("멀티태스킹", "ms-settings:multitasking", "", "창 끌기", "스냅", "가상 데스크톱", "snap", "multitasking"),
        new("클립보드", "ms-settings:clipboard", "", "클립보드 기록", "clipboard"),
        new("글꼴", "ms-settings:fonts", "", "폰트", "fonts"),
        new("텍스트 크기", "ms-settings:easeofaccess-display", "", "접근성", "글자 크기", "글씨 크기", "text size"),
        new("게임 모드", "ms-settings:gaming-gamemode", "", "게임", "game mode"),
        new("복구", "ms-settings:recovery", "", "초기화", "PC 초기화", "recovery", "reset"),
        new("문제 해결", "ms-settings:troubleshoot", "", "문제 해결사", "troubleshoot"),
        new("정보", "ms-settings:about", "", "PC 이름", "사양", "시스템 정보", "윈도우 버전", "about"),
    };
}
