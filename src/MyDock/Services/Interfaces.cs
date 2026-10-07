using System.Windows;
using System.Windows.Media;
using MyDock.Models;

namespace MyDock.Services;

// ─────────────────────────────────────────────────────────────
// UI ↔ 백엔드 계약. UI(Views/ViewModels)는 이 인터페이스만 사용한다.
// 구현은 백엔드(Services/*.cs, Native/*.cs) 담당.
// 모든 이벤트는 UI(Dispatcher) 스레드에서 발생해야 한다.
// ─────────────────────────────────────────────────────────────

public interface ISettingsService
{
    Settings Current { get; }
    /// <summary>설정이 바뀌었을 때: Save() 직후, 또는 외부에서 settings.json 을 편집해 다시 로드했을 때.</summary>
    event EventHandler? SettingsChanged;
    void Save();
    string SettingsPath { get; }
    /// <summary>원본 이미지를 %APPDATA%\MyDock\icons\ 로 복사하고 복사본 경로를 반환.</summary>
    string ImportIcon(string sourcePath);
}

public interface IWindowTracker
{
    /// <summary>현재 독에 표시할 앱 창 목록 (작업표시줄에 뜨는 창 기준).</summary>
    IReadOnlyList<AppWindowInfo> Windows { get; }
    IntPtr ForegroundWindow { get; }
    event EventHandler? WindowsChanged;
    /// <summary>창이 알림(작업표시줄 깜빡임)을 요청함 → UI 는 점으로만 표시.</summary>
    event EventHandler<IntPtr>? WindowFlashed;
    /// <summary>창이 활성화됨 → 해당 앱의 알림 점 제거용.</summary>
    event EventHandler<IntPtr>? WindowActivated;
    /// <summary>핀과 창이 같은 앱인지 (exe 경로 또는 AUMID 비교).</summary>
    bool Matches(PinItem pin, AppWindowInfo window);
    /// <summary>같은 앱으로 묶기 위한 키 (AUMID 우선, 없으면 소문자 exe 경로).</summary>
    string GetAppKey(AppWindowInfo window);
    /// <summary>실행 중 창으로 핀 생성. WindowsApps 패키지 앱이면 Kind=Aumid(버전 경로 저장 금지), 아니면 Kind=Exe.</summary>
    PinItem CreatePin(AppWindowInfo window);
    void Start();
    void Stop();
}

public interface IAppLauncher
{
    void Launch(PinItem pin);
    /// <summary>창을 앞으로 가져옴 (최소화돼 있으면 복원).</summary>
    void Activate(IntPtr hwnd);
    /// <summary>이미 앞에 있으면 최소화, 아니면 Activate. (맥 독 클릭 동작)</summary>
    void ToggleActivate(IntPtr hwnd);
    void Close(IntPtr hwnd);
    /// <summary>파일/폴더를 기본 프로그램으로 엶 (settings.json 편집 등).</summary>
    void OpenFile(string path);
}

public interface IIconService
{
    /// <summary>핀 아이콘. IconPath 가 있으면 그것, 없으면 exe/패키지 아이콘. 실패 시 기본 아이콘. Frozen.</summary>
    ImageSource GetIcon(PinItem pin, IconStyle style);
    ImageSource GetIcon(AppWindowInfo window, IconStyle style);
}

