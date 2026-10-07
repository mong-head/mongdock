using System.Text.Json.Serialization;

namespace MyDock.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DockEdge { Left, Right, Bottom, Top }

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
    /// <summary>커스텀 아이콘. 항상 %APPDATA%\MyDock\icons\ 안의 복사본 경로. null 이면 exe/패키지 아이콘 사용.</summary>
    public string? IconPath { get; set; }
}

public sealed class DockSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Right;
    /// <summary>가장자리를 따라 독 중심의 위치 (0=시작, 0.5=가운데, 1=끝). 독을 드래그하면 바뀜.</summary>
    public double Offset { get; set; } = 0.5;
    /// <summary>아이콘 크기 (DIP).</summary>
    public double IconSize { get; set; } = 52;
    public double IconSpacing { get; set; } = 5;
    /// <summary>마우스 오버 확대 배율. 1.0 이면 확대 없음.</summary>
    public double HoverScale { get; set; } = 1.36;
    public string Background { get; set; } = "#B0202024";
    public string BorderColor { get; set; } = "#40FFFFFF";
    public string IndicatorColor { get; set; } = "#E0FFFFFF";
    public string NotificationColor { get; set; } = "#FFFF453A";
    public double CornerRadius { get; set; } = 16;
    /// <summary>화면 가장자리와 독 사이 여백 (DIP).</summary>
    public double Margin { get; set; } = 4;
    /// <summary>AppBar 로 등록해서 최대화 창이 독을 가리지 않게 함.</summary>
    public bool ReserveSpace { get; set; } = true;
    public bool ShowRunningApps { get; set; } = true;
}

public sealed class TopBarSettings
{
    public bool Enabled { get; set; } = true;
    public double Height { get; set; } = 28;
    public string Background { get; set; } = "#C0161618";
    public string Foreground { get; set; } = "#FFF2F2F2";
    public double FontSize { get; set; } = 13;
    public string ClockFormat { get; set; } = "ddd tt h:mm";
    public bool ShowDesktopButtons { get; set; } = true;
    /// <summary>왼쪽 로고 버튼 (클릭 시 MyDock 메뉴: 시작 메뉴, 설정, 종료 등).</summary>
    public bool ShowLogo { get; set; } = true;
    /// <summary>포그라운드 앱 이름 표시 (맥 메뉴바처럼 굵게).</summary>
    public bool ShowActiveAppName { get; set; } = true;
    public bool ShowImeToggle { get; set; } = true;
    /// <summary>오른쪽 빠른 버튼: 검색(Win+S), 빠른 설정(Win+A), 알림 센터(Win+N) — 원격에서 단축키 대신 클릭.</summary>
    public bool ShowQuickButtons { get; set; } = true;
    public bool ReserveSpace { get; set; } = true;
}

public sealed class Settings
{
    public DockSettings Dock { get; set; } = new();
    public TopBarSettings TopBar { get; set; } = new();
    public List<PinItem> Pins { get; set; } = new();
    public bool StartWithWindows { get; set; }
    /// <summary>MyDockFinder ico.ini 를 한 번 가져왔는지. true 면 다시 가져오지 않음.</summary>
    public bool ImportedFromMyDockFinder { get; set; }
}
