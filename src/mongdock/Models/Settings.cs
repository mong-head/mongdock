using System.Text.Json.Serialization;

namespace Mongdock.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DockEdge { Left, Right, Bottom, Top }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DockMode
{
    /// <summary>평소엔 숨어 있다가 마우스가 화면 가장자리에 닿으면 나타남. 공간 차지 없음. (MyDockFinder 방식)</summary>
    AutoHide,
    /// <summary>항상 보이고 창 위에 겹침. 공간 차지 없음.</summary>
    Overlay,
    /// <summary>AppBar 로 독의 기본 두께만큼 공간 예약 (확대된 아이콘은 창 위로 겹쳐 그림).</summary>
    Reserve,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MultiWindowClick { Picker, MostRecent }

/// <summary>독에서 앱을 켤 때 반응: Bounce = 아이콘이 통통 튐, Blink = 실행 점이 깜빡임.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LaunchAnimation { Bounce, Blink }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DockTheme { System, Light, Dark }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IconStyle
{
    /// <summary>맥 방식: 모든 아이콘을 같은 격자·여백·스퀴클(둥근 사각형)로 정규화, 모양이 안 맞는 아이콘은 스퀴클 판 위에 올림.</summary>
    Mac,
    /// <summary>원본 아이콘 그대로.</summary>
    Original,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SearchMode
{
    /// <summary>몽독 자체 검색창 (맥 Spotlight 처럼 화면 가운데). 앱 이름 검색 + Windows 검색/웹 검색으로 넘기기.</summary>
    Spotlight,
    /// <summary>윈도우 검색 (Win+S). 윈도우 10·왼쪽 정렬 작업 표시줄에서는 왼쪽 아래에 뜸.</summary>
    Windows,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpotlightHotkey
{
    /// <summary>자동: 입력 언어가 하나면 Win+Space, 여러 개면(Win+Space = 언어 전환) Alt+Space.</summary>
    Auto,
    /// <summary>Win+Space (맥 ⌘+Space 자리). 윈도우 입력 언어 전환과 겹침 — 몽독이 가로챔.</summary>
    WinSpace,
    /// <summary>Alt+Space. 창 메뉴(시스템 메뉴) 대신 Spotlight.</summary>
    AltSpace,
    /// <summary>Ctrl+Space.</summary>
    CtrlSpace,
    /// <summary>단축키 없음.</summary>
    None,
}

/// <summary>시계 달력에서 날짜를 두 번 누르거나 "캘린더에서 열기" 로 열 캘린더 (Services/CalendarApps).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalendarApp
{
    /// <summary>Google 캘린더 웹 (그 날 보기).</summary>
    Google,
    /// <summary>Outlook 웹 (outlook.live.com, 개인 계정, 그 날 보기).</summary>
    OutlookWeb,
    /// <summary>네이버 캘린더 웹 (날짜 지정 불가 — 기본 페이지).</summary>
    Naver,
    /// <summary>새 Outlook 데스크톱 (Microsoft.OutlookForWindows 패키지).</summary>
    NewOutlook,
    /// <summary>클래식 Outlook (outlook.exe, App Paths 로 감지).</summary>
    ClassicOutlook,
    /// <summary>윈도우 "메일 및 일정" (지원 종료 — 설치돼 있을 때만 표시).</summary>
    WindowsCalendar,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopBarColorMode
{
    /// <summary>완전 투명 — 바탕화면이 비침. 글자/아이콘 색은 그 아래 배경화면 밝기에 따라 검정/흰색 자동.</summary>
    Transparent,
    /// <summary>상단바 바로 아래 창의 색을 읽어 같은 색으로 칠함 (MyDockFinder autocolor 방식).</summary>
    Auto,
    /// <summary>Background 색 고정.</summary>
    Fixed,
    /// <summary>반투명 블러(아크릴). Background 는 틴트 색.</summary>
    Blur,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PinKind
{
    /// <summary>일반 exe. Target = exe 전체 경로.</summary>
    Exe,
    /// <summary>스토어/패키지 앱. Target = AUMID (예: Claude_pzs8sxrjxfjjc!Claude). shell:AppsFolder\AUMID 로 실행.</summary>
    Aumid,
    /// <summary>내장 동작. Target = "launchpad" 등.</summary>
    Special,
    /// <summary>구분선. Target 무시.</summary>
    Separator,
}

public sealed class PinItem
{
    public string Name { get; set; } = "";
    public PinKind Kind { get; set; } = PinKind.Exe;
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    /// <summary>커스텀 아이콘. 항상 %APPDATA%\mongdock\icons\ 안의 복사본 경로. null 이면 exe/패키지 아이콘 사용.</summary>
    public string? IconPath { get; set; }
}

public sealed class DockSettings
{
    /// <summary>독 표시 (트레이·로고 메뉴에서 켜고 끔).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>창이 여러 개인 앱 아이콘 클릭: Picker = 창 선택 패널(미리보기), MostRecent = 가장 최근 창으로 바로.</summary>
    public MultiWindowClick MultiWindowClick { get; set; } = MultiWindowClick.Picker;
    /// <summary>독에서 앱을 켤 때(창이 뜰 때까지, 최대 10초) 보여 줄 반응. 알림은 어느 쪽이든 튀지 않는다.</summary>
    public LaunchAnimation LaunchAnimation { get; set; } = LaunchAnimation.Bounce;
    /// <summary>다른 가상 데스크톱의 창도 독에 표시(실행 중 점, 창 선택에 "데스크톱 N").</summary>
    public bool ShowWindowsFromAllDesktops { get; set; } = true;
    public DockEdge Edge { get; set; } = DockEdge.Bottom;
    public DockMode Mode { get; set; } = DockMode.Reserve;
    /// <summary>자동 숨김에서 마우스가 독을 벗어난 뒤 숨기까지 지연 (ms).</summary>
    public int AutoHideDelayMs { get; set; } = 500;
    public DockTheme Theme { get; set; } = DockTheme.System;
    /// <summary>배경 블러(아크릴, 반투명 유리). false 면 반투명 단색.</summary>
    public bool Blur { get; set; } = true;
    public IconStyle IconStyle { get; set; } = IconStyle.Mac;
    /// <summary>맥처럼 커서 주변 이웃 아이콘도 거리에 따라 같이 커짐. false 면 커서 아래 아이콘만.</summary>
    public bool WaveMagnification { get; set; } = true;
    /// <summary>가장자리를 따라 독 중심의 위치 (0=시작, 0.5=가운데, 1=끝). 독을 드래그하면 바뀜.</summary>
    public double Offset { get; set; } = 0.5;
    /// <summary>아이콘 크기 (DIP).</summary>
    public double IconSize { get; set; } = 52;
    public double IconSpacing { get; set; } = 5;
    /// <summary>마우스 오버 확대 배율. 1.0 이면 확대 없음.</summary>
    public double HoverScale { get; set; } = 1.8;
    // 아래 색들은 "" 이면 Theme 기본값 사용. "#AARRGGBB" 로 지정하면 그 색.
    public string Background { get; set; } = "";
    public string BorderColor { get; set; } = "";
    public string IndicatorColor { get; set; } = "";
    public string NotificationColor { get; set; } = "#FFFF453A";
    public double CornerRadius { get; set; } = 16;
    /// <summary>화면 가장자리와 독 사이 여백 (DIP).</summary>
    public double Margin { get; set; } = 10;
    public bool ShowRunningApps { get; set; } = true;
    /// <summary>독을 둘 모니터의 장치 이름 (예 "\.\DISPLAY2"). "" 또는 연결 안 된 모니터면 주 모니터.</summary>
    public string Monitor { get; set; } = "";
}

public sealed class TopBarSettings
{
    public bool Enabled { get; set; } = true;
    public double Height { get; set; } = 26;
    /// <summary>기본 Auto(앱 색에 맞춤). 저장된 colorMode 는 그대로 따름 (이관 없음).</summary>
    public TopBarColorMode ColorMode { get; set; } = TopBarColorMode.Auto;
    /// <summary>Fixed 모드의 배경색 / Blur 모드의 틴트.</summary>
    public string Background { get; set; } = "#FFFFFFFF";
    /// <summary>"" 이면 배경 밝기에 따라 검정/흰색 자동.</summary>
    public string Foreground { get; set; } = "";
    public double FontSize { get; set; } = 13;
    public string ClockFormat { get; set; } = "ddd tt h:mm";
    public bool ShowDesktopButtons { get; set; } = true;
    /// <summary>앱 이름 오른쪽에 그 앱의 메뉴(파일·편집·보기…) 표시 (맥 메뉴바처럼).</summary>
    public bool ShowAppMenus { get; set; } = true;
    /// <summary>
    /// 실험(기본 꺼짐): 메모장(클래식)·그림판(클래식)·레지스트리 편집기·시스템 정보 같은 표준 Win32 메뉴 앱의
    /// 창 안 메뉴 줄을 떼어 내고 상단바에서만 보이게. 끄거나 일시 정지·종료하면 되돌림 (NativeMenuHider).
    /// </summary>
    public bool HideNativeMenuBars { get; set; }
    /// <summary>왼쪽 로고 버튼 (클릭 시 mongdock 메뉴: 시작 메뉴, 설정, 종료 등).</summary>
    public bool ShowLogo { get; set; } = true;
    /// <summary>포그라운드 앱 이름 표시 (맥 메뉴바처럼 굵게).</summary>
    public bool ShowActiveAppName { get; set; } = true;
    public bool ShowImeToggle { get; set; } = true;
    /// <summary>오른쪽 빠른 버튼: 검색(Win+S), 빠른 설정(Win+A), 알림 센터(Win+N) — 원격에서 단축키 대신 클릭.</summary>
    public bool ShowQuickButtons { get; set; } = true;
    /// <summary>와이파이·블루투스·볼륨 아이콘 (MyDockFinder 처럼). 클릭 시 작은 패널(볼륨 슬라이더, 블루투스 토글, 설정 열기).</summary>
    public bool ShowStatusIcons { get; set; } = true;
    /// <summary>
    /// 다른 앱의 트레이(알림 영역) 아이콘 — 카카오톡·디스코드 등 (작업 표시줄을 숨겨도 접근 가능하게).
    /// 켜져 있으면 몽독이 트레이 메시지를 먼저 받아 explorer 로 그대로 전달한다 (TrayIconService).
    /// 기본 꺼짐. 사용자가 직접 정한 적 없으면(TrayIconsUserSet=false) "작업 표시줄 숨기기" 를 켤 때 자동으로 켜짐 (Settings.SetHideWindowsTaskbar).
    /// </summary>
    public bool ShowTrayIcons { get; set; }
    /// <summary>ShowTrayIcons 를 사용자가 설정 창/메뉴에서 직접 바꾼 적 있는지. true 면 작업 표시줄 숨기기와 연동하지 않음.</summary>
    public bool TrayIconsUserSet { get; set; }

    /// <summary>사용자가 UI 에서 "앱 트레이 아이콘" 을 바꿀 때 (사용자 지정으로 기록).</summary>
    public void SetShowTrayIconsByUser(bool on)
    {
        ShowTrayIcons = on;
        TrayIconsUserSet = true;
    }
    /// <summary>
    /// 상단바에 바로 보일 트레이 아이콘의 <b>최대</b> 개수. 어느 아이콘이 바에 갈지는 TrayIconPlacement(몽독에서 옮김) &gt;
    /// 윈도우 설정("작업 표시줄에 항상 표시") &gt; 기본(⌃ 안) 순서로 정하고, 이 수를 넘으면 순서 뒤쪽부터 ⌃ 로.
    /// </summary>
    public int TrayIconsVisibleCount { get; set; } = 10;
    /// <summary>
    /// 몽독에서 직접 옮긴 트레이 아이콘 자리 (윈도우 설정보다 우선). 키 = GUID("d" 형식 소문자) 또는 "exe 파일 이름 소문자:uID".
    /// </summary>
    public Dictionary<string, TrayIconPlacement> TrayIconPlacement { get; set; } = new();
    /// <summary>네트워크 업/다운 속도 (2줄 작은 글씨).</summary>
    public bool ShowNetworkSpeed { get; set; } = false;
    public bool ReserveSpace { get; set; } = true;
    /// <summary>
    /// 작업 영역을 무시하고 모니터 맨 위에 뜨는 창(캡처 도구 등)이나 상단바 밑으로 끌어 놓은 창을 상단바 바로 아래로 내림 (맥처럼).
    /// 상단바가 켜져 있고 ReserveSpace 일 때만 동작 (WindowNudger).
    /// </summary>
    public bool KeepWindowsBelowBar { get; set; } = true;
    /// <summary>모든 모니터에 상단바 표시 (맥처럼). false 면 주 모니터에만.</summary>
    public bool ShowOnAllMonitors { get; set; } = true;
    /// <summary>검색 버튼 동작.</summary>
    public SearchMode SearchMode { get; set; } = SearchMode.Spotlight;
    /// <summary>Spotlight 검색창을 여는 전역 단축키 (맥 ⌘+Space 처럼). None 이면 끔.</summary>
    public SpotlightHotkey SpotlightHotkey { get; set; } = SpotlightHotkey.Auto;
    /// <summary>시계 달력에서 날짜를 열 캘린더. 고른 데스크톱 앱이 지워졌으면 Google 웹으로 대신 엶.</summary>
    public CalendarApp CalendarApp { get; set; } = CalendarApp.Google;
}

/// <summary>트레이 아이콘 하나의 몽독 저장 자리.</summary>
public sealed class TrayIconPlacement
{
    /// <summary>true = 상단바, false = ⌃ 안.</summary>
    public bool OnBar { get; set; }
    /// <summary>바(또는 ⌃) 안의 순서 (작을수록 왼쪽).</summary>
    public int Order { get; set; }
}

public sealed class NotificationSettings
{
    /// <summary>윈도우 알림이 오면 맥처럼 상단바 아래 오른쪽에 몽독 배너를 띄움 (윈도우 알림 DB 를 읽기만 함).</summary>
    public bool ShowNotificationBanners { get; set; } = true;

    /// <summary>
    /// 윈도우 기본 알림 팝업(오른쪽 아래 토스트)을 화면 밖으로 옮겨 숨김 → 몽독 배너만 보임. 알림 기록은 그대로 남음.
    /// ShowNotificationBanners 가 켜져 있고 일시 정지가 아닐 때만 동작 (NativeToastSuppressor).
    /// 방해 금지(DND)·레지스트리(ShowBanner/NOC_GLOBAL_SETTING_TOASTS_ENABLED)는 쓰지 않음: 실시간 반영이 안 되거나 알림 자체가 꺼짐.
    /// </summary>
    public bool HideWindowsToastPopups { get; set; } = true;

    /// <summary>
    /// 사용자가 고른 윈도우 알림 소리(원본 wav 경로, "" = 무음). null = 몽독이 손대지 않음.
    /// 실제로는 이 파일 내용을 %APPDATA%\mongdock\sounds\notification.wav 로 복사하고 레지스트리가 그 파일을 가리킴 (NotificationSoundService).
    /// </summary>
    public string? Sound { get; set; }

    /// <summary>몽독이 처음 알림 소리를 바꾸기 전의 레지스트리 값(원본 그대로, "" = 무음). 한 번만 저장. null = 아직 바꾼 적 없음.</summary>
    public string? OriginalSound { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebSearchEngine { Google, Naver, Bing }

/// <summary>Spotlight 검색창의 검색 항목 (설정 창 "검색" 페이지).</summary>
public sealed class SearchSettings
{
    /// <summary>응용 프로그램 (시작 메뉴의 모든 앱).</summary>
    public bool Apps { get; set; } = true;
    /// <summary>검색창을 열면(검색어 없음) 최근 실행한 앱을 "최근 사용" 으로 보여 줌. 꺼도 검색 순위 가산은 유지.</summary>
    public bool ShowRecents { get; set; } = true;
    /// <summary>윈도우 설정 페이지 (ms-settings:).</summary>
    public bool Settings { get; set; } = true;
    /// <summary>수식을 입력하면 맨 위에 계산 결과.</summary>
    public bool Calculator { get; set; } = true;
    // 아래 넷은 윈도우 검색 색인(Windows Search)에서 파일 이름으로 찾음
    public bool Folders { get; set; } = true;
    public bool Documents { get; set; } = true;
    /// <summary>사진·동영상·음악.</summary>
    public bool Media { get; set; } = true;
    public bool OtherFiles { get; set; } = true;
    /// <summary>결과 맨 아래 "웹에서 검색".</summary>
    public bool WebSearch { get; set; } = true;
    /// <summary>결과 맨 아래 "Windows 검색에서 찾기".</summary>
    public bool WindowsSearch { get; set; } = true;
    /// <summary>파일·폴더를 찾을 위치 (하위 폴더 포함). 기본: 사용자 프로필 폴더.</summary>
    public List<string> FileSearchFolders { get; set; } = new() { DefaultFileSearchFolder };
    /// <summary>카테고리마다 보여 줄 최대 개수 (3~10).</summary>
    public int MaxPerCategory { get; set; } = 5;
    public WebSearchEngine WebSearchEngine { get; set; } = WebSearchEngine.Google;

    public static string DefaultFileSearchFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>
/// 시계 달력에 겹쳐 보일 iCal(ICS) 구독 일정 (설정 창 "캘린더" 페이지).
/// 구독 목록(이름·주소·색)은 비밀 링크가 들어 있어 settings.json 이 아니라 %APPDATA%\mongdock\calendars.json 에
/// 주소를 DPAPI(현재 사용자)로 암호화해 따로 저장한다 (Services/CalendarFeedService). 여기에는 주기만.
/// </summary>
public sealed class CalendarSettings
{
    /// <summary>구독 캘린더 새로고침 주기 (분, 5~1440). 몽독 일시 정지 중엔 멈춤.</summary>
    public int RefreshMinutes { get; set; } = 15;
}

public sealed class Settings
{
    /// <summary>settings.json 형식 버전. 이관은 파일에 적힌 버전이 이보다 낮을 때만 한 번 (SettingsService.Migrate).</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// 이 파일이 어느 형식까지 이관됐는지. 키가 없는 옛 파일은 0 으로 본다 (원본 JSON 으로 판단 — 속성 기본값과 무관).
    /// 1 이하: 옛 기본 색·상단바 32/14 를 새 기본값으로 바꾼 적 없음.
    /// 2: topBar.showTrayIcons 기본값이 true 였음 (3 부터 false — 키 없이 작업 표시줄을 숨기던 사용자는 true 로 이관). 3: 현재.
    /// </summary>
    public int SettingsVersion { get; set; } = CurrentVersion;
    public DockSettings Dock { get; set; } = new();
    public TopBarSettings TopBar { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    /// <summary>Spotlight 검색 항목·파일 검색 위치·웹 검색 엔진.</summary>
    public SearchSettings Search { get; set; } = new();
    /// <summary>시계 달력의 구독 일정 (새로고침 주기). 구독 목록은 calendars.json.</summary>
    public CalendarSettings Calendar { get; set; } = new();
    public List<PinItem> Pins { get; set; } = new();
    /// <summary>상단바·메뉴·패널·독 말풍선 글꼴. "Pretendard" = 앱에 내장된 Pretendard(맥 느낌). 설치된 글꼴 이름을 쓰면 그 글꼴. 쉼표로 대체 글꼴 나열 가능.</summary>
    public string FontFamily { get; set; } = "Pretendard";
    /// <summary>
    /// mongdock 이 켜져 있는 동안 윈도우 작업 표시줄 숨김 (숨기는 동안 작업 표시줄 자동 숨김을 켜서 빈 띠가 안 남게).
    /// 일시 정지·종료·크래시 시 원래대로 복원. 속성 기본값은 false(키 없는 기존 파일 호환) — 새 설치는 SettingsService 가 켬.
    /// </summary>
    public bool HideWindowsTaskbar { get; set; }

    /// <summary>
    /// UI 에서 "윈도우 작업 표시줄 숨기기" 를 바꿀 때. 켤 때 앱 트레이 아이콘을 사용자가 정한 적 없으면 같이 켬
    /// (작업 표시줄이 없으면 트레이 아이콘에 접근할 곳이 상단바뿐이므로).
    /// </summary>
    public void SetHideWindowsTaskbar(bool on)
    {
        // 처음 켤 때 작업 표시줄 고정 앱을 독에 가져오도록 요청 (SettingsService.Save 가 처리)
        if (on && !HideWindowsTaskbar && !TaskbarPinsImported) TaskbarPinImportRequested = true;
        HideWindowsTaskbar = on;
        if (on && !TopBar.TrayIconsUserSet) TopBar.ShowTrayIcons = true;
    }
    /// <summary>
    /// 윈도우 작업 표시줄 고정 앱을 독에 한 번 가져왔는지 (새 설치 시, 또는 "작업 표시줄 숨기기" 를 처음 켤 때).
    /// true 면 자동으로 다시 가져오지 않음 (설정 → 독 → "작업 표시줄 고정 앱 가져오기" 로는 언제든).
    /// </summary>
    public bool TaskbarPinsImported { get; set; }
    /// <summary>SetHideWindowsTaskbar 가 처음 켤 때 세움 → 다음 저장 때 작업 표시줄 고정 앱 가져오기 (저장 안 함).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TaskbarPinImportRequested { get; set; }
    public bool StartWithWindows { get; set; }
    /// <summary>MyDockFinder ico.ini 를 한 번 가져왔는지. true 면 다시 가져오지 않음.</summary>
    public bool ImportedFromMyDockFinder { get; set; }
    /// <summary>
    /// "새로운 기능"/첫 둘러보기를 마지막으로 본(또는 건너뛴) 몽독 버전 (예 "0.3.0"). null = 아직 없음
    /// (새 설치면 첫 둘러보기, 기존 설정 파일이면 0.2.0 에서 올라온 것으로 봄 — Views/CoachMarks).
    /// </summary>
    public string? LastSeenVersion { get; set; }
    /// <summary>
    /// 첫 설치 둘러보기를 아직 끝까지 보지(또는 건너뛰지) 않음. 첫 설치 때 true, 둘러보기 완료·건너뛰기 때 false.
    /// 일시 정지·전체 화면·종료로 중간에 닫히면 true 로 남아 다음 실행에서 다시 둘러보기 (Views/CoachMarks).
    /// </summary>
    public bool FirstRunTourPending { get; set; }
    /// <summary>GitHub 릴리스에서 새 버전 자동 확인 (시작 1분 뒤 + 12시간마다, Services/UpdateService).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>"이 버전 건너뛰기" 한 버전 (예 "0.3.1"). 자동 확인은 이 버전을 알리지 않음 — 더 새 버전이 나오면 다시 알림.</summary>
    public string? SkippedUpdateVersion { get; set; }
    /// <summary>"업데이트 있음" 배너를 이미 띄운 버전 (같은 버전은 한 번만 배너).</summary>
    public string? NotifiedUpdateVersion { get; set; }
    /// <summary>사용자 정의 앱 메뉴. 키 = exe 파일명(소문자, 예 "chrome.exe") 또는 AUMID. 있으면 기본 메뉴 대신 사용.</summary>
    public Dictionary<string, List<AppMenuDef>> AppMenus { get; set; } = new();
    /// <summary>
    /// 앱 전용 메뉴 규칙(menus/app-menus.json)을 GitHub 에서 하루 한 번 받아 갱신할지 (Services/MenuRulesService).
    /// false 면 내장(또는 이전에 받아 둔 캐시) 규칙만 쓴다. 설정 창에는 없음.
    /// </summary>
    public bool UpdateMenuRules { get; set; } = true;
}
