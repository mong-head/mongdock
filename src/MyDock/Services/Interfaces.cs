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
    /// <summary>바탕 화면(Progman/WorkerW)·작업 표시줄 같은 셸 창이면 true — 상단바가 "바탕 화면" 표시용.</summary>
    bool IsDesktopWindow(IntPtr hwnd);
    /// <summary>창의 프로필 이름 (크롬 등, 알 수 있을 때만).</summary>
    string? GetProfileName(AppWindowInfo window);
    void Start();
    void Stop();
}

public interface IAppLauncher
{
    void Launch(PinItem pin);
    /// <summary>창을 앞으로 가져옴 (최소화돼 있으면 복원). 다른 가상 데스크톱에 있으면 그 데스크톱으로 이동한 뒤 활성화.</summary>
    void Activate(IntPtr hwnd);
    /// <summary>이미 앞에 있으면 최소화, 아니면 Activate. (맥 독 클릭 동작)</summary>
    void ToggleActivate(IntPtr hwnd);
    void Close(IntPtr hwnd);
    /// <summary>브라우저(크롬·엣지·웨일) 프로필 목록 (Local State). 프로필 개념이 없는 앱이면 빈 목록.</summary>
    IReadOnlyList<AppProfile> GetProfiles(PinItem pin);
    /// <summary>해당 프로필로 새 창 (예: chrome.exe --profile-directory="Profile 1").</summary>
    void LaunchProfile(PinItem pin, AppProfile profile);
    /// <summary>창 최소화 (ShowWindowAsync SW_MINIMIZE).</summary>
    void Minimize(IntPtr hwnd);
    /// <summary>파일/폴더를 기본 프로그램으로 엶 (settings.json 편집 등).</summary>
    void OpenFile(string path);
}

public interface IIconService
{
    /// <summary>핀 아이콘. IconPath 가 있으면 그것, 없으면 exe/패키지 아이콘. 실패 시 기본 아이콘. Frozen.</summary>
    ImageSource GetIcon(PinItem pin, IconStyle style);
    ImageSource GetIcon(AppWindowInfo window, IconStyle style);
    /// <summary>창 자체의 작은 아이콘(WM_GETICON — 크롬 프로필 아바타 배지 등). 없으면 null.</summary>
    ImageSource? GetWindowIcon(IntPtr hwnd);
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
    /// <summary>주 모니터 위의 커서 위치 (주 모니터 DPI 기준 DIP). 커서가 다른 모니터에 있으면 null.</summary>
    Point? GetCursorPosition();
    /// <summary>화면 어디서든 마우스 버튼(왼/오/가운데)이 눌린 순간 (WH_MOUSE_LL, UI 스레드). 인자는 눌린 곳 모니터 기준 DIP 위치
    /// (물리 px / 그 모니터 배율 — 그 모니터 위 창의 화면 DIP 와 비교 가능; 주 모니터면 기존과 같음). 어느 모니터에도 없으면 null.
    /// 구독자가 있을 때만 훅을 설치 — NOACTIVATE 창의 메뉴·패널 "바깥 클릭 시 닫기"용. 폴링은 짧은 탭(원격 트랙패드)을 놓침.</summary>
    event EventHandler<Point?>? GlobalMouseDown;
    /// <summary>화면 영역(DIP)의 대표 색 (최빈/중앙값). 읽을 수 없으면 null. 상단바 자동 색용.</summary>
    Color? SampleScreenColor(Rect areaDip);
    /// <summary>바탕화면 배경(월페이퍼)만의 해당 영역(DIP) 평균 색 — 우리 창·다른 창 제외. 투명 상단바 글자색 결정용. 실패 시 null.
    /// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) 는 원격(StarDesk) 화면에서도 창이 사라지므로 사용 금지.</summary>
    Task<Color?> SampleWallpaperColorAsync(Rect areaDip);
    // 구독자가 있을 때만 배경 감시 폴링을 돌림 (Transparent 모드가 아니면 UI 는 구독 해제).
    /// <summary>배경화면이 바뀜 (설정 변경, 가상 데스크톱 전환으로 데스크톱별 배경이 바뀐 경우 포함).</summary>
    event EventHandler? WallpaperChanged;
    /// <summary>전체 화면 앱이 켜짐(true)/꺼짐(false) — 어느 모니터든. 이때 그 모니터의 독·상단바는 Topmost 해제·숨김
    /// (어느 모니터인지는 <see cref="FullscreenMonitor"/>/<see cref="IsFullscreenOn"/>). 다른 모니터로 옮겨 가도 true 로 다시 발생.</summary>
    event EventHandler<bool>? FullscreenAppChanged;
    /// <summary>해상도·DPI·작업 영역 변경, 모니터 연결/분리, 탐색기 재시작 후 (300ms 디바운스). UI 는 배치를 다시 계산.</summary>
    event EventHandler? DisplayChanged;
    /// <summary>윈도우 작업 표시줄(주·보조 모니터) 숨김/복원. 숨긴 상태로 프로세스가 끝나면(정상·예외·ProcessExit) 반드시 복원. 탐색기 재시작 후 숨김 상태면 다시 숨김.</summary>
    void SetWindowsTaskbarHidden(bool hidden);
    /// <summary>DWM 실시간 창 미리보기를 host 창의 destDip 영역에 그림. Dispose 로 해제, Update 로 위치 변경. 다른 데스크톱(cloaked) 창은 비어 보일 수 있음.</summary>
    IWindowThumbnail? CreateThumbnail(Window host, IntPtr source, Rect destDip);

