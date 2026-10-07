using System.Diagnostics;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 윈도우 기본 알림 팝업(오른쪽 아래 토스트)을 화면 밖으로 옮겨 안 보이게 한다. 몽독 배너만 보이게 하는 용도.
///
/// 확인한 사실 (Win11 26200):
/// - 토스트 팝업은 ShellExperienceHost.exe 의 최상위 창 하나(클래스 "Windows.UI.Core.CoreWindow", 제목 "새 알림"/"New notification")를
///   계속 재사용한다. 평소엔 DWM 셸 클로킹(cloaked=2) + 높이 0 으로 숨어 있고, 토스트가 오면 클로킹 해제 → 크기/위치 설정 → 표시.
/// - 표시 중에 SetWindowPos 로 화면 밖으로 옮기면 안 보이고, 셸이 다시 자리를 잡을 때(LOCATIONCHANGE) 한 번 더 옮기면 끝까지 밖에 있다.
///   토스트가 끝나면 셸이 다시 클로킹 + 원래 자리로 되돌리므로, 몽독이 꺼지거나 죽어도 다음 토스트는 정상으로 뜬다.
/// - 알림 자체는 그대로라 알림 기록(알림 센터·wpndatabase)에 남는다.
/// - SW_HIDE / 레이어드 알파는 쓰지 않는다: 재사용되는 셸 창의 상태를 바꿔서 몽독이 꺼진 뒤 복원된다는 보장이 없다.
///
/// 판별은 엄격하게: 프로세스 ShellExperienceHost + 클래스 + 제목 + 크기(모니터 너비의 60% 미만). 알림 센터("알림 센터")·
/// 빠른 설정·시작 메뉴·검색 창은 제목/프로세스가 달라 걸리지 않는다.
/// 훅: EVENT_OBJECT_UNCLOAKED 는 전역(드문 이벤트), EVENT_OBJECT_LOCATIONCHANGE 는 ShellExperienceHost 프로세스로만 한정.
/// UI 스레드(메시지 루프가 있는 스레드)에서만 만들고 호출할 것.
/// </summary>
public sealed class NativeToastSuppressor : IDisposable
{
    private const string ToastClass = "Windows.UI.Core.CoreWindow";
    private const string HostProcess = "ShellExperienceHost";
    private const int OffscreenPos = -32000;
    private const int OffscreenThreshold = -10000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS (셸 스레드가 바빠도 몽독이 막히지 않게)
    private const uint MoveFlags = 0x0001 | 0x0004 | 0x0010 | 0x0200 | 0x4000;

    /// <summary>토스트 팝업 창 제목 (윈도우 표시 언어별). 알림 센터 창 제목("알림 센터"/"Notification Center")과는 다르다.</summary>
    private static readonly HashSet<string> ToastTitles = new(StringComparer.Ordinal)
    {
        "새 알림", "New notification", "新しい通知", "新通知", "新通知。",
    };

    private readonly WinEventApi.WinEventProc _proc;
    private IntPtr _uncloakHook;
    private IntPtr _locationHook;
    private uint _hostPid;
    private readonly Dictionary<uint, bool> _pidIsHost = new();

    // 마지막으로 옮긴 창과 옮기기 전 위치 (끌 때 아직 떠 있으면 되돌리기용)
    private IntPtr _movedHwnd;
    private int _origLeft, _origTop;
    private int _hiddenCount;
    private bool _disposed;

    public NativeToastSuppressor()
    {
        _proc = OnWinEvent; // 델리게이트를 필드에 잡아 GC 로 수거되지 않게
    }

    public bool Enabled { get; private set; }

    /// <summary>켜고 끔. 같은 값이면 아무것도 안 함.</summary>
    public void SetEnabled(bool on)
    {
        if (_disposed || on == Enabled) return;
        if (on) Enable();
        else Disable();
    }

    private void Enable()
    {
        _uncloakHook = WinEventApi.SetWinEventHook(WinEventApi.EVENT_OBJECT_UNCLOAKED, WinEventApi.EVENT_OBJECT_UNCLOAKED,
            IntPtr.Zero, _proc, 0, 0, WinEventApi.WINEVENT_OUTOFCONTEXT | WinEventApi.WINEVENT_SKIPOWNPROCESS);
        if (_uncloakHook == IntPtr.Zero)
        {
            Log.Warn($"토스트 숨기기: SetWinEventHook 실패 (오류 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return;
        }
        Enabled = true;
        HookHost(FindHostPid());
        Log.Info($"토스트 숨기기 켬 (ShellExperienceHost pid={_hostPid})");
        SweepExisting();
    }

    private void Disable()
    {
        Enabled = false;
        Unhook(ref _locationHook);
        Unhook(ref _uncloakHook);
        _hostPid = 0;
        RestoreMoved();
        Log.Info($"토스트 숨기기 끔 (숨긴 횟수 {_hiddenCount})");
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook == IntPtr.Zero) return;
        WinEventApi.UnhookWinEvent(hook);
        hook = IntPtr.Zero;
    }

