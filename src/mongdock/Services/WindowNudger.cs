using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mongdock.Native;
using K = Mongdock.Native.KeyboardHookApi;
using W = Mongdock.Native.WindowPosApi;

namespace Mongdock.Services;

/// <summary>
/// 창이 상단바 밑으로 들어가면 상단바 바로 아래로 내려 준다 (맥처럼).
/// 캡처 도구(WinUI) 등 일부 앱은 작업 영역(AppBar 예약)을 무시하고 모니터 맨 위(y=0)에 창을 띄워
/// 제목·도구 줄이 Topmost 상단바에 가려진다. 사용자가 상단바 밑으로 끌어 놓은 창도 같은 처리.
///
/// 동작:
/// - 전용 스레드의 WinEvent 훅(WINEVENT_OUTOFCONTEXT | SKIPOWNPROCESS): EVENT_OBJECT_SHOW / EVENT_OBJECT_UNCLOAKED 때
///   바로 1회 + 150ms 뒤 1회(앱이 직접 위치를 다시 잡는 경우), EVENT_SYSTEM_MOVESIZEEND(끌어서 놓음) 때 1회.
///   LOCATIONCHANGE 는 쓰지 않는다 (창을 계속 따라다니지 않음).
/// - 기준: <see cref="DesktopWindowService.TopBarRectsPx"/> (공간을 예약한 상단바, 물리 px). 비어 있으면(상단바 꺼짐,
///   공간 예약 꺼짐) 아무것도 안 한다. 여러 모니터면 창이 있는 모니터의 상단바 기준.
/// - 대상: 보이는 최상위 창(자식·도구 창·활성화 안 되는 창 제외), 제목 줄이 있거나(owner 있는 창은 제목 줄 필수) 크기 조절 테두리가
///   있는 창, 셸 창·메뉴·툴팁 클래스 제외, 클로킹 아님, 200×120 이상, 최소화·최대화·전체 화면 아님,
///   창 높이가 그 모니터 작업 영역 높이 이하. 보이는 테두리(DWMWA_EXTENDED_FRAME_BOUNDS)의 top 이 상단바 bottom 보다 위면
///   그만큼 아래로 (크기는 그대로 — SWP_NOSIZE|NOZORDER|NOACTIVATE|ASYNCWINDOWPOS).
/// - 싸우지 않기: 창마다 최근 조정 시각을 기록해 1초 안에 3번 넘게 옮기게 되면 그 창은 포기(창이 없어질 때까지).
/// - 관리자 권한 창(UIPI)은 SetWindowPos 가 실패 — 무시 (시도 횟수엔 들어가 곧 포기).
/// SetEnabled 는 아무 스레드에서나 호출 가능.
/// </summary>
public sealed class WindowNudger : IDisposable
{
    private const int RecheckDelayMs = 150;
    private const uint TimerIntervalMs = 50;
    private const int MinWidth = 200, MinHeight = 120;
    private const int FightWindowMs = 1000;
    private const int FightMaxMoves = 3;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private const uint MoveFlags = User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE |
                                   W.SWP_NOOWNERZORDER | W.SWP_ASYNCWINDOWPOS;