    // ───────────── 여러 모니터 ─────────────
    // DIP 좌표 규칙: "물리 픽셀 / 그 모니터의 배율" (= 그 모니터 위 WPF 창의 Left/Top). MonitorInfo 주석 참고.
    // 위의 모니터 인자 없는 메서드들은 기존처럼 주 모니터 기준.

    /// <summary>연결된 모니터 목록 (주 모니터 먼저). <see cref="Monitors.GetAll"/> 과 같음.</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();
    /// <summary>장치 이름(Dock.Monitor 등)의 모니터. "" 이거나 연결 안 됐으면 주 모니터.</summary>
    MonitorInfo ResolveMonitor(string? deviceName);
    /// <summary>독 공간 예약을 지정 모니터에 (null/""/분리됨 → 주 모니터, 다시 연결되면 자동 복귀).</summary>
    IEdgeReservation ReserveEdge(DockEdge edge, double thickness, string? monitor);
    /// <summary>상단바 AppBar 를 지정 모니터 맨 위에 (null/"" → 주 모니터). 그 모니터가 분리되면 재배치하지 않음 — 창을 닫을 것.</summary>
    void RegisterTopAppBar(Window window, double thickness, string? monitor);
    /// <summary>커서가 monitor 위에 있으면 그 모니터 기준 DIP, 아니면 null.</summary>
    Point? GetCursorPosition(MonitorInfo monitor);
    /// <summary>monitor 기준 DIP 영역의 화면 대표 색 (그 모니터 밖은 잘라냄).</summary>
    Color? SampleScreenColor(Rect areaDip, MonitorInfo monitor);
    /// <summary>monitor 기준 DIP 영역의 배경화면 색 (그 모니터의 배경).</summary>
    Task<Color?> SampleWallpaperColorAsync(Rect areaDip, MonitorInfo monitor);
    /// <summary>
    /// 창의 DPI 가 monitor 와 다르고 다른 모니터에 있으면 물리 픽셀(SetWindowPos)로 그 모니터로 옮겨 DPI 를 맞춘다
    /// (그 뒤 Left/Top 을 monitor 기준 DIP 로 설정하면 정확). 같은 모니터·같은 DPI 면 아무것도 안 함. 옮겼으면 true.
    /// 핸들이 없으면 아무것도 안 함 — 새 창은 WPF 가 Left/Top 이 속한 모니터에 만든다.
    /// </summary>
    bool EnsureOnMonitor(Window window, MonitorInfo monitor);
    /// <summary>전체 화면 앱이 있는 모니터의 장치 이름 (없으면 null). <see cref="FullscreenAppChanged"/> 는 이 값이 바뀔 때 발생.</summary>
    string? FullscreenMonitor { get; }
    /// <summary>해당 모니터(null/"" = 주 모니터)에 전체 화면 앱이 있는지.</summary>
    bool IsFullscreenOn(string? deviceName);
}

public interface IWindowThumbnail : IDisposable
{
    /// <summary>원본 창 크기(px) — 비율 계산용.</summary>
    Size SourceSize { get; }
    void Update(Rect destDip);
}

public sealed record AppProfile(string Id, string Name, ImageSource? Avatar);

public interface IEdgeReservation : IDisposable
{
    /// <summary>시스템이 확정한 예약 영역 (DIP).</summary>
    Rect Bounds { get; }
    event EventHandler? BoundsChanged;
}

public interface IVirtualDesktopService
{
    /// <summary>현재 가상 데스크톱 번호 (1부터)와 전체 개수. 모르면 0. 바뀌면 Changed (UI 스레드, 단축키·작업 보기로 바꾼 경우 포함).</summary>
    int CurrentIndex { get; }
    int Count { get; }
    event EventHandler? Changed;
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
    void ShowDesktop();          // Win+D (바탕 화면 보기 토글)
    // 로고 메뉴 (맥 Apple 메뉴 대응). 전원 동작은 UI 가 확인 카드를 띄운 뒤에만 호출.
    void OpenAbout();            // ms-settings:about
    void OpenSettings();         // ms-settings:
    void OpenStore();            // ms-windows-store:
    void OpenTaskManager();      // taskmgr
    void Sleep();                // SetSuspendState
    void Restart();              // shutdown /r /t 0
    void Shutdown();             // shutdown /s /t 0
    void LockScreen();           // LockWorkStation
    void SignOut();              // ExitWindowsEx(EWX_LOGOFF)
}

