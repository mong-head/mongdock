using System.Text.Json.Serialization;

namespace MyDock.Models;

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
    /// <summary>커스텀 아이콘. 항상 %APPDATA%\MyDock\icons\ 안의 복사본 경로. null 이면 exe/패키지 아이콘 사용.</summary>
    public string? IconPath { get; set; }
}

public sealed class DockSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Right;
    public DockMode Mode { get; set; } = DockMode.AutoHide;
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
    public double HoverScale { get; set; } = 1.36;
    // 아래 색들은 "" 이면 Theme 기본값 사용. "#AARRGGBB" 로 지정하면 그 색.
    public string Background { get; set; } = "";
    public string BorderColor { get; set; } = "";
    public string IndicatorColor { get; set; } = "";
    public string NotificationColor { get; set; } = "#FFFF453A";
    public double CornerRadius { get; set; } = 16;
    /// <summary>화면 가장자리와 독 사이 여백 (DIP).</summary>
    public double Margin { get; set; } = 4;
    public bool ShowRunningApps { get; set; } = true;
}

public sealed class TopBarSettings
{
    public bool Enabled { get; set; } = true;
    public double Height { get; set; } = 28;
    public TopBarColorMode ColorMode { get; set; } = TopBarColorMode.Transparent;
    /// <summary>Fixed 모드의 배경색 / Blur 모드의 틴트.</summary>
    public string Background { get; set; } = "#E0F6F6F6";
    /// <summary>"" 이면 배경 밝기에 따라 검정/흰색 자동.</summary>
    public string Foreground { get; set; } = "";
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
    /// <summary>와이파이·블루투스·볼륨 아이콘 (MyDockFinder 처럼). 클릭 시 작은 패널(볼륨 슬라이더, 블루투스 토글, 설정 열기).</summary>
    public bool ShowStatusIcons { get; set; } = true;
    /// <summary>네트워크 업/다운 속도 (2줄 작은 글씨).</summary>
    public bool ShowNetworkSpeed { get; set; } = true;
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
