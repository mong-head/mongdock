using System.Diagnostics;
using System.Windows.Threading;
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
/// 몽독 배너가 실제로 나간 토스트만 숨긴다 (안 그러면 파싱 실패·SuppressPopup·같은 태그 반복·DB 읽기 실패 등으로
/// 배너가 안 뜬 알림을 아예 못 보게 됨):
/// - 토스트 창이 뜨면(클로킹 해제) 일단 화면 밖으로 옮겨 두고 "확인 대기". 셸 창이 DB 갱신보다 먼저 뜰 수 있어
///   <see cref="NotificationService.RequestReadNow"/> 로 곧바로 DB 를 다시 읽게 한다.
/// - 뜬 시점 전후 <see cref="LookbackMs"/> 안에 NotificationService 가 배너(Arrived)를 냈으면 확정 → 계속 숨김.
/// - <see cref="ConfirmTimeoutMs"/> 안에 확인되지 않으면 원래 자리로 복원 (조금 늦게 뜨는 셈).
/// - 새 토스트 중 scenario 가 reminder/alarm/incomingCall/urgent(사용자 조작 필요)인 것이 있으면 숨기지 않고 복원.
///
/// 판별은 엄격하게, 그리고 다른 프로세스에 메시지를 보내지 않게(셸이 바쁘면 몽독 UI 가 멈추므로):
/// pid(캐시된 ShellExperienceHost) → 클래스 → 크기(모니터 너비의 60% 미만) → 제목(InternalGetWindowText — WM_GETTEXT 안 보냄).
/// 알림 센터("알림 센터")·빠른 설정·시작 메뉴·검색 창은 제목/프로세스가 달라 걸리지 않는다.
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
    /// <summary>토스트가 뜬 시점보다 이만큼 전의 배너까지 "이 토스트의 배너" 로 인정 (DB 가 먼저 갱신된 경우).</summary>
    private const long LookbackMs = 2500;
    /// <summary>
    /// 뜬 뒤 이 시간 안에 배너가 확인되지 않으면 셸 팝업을 원래 자리로 복원.
    /// 디스코드·Claude 등은 DB 반영이 셸 창보다 ~700ms 늦으므로 여유 있게 (그동안 팝업은 화면 밖이라 보이지 않음).
    /// </summary>
    private const long ConfirmTimeoutMs = 1500;

    // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS (셸 스레드가 바빠도 몽독이 막히지 않게)
    private const uint MoveFlags = 0x0001 | 0x0004 | 0x0010 | 0x0200 | 0x4000;

    /// <summary>토스트 팝업 창 제목 (윈도우 표시 언어별). 알림 센터 창 제목("알림 센터"/"Notification Center")과는 다르다.</summary>
    private static readonly HashSet<string> ToastTitles = new(StringComparer.Ordinal)
    {
        "새 알림", "New notification", "新しい通知", "新通知", "新通知。",
    };

    private enum State
    {
        /// <summary>토스트 없음 (또는 아직 못 봄).</summary>
        Idle,
        /// <summary>일단 화면 밖에 두고 배너 확인 대기.</summary>
        Pending,
        /// <summary>배너 확인됨 → 계속 화면 밖.</summary>
        Hidden,
        /// <summary>배너 없음/사용자 조작 필요 → 원래 자리 (이 토스트가 끝날 때까지 손대지 않음).</summary>
        Shown,
    }

    private readonly NotificationService? _notifications;
    private readonly WinEventApi.WinEventProc _proc;
    private readonly DispatcherTimer _confirmTimer;
    private IntPtr _uncloakHook;
    private IntPtr _locationHook;
    private uint _hostPid;
    private readonly Dictionary<uint, bool> _pidIsHost = new();

    // 현재 토스트 창의 상태와 옮기기 전 위치 (복원·끌 때 되돌리기용)
    private State _state = State.Idle;
    private IntPtr _toastHwnd;
    private int _origLeft, _origTop, _lastHeight;
    private bool _hasOrig;
    private long _sessionTick;
    /// <summary>마지막으로 숨김을 확정할 때 쓴 배너 시각 — 배너 하나로 토스트 둘을 숨기지 않게.</summary>
    private long _usedBannerTick;
    private int _hiddenCount, _shownCount;
    private bool _disposed;

    /// <param name="notifications">배너 확인용. null 이면 아무 토스트도 숨기지 않는다 (확인할 방법이 없으므로).</param>
    public NativeToastSuppressor(NotificationService? notifications)
    {
        _notifications = notifications;
        _proc = OnWinEvent; // 델리게이트를 필드에 잡아 GC 로 수거되지 않게
        _confirmTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        _confirmTimer.Tick += (_, _) => OnConfirmTick();
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
        if (_notifications is not null) _notifications.ToastActivity += OnToastActivity;
        HookHost(FindHostPid());
        Log.Info($"토스트 숨기기 켬 (ShellExperienceHost pid={_hostPid})");
        SweepExisting();
    }

    private void Disable()
    {
        Enabled = false;
        if (_notifications is not null) _notifications.ToastActivity -= OnToastActivity;
        _confirmTimer.Stop();
        Unhook(ref _locationHook);
        Unhook(ref _uncloakHook);
        _hostPid = 0;
        RestoreMoved();
        _state = State.Idle;
        Log.Info($"토스트 숨기기 끔 (숨김 {_hiddenCount}회, 그대로 둠 {_shownCount}회)");
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
        _pidIsHost[pid] = true;
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

    /// <summary>켤 때 이미 떠 있는 토스트가 있으면 새로 뜬 것처럼 처리 (최근 배너가 없으면 그대로 둠).</summary>
    private void SweepExisting()
    {
        IntPtr h = IntPtr.Zero;
        while ((h = User32.FindWindowEx(IntPtr.Zero, h, ToastClass, null)) != IntPtr.Zero)
        {
            if (IsToastWindow(h, out uint pid) && Dwm.GetCloaked(h) == 0)
            {
                HookHost(pid);
                BeginSession(h);
            }
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!Enabled || hwnd == IntPtr.Zero || idObject != WinEventApi.OBJID_WINDOW || idChild != WinEventApi.CHILDID_SELF) return;
        try
        {
            if (!IsToastWindow(hwnd, out uint pid)) return;
            if (Dwm.GetCloaked(hwnd) != 0)
            {
                // 평소 대기 상태 (토스트 끝남) → 다음 토스트는 새로
                if (hwnd == _toastHwnd) EndSession();
                return;
            }
            if (eventType == WinEventApi.EVENT_OBJECT_UNCLOAKED)
            {
                HookHost(pid);
                BeginSession(hwnd);
                return;
            }
            OnLocationChange(hwnd);
        }
        catch (Exception ex)
        {
            Log.Warn("토스트 숨기기 처리 실패", ex);
        }
    }

    /// <summary>
    /// 다른 프로세스에 메시지를 보내지 않는 판별 (셸이 바빠도 막히지 않게): pid → 클래스 → 크기 → 제목(InternalGetWindowText).
    /// </summary>
    private bool IsToastWindow(IntPtr hwnd, out uint pid)
    {
        User32.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0 || !IsHostPid(pid)) return false;
        if (User32.GetClassNameOf(hwnd) != ToastClass) return false;

        // 크기: 토스트는 좁은 카드(여러 개면 세로로 쌓임). 모니터 너비의 60% 이상이면 토스트가 아님.
        if (!User32.GetWindowRect(hwnd, out RECT r)) return false;
        if (r.Left > OffscreenThreshold) // 이미 우리가 옮긴 상태면 크기 검사 생략
        {
            int width = r.Width;
            if (width < 100) return false;
            IntPtr monitor = DesktopApi.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (!DesktopApi.TryGetMonitorRects(monitor, out RECT bounds, out _)) return false;
            if (width >= bounds.Width * 0.6 || r.Height > bounds.Height) return false;
        }
        return ToastTitles.Contains(User32.GetWindowTitleNoMessage(hwnd));
    }

    private bool IsHostPid(uint pid)
    {
        if (pid == _hostPid) return true;
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

    // ───────────────────────── 토스트 한 번 (뜸 → 확인 → 숨김/복원) ─────────────────────────

    /// <summary>토스트가 새로 뜸: 일단 화면 밖으로 옮기고 배너 확인 대기.</summary>
    private void BeginSession(IntPtr hwnd)
    {
        _toastHwnd = hwnd;
        _sessionTick = Environment.TickCount64;
        _state = State.Pending;
        _hasOrig = false;
        MoveOffscreen(hwnd);
        _notifications?.RequestReadNow(); // 셸 창이 DB 갱신보다 먼저 뜰 수 있음 → 디바운스 없이 바로 확인
        if (!Evaluate()) _confirmTimer.Start();
    }

    private void EndSession()
    {
        _confirmTimer.Stop();
        _state = State.Idle;
        _toastHwnd = IntPtr.Zero;
    }

    /// <summary>셸이 토스트 창을 다시 배치함 (자리 잡기·여러 개 쌓임).</summary>
    private void OnLocationChange(IntPtr hwnd)
    {
        if (hwnd != _toastHwnd || _state == State.Idle)
        {
            // 클로킹 해제 이벤트를 놓친 토스트 (몽독이 켜지기 전 등) → 새로 뜬 것으로
            BeginSession(hwnd);
            return;
        }
        if (!User32.GetWindowRect(hwnd, out RECT r)) return;
        bool offscreen = r.Left <= OffscreenThreshold;
        switch (_state)
        {
            case State.Pending:
                if (!offscreen) MoveOffscreen(hwnd); // 확인 전까지는 밖에
                break;
            case State.Hidden:
                if (offscreen) break;
                // 셸이 다시 화면 안으로 배치 → 그냥 다시 숨김. 높이가 바뀌는 것만으로는 새 토스트로 보지 않는다
                // (디스코드 등은 같은 토스트를 뜬 뒤 다시 배치함). 새 토스트가 쌓였는지는 DB 의 새 행(OnToastActivity)으로 판단.
                MoveOffscreen(hwnd);
                break;
            case State.Shown:
                if (!offscreen) _lastHeight = r.Height;
                break;
        }
    }

    private void OnConfirmTick()
    {
        if (_state != State.Pending || !Enabled)
        {
            _confirmTimer.Stop();
            return;
        }
        _notifications?.RequestReadNow();
        if (Evaluate()) _confirmTimer.Stop();
    }

    /// <summary>NotificationService 가 새 토스트 행을 반영함 (UI 스레드).</summary>
    private void OnToastActivity(object? sender, EventArgs e)
    {
        if (!Enabled) return;
        if (_state == State.Pending)
        {
            if (Evaluate()) _confirmTimer.Stop();
        }
        else if (_state == State.Hidden && IsInteractiveSince(_sessionTick - LookbackMs))
        {
            // 숨긴 뒤에 들어온 사용자 조작 필요 토스트 → 보여 줌
            Show("사용자 조작이 필요한 알림");
        }
        else if (_state == State.Hidden && _notifications is not null)
        {
            // 숨긴 셸 창에 토스트가 더 쌓이거나 같은 토스트가 갱신됨(디스코드 등, 같은 태그라 배너 생략)
            // → 계속 숨김. 새 배너가 나갔으면 그 배너는 이 창 몫으로 소비.
            _usedBannerTick = Math.Max(_usedBannerTick, _notifications.LastBannerTick);
        }
    }

    /// <summary>대기 중 판정. 결론이 나면 true (숨김 확정 또는 복원).</summary>
    private bool Evaluate()
    {
        if (_state != State.Pending) return true;
        long since = _sessionTick - LookbackMs;
        if (_notifications is null)
        {
            Show("배너 확인 불가");
            return true;
        }
        if (IsInteractiveSince(since))
        {
            Show("사용자 조작이 필요한 알림");
            return true;
        }
        long banner = _notifications.LastBannerTick;
        if (banner != 0 && banner >= since && banner > _usedBannerTick)
        {
            _usedBannerTick = banner;
            _state = State.Hidden;
            _hiddenCount++;
            Log.Info($"윈도우 기본 알림 팝업 숨김 (몽독 배너 확인 {Environment.TickCount64 - _sessionTick}ms)");
            return true;
        }
        if (Environment.TickCount64 - _sessionTick >= ConfirmTimeoutMs)
        {
            Show("몽독 배너 없음");
            return true;
        }
        return false;
    }

    private bool IsInteractiveSince(long since) =>
        _notifications is not null && _notifications.LastInteractiveToastTick != 0 && _notifications.LastInteractiveToastTick >= since;

    /// <summary>원래 자리로 되돌리고 이 토스트가 끝날 때까지 손대지 않음.</summary>
    private void Show(string reason)
    {
        _confirmTimer.Stop();
        _state = State.Shown;
        _shownCount++;
        RestoreMoved(keepHwnd: true);
        Log.Info($"윈도우 기본 알림 팝업 그대로 둠 ({reason})");
    }

    private void MoveOffscreen(IntPtr hwnd)
    {
        if (!User32.GetWindowRect(hwnd, out RECT r) || r.Left <= OffscreenThreshold) return;
        _lastHeight = r.Height;
        // 셸이 크기를 바꾸는 중간에 (0,0) 같은 임시 위치를 거칠 수 있음 → 오른쪽 아래에 붙은 정상 위치만 기억
        if (IsPlausiblePlacement(r))
        {
            _origLeft = r.Left;
            _origTop = r.Top;
            _hasOrig = true;
        }
        if (!User32.SetWindowPos(hwnd, IntPtr.Zero, OffscreenPos, OffscreenPos, 0, 0, MoveFlags))
            Log.Warn($"토스트 숨기기: SetWindowPos 실패 (오류 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
    }

    /// <summary>아직 토스트가 떠 있으면(클로킹 해제 + 화면 밖) 원래 자리로 되돌림. 대기 상태면 셸이 알아서 되돌리므로 그대로 둔다.</summary>
    private void RestoreMoved(bool keepHwnd = false)
    {
        IntPtr hwnd = _toastHwnd;
        if (!keepHwnd) _toastHwnd = IntPtr.Zero;
        if (hwnd == IntPtr.Zero || Dwm.GetCloaked(hwnd) != 0 || !User32.GetWindowRect(hwnd, out RECT r) || r.Left > OffscreenThreshold) return;
        int x = _origLeft, y = _origTop;
        if (!_hasOrig || !IsPlausiblePlacement(new RECT { Left = x, Top = y, Right = x + r.Width, Bottom = y + r.Height }))
        {
            // 기억한 위치가 없거나 이상하면 주 모니터 작업 영역 오른쪽 아래 (셸의 기본 토스트 자리)
            if (!DesktopApi.TryGetMonitorRects(DesktopApi.PrimaryMonitor, out _, out RECT work)) return;
            x = work.Right - r.Width;
            y = work.Bottom - r.Height;
        }
        User32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, MoveFlags);
    }

    /// <summary>토스트의 정상 자리: 어떤 모니터의 오른쪽 가장자리에 붙고(±48px) 아래쪽 300px 안에서 끝남.</summary>
    private static bool IsPlausiblePlacement(RECT r)
    {
        IntPtr monitor = DesktopApi.MonitorFromPoint(new POINT { X = r.Right - 1, Y = r.Bottom - 1 }, DesktopApi.MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero || !DesktopApi.TryGetMonitorRects(monitor, out RECT bounds, out _)) return false;
        return Math.Abs(r.Right - bounds.Right) <= 48 && r.Bottom <= bounds.Bottom && r.Bottom >= bounds.Bottom - 300
               && r.Left >= bounds.Left && r.Top >= bounds.Top;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Enabled) Disable();
        _confirmTimer.Stop();
        _disposed = true;
    }
}