public enum WifiState { Unknown, Disconnected, Connected, Ethernet }

/// <summary>상단바 상태 아이콘(와이파이·블루투스·볼륨·네트워크 속도). 값이 바뀌면 Changed (UI 스레드).</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault, AudioDeviceKind Kind);
public enum AudioDeviceKind { Speakers, Headphones, Display, Digital, Other }
/// <summary>
/// 페어링된 블루투스 기기. CanConnect = 블루투스 오디오 KS 필터가 있어 패널에서 바로 연결/해제 가능
/// (아니면 UI 는 블루투스 설정을 연다).
/// </summary>
public sealed record BluetoothDeviceInfo(string Id, string Name, bool Connected,
    BluetoothDeviceKind Kind = BluetoothDeviceKind.Other, bool CanConnect = false);
public enum BluetoothDeviceKind { Other, Headphones, Speaker, Mouse, Keyboard, Gamepad, Phone, Computer }
/// <summary>Requested = 드라이버에 연결/해제 요청을 보냄(실제 결과는 BluetoothDevices 의 Connected 로 확인).
/// NotSupported = 오디오 기기가 아니거나 필터를 못 찾음. Failed = 요청 실패/시간 초과.</summary>
public enum BluetoothConnectResult { Requested, NotSupported, Failed }

/// <summary>현재 재생 중인 미디어 (GlobalSystemMediaTransportControlsSessionManager). 변경 시 Changed (UI 스레드).</summary>
public interface IMediaService
{
    bool HasSession { get; }
    string? Title { get; }
    string? Artist { get; }
    /// <summary>앨범 아트/썸네일. 없으면 null. Frozen.</summary>
    ImageSource? Thumbnail { get; }
    bool IsPlaying { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    event EventHandler? Changed;
    Task PlayPauseAsync();
    Task NextAsync();
    Task PreviousAsync();
    void Start();
    void Stop();
}

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
    void OpenAvailableNetworks();  // 다른 네트워크 목록 (ms-availablenetworks:)
    /// <summary>와이파이 라디오 켜짐 여부. 알 수 없으면 null.</summary>
    bool? WifiRadioOn { get; }
    Task<bool> SetWifiAsync(bool on);
    /// <summary>현재 연결의 IPv4 주소와 링크 속도(bps). 모르면 null / 0.</summary>
    string? IpAddress { get; }
    long LinkSpeedBps { get; }
    /// <summary>재생 장치 목록 (활성 장치만). IsDefault 가 현재 기본 장치.</summary>
    IReadOnlyList<AudioDevice> OutputDevices { get; }
    /// <summary>기본 재생 장치 변경 (IPolicyConfig). 실패 시 false.</summary>
    bool SetDefaultOutput(string deviceId);
    /// <summary>페어링된 블루투스 장치 (연결 여부 포함).</summary>
    IReadOnlyList<BluetoothDeviceInfo> BluetoothDevices { get; }
    /// <summary>
    /// 블루투스 오디오 기기 연결(connect=true)/해제. 백그라운드 스레드에서 KS 속성을 보내고 결과만 돌려줌 (UI 스레드를 막지 않음, 예외 없음).
    /// 실제 연결 반영은 DeviceWatcher → Changed 로 옴.
    /// </summary>
    Task<BluetoothConnectResult> SetBluetoothDeviceConnectedAsync(string id, bool connect);
    /// <summary>표시 안 하는 항목의 폴링을 끔 (상단바 설정 반영).</summary>
    void SetPolling(bool networkSpeed, bool wifiAndBluetooth);
    void Start();
    void Stop();
}

/// <summary>포그라운드 앱의 메뉴. 일반 Win32 메뉴(HMENU)가 있으면 그것을 읽고, 없으면 settings.AppMenus → 내장 기본 메뉴(브라우저·탐색기 등) → 공통 편집 메뉴 순.</summary>
public interface IAppMenuService
{
    IReadOnlyList<AppMenu> GetMenus(AppWindowInfo window);
    /// <summary>항목 실행: Win32 메뉴는 WM_COMMAND 를 그 창에, 단축키 항목은 그 창을 포그라운드로 확인한 뒤 SendInput.</summary>
    void Invoke(IntPtr hwnd, AppMenuItem item);
}

public sealed record AppMenu(string Title, IReadOnlyList<AppMenuItem> Items);

/// <summary>IsSeparator 면 구분선. Shortcut 은 표시용 문자열. Children 이 있으면 하위 메뉴.</summary>
public sealed record AppMenuItem(string Text, bool IsSeparator, bool Enabled, bool Checked, string? Shortcut, IReadOnlyList<AppMenuItem>? Children, object? Payload);

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
    IMediaService Media,
    IAppMenuService AppMenus,
    IStartupService Startup);