    /// <summary>LOCATIONCHANGE 훅을 ShellExperienceHost 프로세스에 건다 (셸이 재시작돼 pid 가 바뀌면 다시 건다).</summary>
    private void HookHost(uint pid)
    {
        if (pid == 0 || pid == _hostPid) return;
        Unhook(ref _locationHook);
        _hostPid = pid;
        _locationHook = WinEventApi.SetWinEventHook(WinEventApi.EVENT_OBJECT_LOCATIONCHANGE, WinEventApi.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _proc, pid, 0, WinEventApi.WINEVENT_OUTOFCONTEXT);
        if (_locationHook == IntPtr.Zero)
            Log.Warn($"토스트 숨기기: 위치 변경 훅 실패 (오류 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
    }

    private static uint FindHostPid()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName(HostProcess))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId == session) return (uint)p.Id;
                }
                catch (Exception) { /* 종료 중인 프로세스 */ }
            }
        }
        return 0;
    }

    /// <summary>켤 때 이미 떠 있는 토스트가 있으면 바로 숨김.</summary>
    private void SweepExisting()
    {
        IntPtr h = IntPtr.Zero;
        while ((h = User32.FindWindowEx(IntPtr.Zero, h, ToastClass, null)) != IntPtr.Zero)
        {
            if (IsToastWindow(h, out uint pid))
            {
                HookHost(pid);
                Hide(h, countAsNew: true);
            }
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!Enabled || hwnd == IntPtr.Zero || idObject != WinEventApi.OBJID_WINDOW || idChild != WinEventApi.CHILDID_SELF) return;
        try
        {
            if (!IsToastWindow(hwnd, out uint pid)) return;
            bool uncloaked = eventType == WinEventApi.EVENT_OBJECT_UNCLOAKED;
            if (uncloaked) HookHost(pid);
            Hide(hwnd, countAsNew: uncloaked);
        }
        catch (Exception ex)
        {
            Log.Warn("토스트 숨기기 처리 실패", ex);
        }
    }

    private bool IsToastWindow(IntPtr hwnd, out uint pid)
    {
        pid = 0;
        if (User32.GetClassNameOf(hwnd) != ToastClass) return false;
        if (!ToastTitles.Contains(User32.GetWindowTitle(hwnd))) return false;
        User32.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0 || !IsHostPid(pid)) return false;

        // 크기: 토스트는 좁은 카드(여러 개면 세로로 쌓임). 모니터 너비의 60% 이상이면 토스트가 아님.
        if (!User32.GetWindowRect(hwnd, out RECT r)) return false;
        if (r.Left <= OffscreenThreshold) return true; // 이미 우리가 옮긴 상태
        int width = r.Width;
        if (width < 100) return false;
        IntPtr monitor = DesktopApi.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (!DesktopApi.TryGetMonitorRects(monitor, out RECT bounds, out _)) return false;
        return width < bounds.Width * 0.6 && r.Height <= bounds.Height;
    }

    private bool IsHostPid(uint pid)
    {
        if (_pidIsHost.TryGetValue(pid, out bool known)) return known;
        bool isHost = false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            isHost = string.Equals(p.ProcessName, HostProcess, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { /* 이미 종료됨 */ }
        if (_pidIsHost.Count > 64) _pidIsHost.Clear();
        _pidIsHost[pid] = isHost;
        return isHost;
    }

    /// <param name="countAsNew">클로킹 해제 = 새로 뜬 토스트 → 로그 한 줄 (셸이 자리를 다시 잡을 때마다 찍지 않게).</param>
    private void Hide(IntPtr hwnd, bool countAsNew)
    {
        if (Dwm.GetCloaked(hwnd) != 0) return; // 평소 대기 상태 (토스트 없음)
        if (!User32.GetWindowRect(hwnd, out RECT r) || r.Left <= OffscreenThreshold) return;

        _movedHwnd = hwnd;
        _origLeft = r.Left;
        _origTop = r.Top;
        if (!User32.SetWindowPos(hwnd, IntPtr.Zero, OffscreenPos, OffscreenPos, 0, 0, MoveFlags))
        {
            Log.Warn($"토스트 숨기기: SetWindowPos 실패 (오류 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return;
        }
        if (countAsNew)
        {
            _hiddenCount++;
            Log.Info("윈도우 기본 알림 팝업 숨김");
        }
    }

    private static bool IsOffscreen(IntPtr hwnd) =>
        User32.GetWindowRect(hwnd, out RECT r) && r.Left <= OffscreenThreshold;

    /// <summary>끌 때 아직 토스트가 떠 있으면(클로킹 해제 + 화면 밖) 원래 자리로 되돌림. 대기 상태면 셸이 알아서 되돌리므로 그대로 둔다.</summary>
    private void RestoreMoved()
    {
        IntPtr hwnd = _movedHwnd;
        _movedHwnd = IntPtr.Zero;
        if (hwnd == IntPtr.Zero || Dwm.GetCloaked(hwnd) != 0 || !IsOffscreen(hwnd)) return;
        User32.SetWindowPos(hwnd, IntPtr.Zero, _origLeft, _origTop, 0, 0, MoveFlags);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Enabled) Disable();
        _disposed = true;
    }
}