    /// <summary>건드리지 않는 창 클래스 (셸·메뉴·툴팁·드롭다운·작업 전환 등).</summary>
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "#32768",                       // 메뉴
        "#32769",                       // 데스크톱
        "#32771",                       // Alt+Tab (클래식)
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow",   // 시작·검색·알림 센터·빠른 설정·토스트
        "XamlExplorerHostIslandWindow", // Alt+Tab·작업 보기 (Win11)
        "MultitaskingViewFrame", "ForegroundStaging", "TaskListThumbnailWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Xaml_WindowedPopupClass", "Windows.UI.Composition.DesktopWindowContentBridge",
        "tooltips_class32", "SysShadow", "DropDown", "ComboLBox",
        "Shell_InputSwitchTopLevelWindow", "ApplicationManager_ImmersiveShellWindow",
        "ImmersiveLauncher", "ImmersiveSwitchList", "EdgeUiInputTopWndClass", "EdgeUiInputWndClass",
        "Shell_Dim", "Shell_LightDismissOverlay",
    };

    private sealed class WinState
    {
        public readonly Queue<long> Moves = new();
        public bool GaveUp;
        public long LastSeen;
    }

    private readonly object _gate = new();
    private readonly WinEventApi.WinEventProc _proc;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _enabled;
    private bool _disposed;

    // ── 아래는 훅 스레드에서만 접근 ──
    private readonly Dictionary<IntPtr, WinState> _states = new();
    private readonly Dictionary<IntPtr, long> _pending = new();
    private UIntPtr _timer;
    private readonly StringBuilder _classBuf = new(256);

    public WindowNudger()
    {
        _proc = OnWinEvent;
    }

    /// <summary>시험용: 0 이 아니면 이 프로세스의 창만 건드린다 (사용자 창을 건드리지 않고 확인할 때).</summary>
    internal uint OnlyProcessId
    {
        get => Volatile.Read(ref _onlyProcessId);
        set => Volatile.Write(ref _onlyProcessId, value);
    }
    private uint _onlyProcessId;

    public bool IsRunning
    {
        get { lock (_gate) return _thread is not null; }
    }

    /// <summary>켜기/끄기 (켜면 전용 훅 스레드 시작, 끄면 훅 해제·스레드 종료).</summary>
    public void SetEnabled(bool on)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = on;
            if (on && _thread is null) Start();
            else if (!on && _thread is not null) Stop();
        }
    }

    private void Start()
    {
        var ready = new ManualResetEventSlim(false);
        var t = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "mongdock window nudger",
        };
        _thread = t;
        t.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(3)))
            Log.Warn("창 내리기: 훅 스레드 시작 대기 시간 초과");
    }

    private void Stop()
    {
        var t = _thread;
        if (t is null) return;
        uint id = _threadId;
        if (id != 0 && !K.PostThreadMessage(id, K.WM_QUIT, IntPtr.Zero, IntPtr.Zero))
            Log.Warn($"창 내리기: 스레드 종료 요청 실패 err={Marshal.GetLastWin32Error()}");
        if (Thread.CurrentThread != t && !t.Join(2000))
            Log.Warn("창 내리기: 훅 스레드가 제때 끝나지 않음");
        _thread = null;
        _threadId = 0;
    }

    private void Run(ManualResetEventSlim ready)
    {
        var hooks = new List<IntPtr>(3);
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();
            K.PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // 메시지 큐 만들기 (PostThreadMessage 용)

            const uint flags = WinEventApi.WINEVENT_OUTOFCONTEXT | WinEventApi.WINEVENT_SKIPOWNPROCESS;
            foreach (uint ev in new[] { WinEventApi.EVENT_SYSTEM_MOVESIZEEND, WinEventApi.EVENT_OBJECT_SHOW, WinEventApi.EVENT_OBJECT_UNCLOAKED })
            {
                IntPtr h = WinEventApi.SetWinEventHook(ev, ev, IntPtr.Zero, _proc, 0, 0, flags);
                if (h == IntPtr.Zero) Log.Warn($"창 내리기: SetWinEventHook(0x{ev:X}) 실패 err={Marshal.GetLastWin32Error()}");
                else hooks.Add(h);
            }
            Log.Info($"창 내리기 시작 (훅 {hooks.Count}개, 스레드 {_threadId})");
            ready.Set();
            if (hooks.Count == 0) return;

            while (true)
            {
                int r = K.GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (r == 0) break; // WM_QUIT
                if (r == -1)
                {
                    Log.Warn($"창 내리기: GetMessage 실패 err={Marshal.GetLastWin32Error()}");
                    break;
                }
                // 스레드 타이머 (hwnd 0, 콜백 없음) → 예약된 재확인
                if (msg.message == W.WM_TIMER && msg.hwnd == IntPtr.Zero)
                    ProcessPending();
                // 창이 없는 스레드라 디스패치할 것 없음 (WinEvent 콜백은 GetMessage 안에서 호출됨)
            }
        }
        catch (Exception e)
        {
            Log.Error("창 내리기 스레드 예외", e);
        }
        finally
        {
            foreach (var h in hooks) WinEventApi.UnhookWinEvent(h);
            if (_timer != UIntPtr.Zero) { W.KillTimer(IntPtr.Zero, _timer); _timer = UIntPtr.Zero; }
            _pending.Clear();
            _states.Clear();
            ready.Set();
            Log.Info("창 내리기 중지");
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (!_enabled || hwnd == IntPtr.Zero) return;
            if (idObject != WinEventApi.OBJID_WINDOW || idChild != WinEventApi.CHILDID_SELF) return;
            if (DesktopWindowService.TopBarRectsPx.Length == 0) return;
            // 최상위 창만 (SHOW 는 자식 창에도 잔뜩 온다 — 가장 싼 검사 먼저)
            if (W.GetAncestor(hwnd, W.GA_ROOT) != hwnd) return;

            TryNudge(hwnd, eventType);
            if (eventType != WinEventApi.EVENT_SYSTEM_MOVESIZEEND)
                Schedule(hwnd, Environment.TickCount64 + RecheckDelayMs);
        }
        catch
        {
            // 콜백 밖으로 예외를 내보내지 않음
        }
    }

    private void Schedule(IntPtr hwnd, long due)
    {
        _pending[hwnd] = due;
        if (_timer == UIntPtr.Zero)
            _timer = W.SetTimer(IntPtr.Zero, UIntPtr.Zero, TimerIntervalMs, IntPtr.Zero);
    }

    private void ProcessPending()
    {
        long now = Environment.TickCount64;
        List<IntPtr>? due = null;
        foreach (var (hwnd, at) in _pending)
            if (at <= now) (due ??= new()).Add(hwnd);
        if (due is not null)
        {
            foreach (var hwnd in due)
            {
                _pending.Remove(hwnd);
                try { if (_enabled) TryNudge(hwnd, 0); } catch { }
            }
        }
        if (_pending.Count == 0 && _timer != UIntPtr.Zero)
        {
            W.KillTimer(IntPtr.Zero, _timer);
            _timer = UIntPtr.Zero;
        }
        if (_states.Count > 128) PruneStates(now);
    }

    private void PruneStates(long now)
    {
        var dead = _states.Where(kv => kv.Value.GaveUp ? !User32.IsWindow(kv.Key) : now - kv.Value.LastSeen > 10_000)
                          .Select(kv => kv.Key).ToList();
        foreach (var h in dead) _states.Remove(h);
    }

    /// <summary>조건에 맞으면 창을 상단바 아래로 옮긴다. 옮겼으면 true.</summary>
    private bool TryNudge(IntPtr hwnd, uint eventType)
    {
        var bars = DesktopWindowService.TopBarRectsPx;
        if (bars.Length == 0) return false;
        if (!User32.IsWindow(hwnd) || !User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd) || W.IsZoomed(hwnd)) return false;
        if (W.GetAncestor(hwnd, W.GA_ROOT) != hwnd) return false;

        long style = User32.GetWindowLong(hwnd, User32.GWL_STYLE);
        if ((style & W.WS_CHILD) != 0) return false;
        long ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
        if ((ex & (User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE)) != 0) return false;
        bool caption = (style & W.WS_CAPTION) == W.WS_CAPTION;
        bool sizable = (style & W.WS_THICKFRAME) != 0;
        bool owned = User32.GetWindow(hwnd, User32.GW_OWNER) != IntPtr.Zero;
        // owner 있는 팝업(드롭다운·메뉴·툴팁 등)은 제목 줄이 있을 때만, owner 없는 창은 제목 줄 또는 크기 조절 테두리(테두리 없는 앱 창)
        if (owned ? !caption : !(caption || sizable)) return false;

        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _ownPid || pid == 0) return false;
        if (OnlyProcessId != 0 && pid != OnlyProcessId) return false;

        _classBuf.Clear();
        if (User32.GetClassName(hwnd, _classBuf, _classBuf.Capacity) > 0 && ExcludedClasses.Contains(_classBuf.ToString())) return false;
        if (Dwm.GetCloaked(hwnd) != 0) return false;

        if (!W.TryGetFrameBounds(hwnd, out RECT frame)) return false;
        if (frame.Width < MinWidth || frame.Height < MinHeight) return false;

        IntPtr mon = DesktopApi.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (!DesktopApi.TryGetMonitorRects(mon, out RECT screen, out RECT work)) return false;
        // 전체 화면 (모니터를 다 덮음)
        if (frame.Left <= screen.Left && frame.Top <= screen.Top && frame.Right >= screen.Right && frame.Bottom >= screen.Bottom) return false;

        RECT bar = default;
        bool found = false;
        foreach (var b in bars)
        {
            int cx = b.Left + b.Width / 2, cy = b.Top + b.Height / 2;
            if (cx >= screen.Left && cx < screen.Right && cy >= screen.Top && cy < screen.Bottom) { bar = b; found = true; break; }
        }
        if (!found) return false;

        // 상단바와 겹치는지 (가로로도 겹쳐야 함)
        if (frame.Top >= bar.Bottom || frame.Bottom <= bar.Top) return false;
        if (frame.Right <= bar.Left || frame.Left >= bar.Right) return false;
        // 작업 영역보다 큰 창은 내리면 아래가 잘리므로 그대로 (최대화 비슷한 창)
        if (frame.Height > work.Height) return false;

        long now = Environment.TickCount64;
        if (!_states.TryGetValue(hwnd, out var st)) _states[hwnd] = st = new WinState();
        st.LastSeen = now;
        if (st.GaveUp) return false;
        while (st.Moves.Count > 0 && now - st.Moves.Peek() > FightWindowMs) st.Moves.Dequeue();
        if (st.Moves.Count >= FightMaxMoves)
        {
            st.GaveUp = true;
            _pending.Remove(hwnd);
            Log.Info($"창 내리기: 창이 계속 제자리로 돌아가 포기 (class={_classBuf}, pid={pid})");
            return false;
        }
        st.Moves.Enqueue(now);

        if (!User32.GetWindowRect(hwnd, out RECT wr)) return false;
        int dy = bar.Bottom - frame.Top;
        if (dy <= 0) return false;
        bool ok = User32.SetWindowPos(hwnd, IntPtr.Zero, wr.Left, wr.Top + dy, 0, 0, MoveFlags);
        if (ok)
            Log.Info($"창 내리기: class={_classBuf} pid={pid} {frame} → {dy}px 아래 ({EventName(eventType)})");
        else
            Log.Info($"창 내리기 실패 (권한 등, 무시): class={_classBuf} pid={pid} err={Marshal.GetLastWin32Error()}");
        return ok;
    }

    private static string EventName(uint ev) => ev switch
    {
        WinEventApi.EVENT_OBJECT_SHOW => "표시",
        WinEventApi.EVENT_OBJECT_UNCLOAKED => "클로킹 해제",
        WinEventApi.EVENT_SYSTEM_MOVESIZEEND => "끌어서 놓음",
        _ => "재확인",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = false;
            Stop();
            _disposed = true;
        }
    }
}
