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
    ImageSource GetIcon(PinItem pin);
    ImageSource GetIcon(AppWindowInfo window);
}

public interface IDesktopWindowService
{
    /// <summary>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW 적용 (포커스 안 뺏고 Alt+Tab 에서 숨김). SourceInitialized 이후 호출.</summary>
    void MakeOverlay(Window window);
    /// <summary>AppBar 로 등록하고 창 위치/크기를 시스템이 정해준 영역으로 맞춤. thickness 는 DIP. edge=Top 이면 상단바 아래에 쌓임.</summary>
    void RegisterAppBar(Window window, DockEdge edge, double thickness);
    /// <summary>상단 AppBar.</summary>
    void RegisterTopAppBar(Window window, double thickness);
    void UnregisterAppBar(Window window);
    /// <summary>주 모니터 작업 영역이 아닌 전체 화면 영역 (DIP).</summary>
    Rect GetPrimaryScreenBounds();
    /// <summary>주 모니터 작업 영역 (DIP). ReserveSpace=false 일 때 배치용.</summary>
    Rect GetPrimaryWorkArea();
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

public interface IImeService
{
    /// <summary>포그라운드 창이 한글 입력 모드면 true, 영문이면 false, 알 수 없으면 null.</summary>
    bool? IsHangulMode();
    /// <summary>포그라운드 창 기준으로 한/영 전환 (VK_HANGUL 전송).</summary>
    void ToggleHangul();
}

public interface IMyDockFinderImporter
{
    /// <summary>ico.ini(UTF-16) 를 읽어 핀 목록으로 변환. 아이콘 png 는 ISettingsService.ImportIcon 으로 복사.</summary>
    List<PinItem> Import(string iniPath);
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
    IStartupService Startup);
