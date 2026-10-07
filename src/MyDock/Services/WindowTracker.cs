using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using MyDock.Models;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 작업표시줄에 뜨는 최상위 창 목록을 추적한다.
/// - EnumWindows + 작업표시줄 규칙(보임, 소유자 없음 또는 WS_EX_APPWINDOW, TOOLWINDOW 제외, DWM cloaked 제외, 자기 프로세스 제외)
/// - RegisterShellHookWindow 로 생성/파괴/활성화/깜빡임(HSHELL_FLASH) 알림 수신
/// - 보조로 1.5초 간격 재열거 (변경 있을 때만 WindowsChanged)
/// 모든 이벤트는 Start() 를 호출한 UI 스레드에서 발생.
/// </summary>
public sealed class WindowTracker : IWindowTracker, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly Dictionary<uint, (string Path, string? Aumid)> _procCache = new();
    private IReadOnlyList<AppWindowInfo> _windows = Array.Empty<AppWindowInfo>();
    private HwndSource? _hookSource;
    private uint _shellHookMsg;
    private DispatcherTimer? _pollTimer;
    private Dispatcher? _dispatcher;
    private bool _refreshQueued;
    private IntPtr _foreground;

    public IReadOnlyList<AppWindowInfo> Windows => _windows;

    public IntPtr ForegroundWindow => _foreground;

    public event EventHandler? WindowsChanged;
    public event EventHandler<IntPtr>? WindowFlashed;
    public event EventHandler<IntPtr>? WindowActivated;

    // ───────────────────────── 시작/정지 ─────────────────────────

    public void Start()
    {
        if (_hookSource is not null) return;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // 보이지 않는 최상위 팝업 창 (메시지 전용 창은 셸 훅 브로드캐스트를 못 받는 경우가 있어 숨김 창 사용).
        var p = new HwndSourceParameters("MyDock.ShellHook")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = User32.WS_POPUP, // WS_VISIBLE 없음 → 숨김
            ExtendedWindowStyle = (int)User32.WS_EX_TOOLWINDOW,
        };
        _hookSource = new HwndSource(p);
        _hookSource.AddHook(WndProc);

        _shellHookMsg = User32.RegisterWindowMessage("SHELLHOOK");
        if (!User32.RegisterShellHookWindow(_hookSource.Handle))
            Log.Error($"RegisterShellHookWindow 실패 (err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}) → 타이머 재열거만 사용");

        _foreground = User32.GetForegroundWindow();
        Refresh(force: true);

        _pollTimer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Poll(), _dispatcher);
        _pollTimer.Start();
        Log.Info("WindowTracker 시작");
    }

    public void Stop()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        if (_hookSource is not null)
        {
            try { User32.DeregisterShellHookWindow(_hookSource.Handle); } catch { /* 무시 */ }
            _hookSource.RemoveHook(WndProc);
            _hookSource.Dispose();
            _hookSource = null;
        }
    }

    public void Dispose() => Stop();

    // ───────────────────────── 셸 훅 ─────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_shellHookMsg == 0 || (uint)msg != _shellHookMsg) return IntPtr.Zero;
        try
        {
            int code = (int)(wParam.ToInt64() & 0xFFFF);
            IntPtr target = lParam;
            switch (code)
            {
                case Shell32.HSHELL_WINDOWCREATED:
                case Shell32.HSHELL_WINDOWDESTROYED:
                case Shell32.HSHELL_WINDOWREPLACED:
                case Shell32.HSHELL_REDRAW:
                    QueueRefresh();
                    break;
                case Shell32.HSHELL_WINDOWACTIVATED:
                case Shell32.HSHELL_RUDEAPPACTIVATED:
                    _foreground = target != IntPtr.Zero ? target : User32.GetForegroundWindow();
                    WindowActivated?.Invoke(this, _foreground);
                    QueueRefresh(); // 최소화 상태 등이 바뀌었을 수 있음
                    break;
                case Shell32.HSHELL_FLASH:
                    WindowFlashed?.Invoke(this, target);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("셸 훅 처리 중 예외", ex);
        }
        return IntPtr.Zero;
    }

    private void QueueRefresh()
    {
        if (_refreshQueued || _dispatcher is null) return;
        _refreshQueued = true;
        // 짧은 지연 후 한 번만 (생성 직후엔 제목/스타일이 아직 안 정해진 경우가 있음)
        _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(120);
                _refreshQueued = false;
                Refresh(force: false);
            }
            catch (Exception ex)
            {
                _refreshQueued = false;
                Log.Error("창 목록 갱신 실패", ex);
            }
        }, DispatcherPriority.Background);
    }

    private void Poll()
    {
        try
        {
            IntPtr fg = User32.GetForegroundWindow();
            if (fg != _foreground)
            {
                _foreground = fg;
                WindowActivated?.Invoke(this, fg);
            }
            Refresh(force: false);
        }
        catch (Exception ex)
        {
            Log.Error("WindowTracker 폴링 중 예외", ex);
        }
    }

    // ───────────────────────── 열거 ─────────────────────────

    private void Refresh(bool force)
    {
        List<AppWindowInfo> list;
        try
        {
            list = Enumerate();
        }
        catch (Exception ex)
        {
            Log.Error("창 열거 실패", ex);
            return;
        }

        if (!force && SameList(_windows, list)) return;
        _windows = list;
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameList(IReadOnlyList<AppWindowInfo> a, IReadOnlyList<AppWindowInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false; // record 값 비교
        return true;
    }

    /// <summary>현재 작업표시줄 창 목록을 즉시 열거 (Z 순서).</summary>
    internal List<AppWindowInfo> Enumerate()
    {
        var result = new List<AppWindowInfo>();
        var seenPids = new HashSet<uint>();
        User32.EnumWindows((hwnd, _) =>
        {
            try
            {
                var info = TryGetInfo(hwnd, seenPids);
                if (info is not null) result.Add(info);
            }
            catch (Exception ex)
            {
                Log.Error($"창 정보 조회 실패 hwnd=0x{hwnd.ToInt64():X}", ex);
            }
            return true;
        }, IntPtr.Zero);

        // 이번 열거에서 안 보인 PID 는 캐시에서 제거 (PID 재사용으로 다른 프로세스 정보가 남는 것 방지).
        // 창이 남아 있는 동안 같은 PID 가 재사용될 수는 없으므로, 보이는 PID 의 캐시는 안전하다.
        foreach (var pid in _procCache.Keys.Where(k => !seenPids.Contains(k)).ToList())
            _procCache.Remove(pid);
        return result;
    }

    private AppWindowInfo? TryGetInfo(IntPtr hwnd, HashSet<uint> seenPids)
    {
        if (!IsTaskbarWindow(hwnd)) return null;

        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _ownPid) return null;
        seenPids.Add(pid);

        string title = User32.GetWindowTitle(hwnd);
        if (title.Length == 0) return null;

        var (path, procAumid) = GetProcessInfo(pid);

        // UWP: ApplicationFrameHost 의 프레임 창이면 자식 CoreWindow 의 실제 앱 프로세스를 사용
        if (path.EndsWith(@"\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint childPid = FindUwpChildPid(hwnd, pid);
            if (childPid != 0)
            {
                seenPids.Add(childPid);
                var child = GetProcessInfo(childPid);
                if (child.Path.Length > 0) path = child.Path;
                procAumid = child.Aumid ?? procAumid;
            }
        }

        string? aumid = Shell32.GetWindowAumid(hwnd) ?? procAumid;
        return new AppWindowInfo(hwnd, title, path, aumid, User32.IsIconic(hwnd));
    }

    /// <summary>작업표시줄에 버튼이 생기는 창인지 (Alt+Tab/작업표시줄 규칙 근사).</summary>
    internal static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!User32.IsWindowVisible(hwnd)) return false;
        long ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
        bool appWindow = (ex & User32.WS_EX_APPWINDOW) != 0;
        if ((ex & User32.WS_EX_TOOLWINDOW) != 0) return false;
        if ((ex & User32.WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
        IntPtr owner = User32.GetWindow(hwnd, User32.GW_OWNER);
        if (owner != IntPtr.Zero && !appWindow) return false;
        // 다른 가상 데스크톱의 창, 일시 중단된 UWP 프레임 등은 cloaked
        if (Dwm.IsCloaked(hwnd)) return false;
        string cls = User32.GetClassNameOf(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        return true;
    }

    private (string Path, string? Aumid) GetProcessInfo(uint pid)
    {
        if (_procCache.TryGetValue(pid, out var v)) return v;
        v = Kernel32.QueryProcess(pid);
        // 접근 거부(관리자 권한 프로세스 등)는 캐시하지 않고 다음에 재시도
        if (v.Path.Length > 0) _procCache[pid] = v;
        return v;
    }

    private static uint FindUwpChildPid(IntPtr frame, uint framePid)
    {
        uint found = 0;
        User32.EnumChildWindows(frame, (child, _) =>
        {
            User32.GetWindowThreadProcessId(child, out uint cpid);
            if (cpid != 0 && cpid != framePid)
            {
                found = cpid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ───────────────────────── 매칭 ─────────────────────────

    public bool Matches(PinItem pin, AppWindowInfo window)
    {
        if (pin is null || window is null) return false;
        string target = (pin.Target ?? "").Trim();
        if (target.Length == 0) return false;

        switch (pin.Kind)
        {
            case PinKind.Aumid:
            {
                if (window.Aumid is { Length: > 0 } wa)
                {
                    if (string.Equals(wa, target, StringComparison.OrdinalIgnoreCase)) return true;
                    // 같은 패키지 패밀리 (앱 ID 가 달라도 같은 패키지) 접두 비교
                    string family = AppsFolder.FamilyOf(target);
                    if (wa.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase)) return true;
                }
                // 창 AUMID 를 못 얻은 경우 WindowsApps 경로에서 패키지 패밀리 추출해 비교
                string? pathFamily = AppsFolder.FamilyFromWindowsAppsPath(window.ProcessPath);
                return pathFamily is not null &&
                       string.Equals(pathFamily, AppsFolder.FamilyOf(target), StringComparison.OrdinalIgnoreCase);
            }
            case PinKind.Exe:
            {
                if (window.ProcessPath.Length == 0) return false;
                string expanded = Environment.ExpandEnvironmentVariables(target.Trim('"'));
                if (string.Equals(NormalizePath(expanded), NormalizePath(window.ProcessPath), StringComparison.OrdinalIgnoreCase))
                    return true;
                // fallback: 파일명 비교 (설치 위치가 바뀐 경우 등)
                string pinFile = Path.GetFileName(expanded);
                return pinFile.Length > 0 &&
                       string.Equals(pinFile, Path.GetFileName(window.ProcessPath), StringComparison.OrdinalIgnoreCase);
            }
            default:
                return false;
        }
    }

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }

    public string GetAppKey(AppWindowInfo window)
    {
        if (!string.IsNullOrEmpty(window.Aumid)) return window.Aumid.ToLowerInvariant();
        if (!string.IsNullOrEmpty(window.ProcessPath)) return window.ProcessPath.ToLowerInvariant();
        return "hwnd:" + window.Hwnd.ToInt64().ToString("X");
    }

    public PinItem CreatePin(AppWindowInfo window)
    {
        string path = window.ProcessPath ?? "";
        if (AppsFolder.IsWindowsAppsPath(path))
        {
            // 패키지 앱: 버전 포함 경로는 절대 저장하지 않고 AUMID 로.
            string? aumid = window.Aumid;
            if (string.IsNullOrEmpty(aumid) || !aumid.Contains('!'))
            {
                string? family = AppsFolder.FamilyFromWindowsAppsPath(path);
                aumid = family is null ? aumid : AppsFolder.FindAumidByFamily(family) ?? aumid;
            }
            if (!string.IsNullOrEmpty(aumid))
            {
                // 프로세스 AUMID 는 실행할 때 쓴 문자열 대소문자를 그대로 가질 수 있음 (예: ...!claude) → 실제 대소문자로
                aumid = AppsFolder.RestoreAumidCase(aumid) ?? aumid;
                return new PinItem
                {
                    Name = AppsFolder.GetAppDisplayName(aumid) ?? FriendlyExeName(path) ?? window.Title,
                    Kind = PinKind.Aumid,
                    Target = aumid,
                };
            }
            Log.Warn($"패키지 앱인데 AUMID 를 못 찾음 → exe 경로로 핀 생성 (업데이트 시 깨질 수 있음): {path}");
        }

        return new PinItem
        {
            Name = FriendlyExeName(path) ?? window.Title,
            Kind = PinKind.Exe,
            Target = path,
        };
    }

    private static string? FriendlyExeName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName.Trim();
        }
        catch
        {
            // 접근 불가 → 파일명
        }
        return Path.GetFileNameWithoutExtension(path);
    }
}