public interface IDesktopWindowService
{
    /// <summary>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW 적용 (포커스 안 뺏고 Alt+Tab 에서 숨김). SourceInitialized 이후 호출.</summary>
    void MakeOverlay(Window window);
    /// <summary>독 공간 예약(Reserve 모드). 숨은 도우미 AppBar 창으로 가장자리 띠만 예약하고 보이는 창은 움직이지 않음.
    /// Top 이면 상단바 아래에 쌓임. Dispose 하면 해제. 탐색기 재시작 시 자동 재등록.</summary>
    IEdgeReservation ReserveEdge(DockEdge edge, double thickness);
    /// <summary>상단바 AppBar (창 위치까지 맞춤). 탐색기 재시작 시 자동 재등록.</summary>
    void RegisterTopAppBar(Window window, double thickness);
    void UnregisterAppBar(Window window);
    /// <summary>주 모니터 작업 영역이 아닌 전체 화면 영역 (DIP).</summary>
    Rect GetPrimaryScreenBounds();
    /// <summary>주 모니터 작업 영역 (DIP). ReserveSpace=false 일 때 배치용.</summary>
    Rect GetPrimaryWorkArea();
    /// <summary>창 전체에 아크릴 블러 배경 적용. tint 의 알파가 진하기. 창은 AllowsTransparency=False 여야 할 수 있음 — 구현 쪽 주석 참고.</summary>
    void EnableBlur(Window window, Color tint);
    void DisableBlur(Window window);
    /// <summary>창 모양을 둥근 사각형으로 자름 (블러 창용). 창 크기가 바뀌면 자동 재적용. radius 0 이면 해제.</summary>
    void SetRoundedRegion(Window window, double radiusDip);
    /// <summary>현재 마우스 커서 위치 (화면 좌표, DIP).</summary>
    Point GetCursorPosition();
    /// <summary>화면 영역(DIP)의 대표 색 (최빈/중앙값). 읽을 수 없으면 null. 상단바 자동 색용.</summary>
    Color? SampleScreenColor(Rect areaDip);
    /// <summary>바탕화면 배경(월페이퍼)만의 해당 영역(DIP) 평균 색 — 우리 창·다른 창 제외. 투명 상단바 글자색 결정용. 실패 시 null.
    /// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) 는 원격(StarDesk) 화면에서도 창이 사라지므로 사용 금지.</summary>
    Color? SampleWallpaperColor(Rect areaDip);
    /// <summary>배경화면이 바뀜 (설정 변경, 가상 데스크톱 전환으로 데스크톱별 배경이 바뀐 경우 포함).</summary>
    event EventHandler? WallpaperChanged;
    /// <summary>전체 화면 앱이 켜짐(true)/꺼짐(false). 이때 독·상단바는 Topmost 해제·숨김.</summary>
    event EventHandler<bool>? FullscreenAppChanged;
    /// <summary>해상도·DPI·작업 영역 변경, 탐색기 재시작 후. UI 는 배치를 다시 계산.</summary>
    event EventHandler? DisplayChanged;
}

public interface IEdgeReservation : IDisposable
{
    /// <summary>시스템이 확정한 예약 영역 (DIP).</summary>
    Rect Bounds { get; }
    event EventHandler? BoundsChanged;
}

public interface IVirtualDesktopService
{
    void Previous();
    void Next();
    void New();
}

public interface IShellActions
{
    // 원격(StarDesk)에서 Win 단축키가 안 넘어가므로 로컬에서 SendInput 으로 대신 보냄.
    void OpenStartMenu();        // Win
    void OpenSearch();           // Win+S
    void OpenQuickSettings();    // Win+A (와이파이/볼륨/블루투스)
    void OpenNotificationCenter(); // Win+N
    void OpenTaskView();         // Win+Tab
}

public enum WifiState { Unknown, Disconnected, Connected, Ethernet }

/// <summary>상단바 상태 아이콘(와이파이·블루투스·볼륨·네트워크 속도). 값이 바뀌면 Changed (UI 스레드).</summary>
public interface IStatusService
{
    WifiState Wifi { get; }
    /// <summary>와이파이 신호 0~100 (Connected 일 때).</summary>
    int WifiSignal { get; }
    string? WifiName { get; }
    /// <summary>블루투스 라디오 켜짐 여부. 어댑터 없거나 알 수 없으면 null.</summary>
    bool? BluetoothOn { get; }
    /// <summary>기본 출력 장치 볼륨 0~1.</summary>
    double Volume { get; }
    bool Muted { get; }
    /// <summary>초당 바이트 (모든 활성 어댑터 합계, 1초 주기).</summary>
    long UploadBytesPerSec { get; }
    long DownloadBytesPerSec { get; }
    event EventHandler? Changed;
    void SetVolume(double volume);
    void SetMuted(bool muted);
    /// <summary>블루투스 라디오 켜기/끄기. 권한 없거나 실패하면 false.</summary>
    Task<bool> SetBluetoothAsync(bool on);
    void OpenWifiSettings();      // ms-settings:network-wifi
    void OpenBluetoothSettings(); // ms-settings:bluetooth
    void OpenSoundSettings();     // ms-settings:sound
    void Start();
    void Stop();
}

public interface IImeService
{
    /// <summary>포그라운드 창이 한글 입력 모드면 true, 영문이면 false, 알 수 없으면 null.</summary>
    bool? IsHangulMode();
    /// <summary>포그라운드 창 기준으로 한/영 전환 (VK_HANGUL 전송).</summary>
    void ToggleHangul();
}

public interface IStartupService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

/// <summary>App.xaml.cs 에서 만들어 창들에 넘겨주는 서비스 묶음.</summary>
public sealed record AppServices(
    ISettingsService Settings,
    IWindowTracker Windows,
    IAppLauncher Launcher,
    IIconService Icons,
    IDesktopWindowService DesktopWindows,
    IVirtualDesktopService VirtualDesktops,
    IShellActions Shell,
    IImeService Ime,
    IStatusService Status,
    IStartupService Startup);
