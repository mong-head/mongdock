using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyDock.Native;
using T = MyDock.Native.TrayApi;

namespace MyDock.Services;

/// <summary>
/// 다른 앱의 트레이 아이콘 수집 (ManagedShell TrayService 방식, MIT 참고).
///
/// 동작:
/// - 전용 STA 스레드에 숨은 최상위 창(클래스 "Shell_TrayWnd", 자식 "TrayNotifyWnd")을 만들고 HWND_TOPMOST 로 올린다.
///   Shell_NotifyIcon·SHAppBarMessage 는 FindWindow("Shell_TrayWnd") 로 Z 순서 맨 위의 창을 찾으므로 이 창이 먼저 받는다.
///   explorer 가 자기 작업 표시줄을 다시 올리면 100ms 타이머가 다시 위로 올린다.
/// - 받은 메시지는 처리 후(트레이 데이터만 읽음) explorer 의 Shell_TrayWnd 로 그대로 전달 → explorer 트레이·AppBar 는 그대로 동작.
///   WM_COPYDATA dwData=0(AppBar 메시지 — 몽독 자신의 상단바·독 포함)은 결과 공유 메모리만 explorer 용으로 바꿔 중계 (<see cref="ForwardAppBar"/>).
/// - 동기 SendMessage 로 들어오므로 빨리 처리: 전달은 SendMessageTimeout(SMTO_ABORTIFHUNG). UI 스레드와 독립된 스레드라 UI 가 바빠도 앱이 안 멈춤.
/// - 시작 시 이미 등록된 아이콘은 "TaskbarCreated" 를 다른 앱(몽독·explorer 제외)의 최상위 창에만 보내 다시 등록하게 해서 얻는다.
/// - 창이 사라지면(끄기·종료·크래시) 앱들은 자동으로 explorer 로 보낸다. explorer 는 그동안 모든 메시지를 전달받았으므로 재등록 불필요
///   (전달 실패가 있었을 때만 끌 때 TaskbarCreated 를 다시 보냄).
/// - 창은 항상 숨김이고 크기·위치는 explorer 작업 표시줄과 같게 맞춘다(FindWindow+GetWindowRect 로 작업 표시줄 위치를 재는 앱 대비).
/// </summary>
public sealed class TrayIconService : ITrayIconService, IDisposable
{
    private const int TimerRaise = 1;       // 100ms: Z 순서 맨 위 유지
    private const int TimerHousekeep = 2;   // 2s: explorer 창 다시 찾기·위치 맞추기·죽은 아이콘 정리
    private const int TimerReplay = 3;      // 시작 직후: TaskbarCreated 재등록 요청 (ReplayPasses 간격으로 여러 번)
    /// <summary>
    /// 재등록 요청 간격 (ms, 앞 요청 기준): 0.2초 뒤 모든 앱에 한 번, 그 뒤 시작 1·3·8초쯤에는 <b>빠진 앱만</b> 골라 다시
    /// (이미 잡힌 앱에 다시 보내면 DELETE→ADD 로 아이콘이 깜빡임 — 실측).
    /// </summary>
    private static readonly uint[] ReplayPasses = { 200, 800, 2000, 5000 };
    private int _replayCount;
    private const uint ForwardTimeoutMs = 3000;
    private const uint AppBarForwardTimeoutMs = 5000;

    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();          // _gate
    private readonly Dictionary<uint, (string Name, string Path)> _procNames = new(); // 트레이 스레드 전용
    private readonly T.WndProc _wndProc;                     // GC 방지
    private readonly uint _selfPid = (uint)Environment.ProcessId;
    private readonly uint _taskbarCreatedMsg;

    private readonly object _lifeGate = new();
    private Thread? _thread;
    private uint _threadId;
    private volatile IntPtr _trayHwnd;
    private IntPtr _notifyHwnd;
    private IntPtr _explorerTray;    // 트레이 스레드 전용
    private RECT _mirroredRect;
    private bool _enabled;
    private bool _disposed;
    private volatile bool _forwardFailed;
    private long _seq;
    private int _errorLogs;
    private bool _publishQueued;     // _gate
    private IReadOnlyList<TrayIconInfo> _icons = Array.Empty<TrayIconInfo>();

    public TrayIconService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _wndProc = WndProc;
        _taskbarCreatedMsg = T.RegisterWindowMessage("TaskbarCreated");
    }

    public bool IsActive => _trayHwnd != IntPtr.Zero;
    public IReadOnlyList<TrayIconInfo> Icons => _icons;
    public event EventHandler? Changed;

    // ───────────────────────── 켜기/끄기 ─────────────────────────

    public void SetEnabled(bool enabled)
    {
        lock (_lifeGate)
        {
            if (_disposed || enabled == _enabled) return;
            _enabled = enabled;
            if (enabled) StartThread();
            else StopThread();
        }
    }

    private void StartThread()
    {
        if (_thread is not null) return;
        var ready = new ManualResetEventSlim(false);
        var t = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "mongdock tray",
            Priority = ThreadPriority.AboveNormal,
        };
        t.SetApartmentState(ApartmentState.STA); // 아이콘 → BitmapSource 변환(WPF) 용
        _thread = t;
        t.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(3)))
            Log.Warn("트레이 창 시작 대기 시간 초과");
    }

    private void StopThread()
    {
        var t = _thread;
        if (t is null) return;
        _thread = null;
        if (_threadId != 0) T.PostThreadMessage(_threadId, T.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != t && !t.Join(TimeSpan.FromSeconds(3)))
            Log.Warn("트레이 스레드 종료 대기 시간 초과");
        _threadId = 0;
        lock (_gate) _entries.Clear();
        QueuePublish();
        if (_forwardFailed)
        {
            // explorer 로의 전달이 실패한 적이 있으면 explorer 트레이가 빠진 아이콘이 있을 수 있음 → 앱들에게 다시 등록 요청
            // (이제 몽독 창이 없으므로 explorer 로 감)
            _forwardFailed = false;
            int n = SendTaskbarCreatedToApps(IntPtr.Zero);
            Log.Info($"트레이 전달 실패가 있었음 → 앱 {n}개 창에 TaskbarCreated 다시 보냄");
        }
    }

    public void Dispose()
    {
        lock (_lifeGate)
        {
            if (_disposed) return;
            _enabled = false;
            StopThread();
            _disposed = true;
        }
    }

    // ───────────────────────── 트레이 스레드 ─────────────────────────

    private void Run(ManualResetEventSlim ready)
    {
        IntPtr hInstance = T.GetModuleHandle(null);
        bool trayClass = false, notifyClass = false;
        try
        {
            _threadId = T.GetCurrentThreadId();
            trayClass = RegisterClass(T.TrayWndClass, hInstance);
            notifyClass = RegisterClass(T.NotifyWndClass, hInstance);

            _explorerTray = T.FindExplorerTray();
            RECT r = default;
            if (_explorerTray != IntPtr.Zero) T.GetWindowRect(_explorerTray, out r);
            _mirroredRect = r;

            // 보이지 않는(WS_VISIBLE 없음) 최상위 도구 창. 활성화·입력 받지 않음.
            IntPtr hwnd = T.CreateWindowEx(T.WS_EX_TOPMOST | T.WS_EX_TOOLWINDOW | T.WS_EX_NOACTIVATE, T.TrayWndClass, "",
                T.WS_POPUP | T.WS_CLIPCHILDREN | T.WS_CLIPSIBLINGS, r.Left, r.Top, r.Width, r.Height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Log.Warn($"트레이 창 만들기 실패 (오류 {Marshal.GetLastWin32Error()})");
                return;
            }
            _trayHwnd = hwnd; // 만들자마자 맨 위 topmost 라 곧바로 메시지를 받을 수 있음
            _notifyHwnd = T.CreateWindowEx(0, T.NotifyWndClass, null, T.WS_CHILD | T.WS_CLIPCHILDREN | T.WS_CLIPSIBLINGS,
                0, 0, 0, 0, hwnd, IntPtr.Zero, hInstance, IntPtr.Zero);
            MirrorNotifyRect();

            // 낮은 무결성 프로세스(샌드박스 앱 등)의 Shell_NotifyIcon 도 받도록 (explorer 도 같은 필터를 연다)
            T.ChangeWindowMessageFilterEx(hwnd, T.WM_COPYDATA, T.MSGFLT_ALLOW, IntPtr.Zero);

            RaiseTopmost();
            T.SetTimer(hwnd, (UIntPtr)TimerRaise, 100, IntPtr.Zero);
            T.SetTimer(hwnd, (UIntPtr)TimerHousekeep, 2000, IntPtr.Zero);
            _replayCount = 0;
            T.SetTimer(hwnd, (UIntPtr)TimerReplay, ReplayPasses[0], IntPtr.Zero);
            Log.Info($"트레이 가로채기 시작 (explorer 트레이 0x{_explorerTray.ToInt64():X})");
            ready.Set();

            while (T.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                T.TranslateMessage(ref msg);
                T.DispatchMessage(ref msg);
            }
        }
        catch (Exception e)
        {
            Log.Error("트레이 스레드 예외", e);
        }
        finally
        {
            IntPtr hwnd = _trayHwnd;
            _trayHwnd = IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                T.KillTimer(hwnd, (UIntPtr)TimerRaise);
                T.KillTimer(hwnd, (UIntPtr)TimerHousekeep);
                T.KillTimer(hwnd, (UIntPtr)TimerReplay);
                T.DestroyWindow(hwnd); // 자식 TrayNotifyWnd 도 함께
            }
            _notifyHwnd = IntPtr.Zero;
            if (notifyClass) T.UnregisterClass(T.NotifyWndClass, hInstance);
            if (trayClass) T.UnregisterClass(T.TrayWndClass, hInstance);
            try { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); } catch { }
            ready.Set();
            if (hwnd != IntPtr.Zero) Log.Info("트레이 가로채기 끝");
        }
    }

    private bool RegisterClass(string name, IntPtr hInstance)
    {
        var wc = new T.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<T.WNDCLASSEX>(),
            style = 0x8, // CS_DBLCLKS (ManagedShell 과 같음)
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = name,
        };
        if (T.RegisterClassEx(ref wc) != 0) return true;
        int err = Marshal.GetLastWin32Error();
        if (err != 1410) Log.Warn($"창 클래스 {name} 등록 실패 (오류 {err})"); // 1410 = 이미 있음
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (hwnd == _trayHwnd && hwnd != IntPtr.Zero)
            {
                if (msg == _taskbarCreatedMsg && msg != 0)
                {
                    // explorer 재시작(또는 다른 셸이 브로드캐스트) → 새 explorer 작업 표시줄 위로 다시 올림. 앱들은 알아서 다시 등록.
                    _explorerTray = T.FindExplorerTray();
                    RaiseTopmost();
                    Log.Info("TaskbarCreated → 트레이 창 다시 맨 위로");
                    return IntPtr.Zero;
                }
                switch (msg)
                {
                    case T.WM_COPYDATA:
                    {
                        IntPtr result = OnCopyData(msg, wParam, lParam);
                        EnsureOnTop();
                        return result;
                    }
                    case T.WM_TIMER:
                        OnTimer((int)wParam.ToInt64());
                        return IntPtr.Zero;
                    case T.WM_WINDOWPOSCHANGING:
                        // 다른 프로그램이 FindWindow("Shell_TrayWnd") 로 이 창을 보이게 해도 숨김 유지
                        if (lParam != IntPtr.Zero)
                        {
                            var wp = Marshal.PtrToStructure<T.WINDOWPOS>(lParam);
                            if ((wp.flags & T.SWP_SHOWWINDOW) != 0)
                            {
                                wp.flags &= ~T.SWP_SHOWWINDOW;
                                Marshal.StructureToPtr(wp, lParam, false);
                            }
                        }
                        break;
                    case T.WM_CLOSE:
                    case T.WM_COMMAND:
                    case T.WM_SYSCOMMAND:
                    case T.WM_HOTKEY:
                        // 작업 표시줄에 보내는 명령(예: WM_COMMAND 419 = 모두 최소화)은 explorer 로. WM_CLOSE 로 이 창이 닫히지 않게 직접 처리
                    {
                        IntPtr result = Forward(msg, wParam, lParam, ForwardTimeoutMs);
                        EnsureOnTop();
                        return result;
                    }
                }
                // explorer 전용 WM_USER 범위 메시지는 그대로 전달. 등록 메시지(0xC000~)는 브로드캐스트일 수 있어 전달하지 않음(explorer 가 두 번 받음)
                if (msg >= T.WM_USER && msg < T.RegisteredMessageFirst)
                {
                    IntPtr result = Forward(msg, wParam, lParam, ForwardTimeoutMs);
                    EnsureOnTop();
                    return result;
                }
            }
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref _errorLogs) <= 20) Log.Error($"트레이 창 메시지 처리 예외 (0x{msg:X})", e);
        }
        return T.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr OnCopyData(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return Forward(msg, wParam, lParam, ForwardTimeoutMs);
        var cds = Marshal.PtrToStructure<T.COPYDATASTRUCT>(lParam);
        long kind = cds.dwData.ToInt64();
        switch (kind)
        {
            case 0:
                // AppBar 메시지 (SHAppBarMessage) — 몽독 자신의 상단바·독 AppBar 포함.
                return ForwardAppBar(wParam, cds);
            case 1:
            {
                bool ours = false;
                try { ours = HandleTrayData(cds.lpData, (int)Math.Min(cds.cbData, int.MaxValue)); }
                catch (Exception e)
                {
                    if (Interlocked.Increment(ref _errorLogs) <= 20) Log.Error("트레이 데이터 처리 예외", e);
                }
                IntPtr r = Forward(msg, wParam, lParam, ForwardTimeoutMs);
                // 둘 중 하나라도 받아들였으면 성공 (예: 재등록 요청으로 온 NIM_ADD 는 explorer 에선 중복이라 실패)
                return ours || r != IntPtr.Zero ? (IntPtr)1 : IntPtr.Zero;
            }
            case 3:
            {
                // Shell_NotifyIconGetRect: 우리가 아는 아이콘 위치(마지막 클릭/호버한 상단바 위치)면 그걸, 아니면 explorer 에 물음
                IntPtr? mine = GetIconRect(cds.lpData, (int)Math.Min(cds.cbData, int.MaxValue));
                return mine ?? Forward(msg, wParam, lParam, ForwardTimeoutMs);
            }
            default:
                return Forward(msg, wParam, lParam, ForwardTimeoutMs);
        }
    }

    /// <summary>
    /// AppBar 메시지 중계. SHAppBarMessage 는 결과용 공유 메모리를 "트레이 창 소유 프로세스"(= 몽독)용으로 만들고
    /// (hSharedMemory, dwSourceProcessId = 그 프로세스 id), 받는 쪽이 SHLockShared 로 열어 결과를 쓴다.
    /// 그대로 explorer 에 넘기면 explorer 는 결과를 써 주지 않는다(실측: ABM_GETTASKBARPOS 빈 사각형, ABM_QUERYPOS 미조정).
    /// → 내용을 explorer 용 공유 메모리(SHAllocShared(..., explorer pid))로 복사해 보내고, 응답 뒤 결과를 원래 메모리로 복사.
    /// 결과 메모리가 없는 메시지(ABM_NEW/REMOVE 등)는 그대로 전달.
    /// </summary>
    private IntPtr ForwardAppBar(IntPtr wParam, T.COPYDATASTRUCT cds)
    {
        IntPtr explorer = _explorerTray;
        if (explorer == IntPtr.Zero || !T.IsWindow(explorer))
        {
            explorer = _explorerTray = T.FindExplorerTray();
            if (explorer == IntPtr.Zero) return IntPtr.Zero;
        }
        if (cds.lpData == IntPtr.Zero || cds.cbData != T.AppBarMsgSize)
            return SendCopyData(explorer, wParam, cds, AppBarForwardTimeoutMs);

        var msgBytes = new byte[T.AppBarMsgSize];
        Marshal.Copy(cds.lpData, msgBytes, 0, msgBytes.Length);
        long hCaller = BitConverter.ToInt64(msgBytes, T.AppBar_hSharedMemory);
        uint callerPid = BitConverter.ToUInt32(msgBytes, T.AppBar_dwSourceProcessId);
        if (hCaller == 0 || callerPid == 0)
            return SendCopyData(explorer, wParam, cds, AppBarForwardTimeoutMs); // 결과 없는 메시지 (ABM_NEW/REMOVE 등)

        IntPtr pCaller = T.SHLockShared((IntPtr)hCaller, callerPid);
        if (pCaller == IntPtr.Zero)
            return SendCopyData(explorer, wParam, cds, AppBarForwardTimeoutMs);
        IntPtr hMine;
        T.GetWindowThreadProcessId(explorer, out uint explorerPid);
        try { hMine = T.SHAllocShared(pCaller, T.AppBarDataSize, explorerPid); }
        finally { T.SHUnlockShared(pCaller); }
        if (hMine == IntPtr.Zero)
            return SendCopyData(explorer, wParam, cds, AppBarForwardTimeoutMs);

        try
        {
            BitConverter.TryWriteBytes(msgBytes.AsSpan(T.AppBar_hSharedMemory, 8), hMine.ToInt64());
            BitConverter.TryWriteBytes(msgBytes.AsSpan(T.AppBar_dwSourceProcessId, 4), explorerPid);
            IntPtr buf = Marshal.AllocHGlobal(msgBytes.Length);
            IntPtr result;
            try
            {
                Marshal.Copy(msgBytes, 0, buf, msgBytes.Length);
                var mine = new T.COPYDATASTRUCT { dwData = IntPtr.Zero, cbData = (uint)msgBytes.Length, lpData = buf };
                result = SendCopyData(explorer, wParam, mine, AppBarForwardTimeoutMs);
            }
            finally { Marshal.FreeHGlobal(buf); }

            // 결과를 호출한 프로세스의 공유 메모리로
            IntPtr pMine = T.SHLockShared(hMine, explorerPid);
            if (pMine != IntPtr.Zero)
            {
                try
                {
                    pCaller = T.SHLockShared((IntPtr)hCaller, callerPid);
                    if (pCaller != IntPtr.Zero)
                    {
                        try
                        {
                            var tmp = new byte[T.AppBarDataSize];
                            Marshal.Copy(pMine, tmp, 0, tmp.Length);
                            Marshal.Copy(tmp, 0, pCaller, tmp.Length);
                        }
                        finally { T.SHUnlockShared(pCaller); }
                    }
                }
                finally { T.SHUnlockShared(pMine); }
            }
            return result;
        }
        finally
        {
            T.SHFreeShared(hMine, explorerPid);
        }
    }

    private IntPtr SendCopyData(IntPtr target, IntPtr wParam, T.COPYDATASTRUCT cds, uint timeoutMs)
    {
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<T.COPYDATASTRUCT>());
        try
        {
            Marshal.StructureToPtr(cds, p, false);
            if (T.SendMessageTimeout(target, T.WM_COPYDATA, wParam, p, T.SMTO_ABORTIFHUNG, timeoutMs, out IntPtr result) == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (Interlocked.Increment(ref _errorLogs) <= 20) Log.Warn($"explorer 로 AppBar 메시지 전달 실패 (오류 {err})");
                return IntPtr.Zero;
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>explorer 의 Shell_TrayWnd 로 그대로 전달. 게시된 메시지는 게시로, 보낸 메시지는 SendMessageTimeout 으로.</summary>
    private IntPtr Forward(uint msg, IntPtr wParam, IntPtr lParam, uint timeoutMs)
    {
        IntPtr target = _explorerTray;
        if (target == IntPtr.Zero || !T.IsWindow(target))
        {
            target = _explorerTray = T.FindExplorerTray();
            if (target == IntPtr.Zero) return IntPtr.Zero;
        }
        uint sm = T.InSendMessageEx(IntPtr.Zero);
        if ((sm & T.ISMEX_SEND) == 0 && msg != T.WM_COPYDATA)
        {
            T.PostMessage(target, msg, wParam, lParam);
            return IntPtr.Zero;
        }
        if (T.SendMessageTimeout(target, msg, wParam, lParam, T.SMTO_ABORTIFHUNG, timeoutMs, out IntPtr result) == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (msg == T.WM_COPYDATA) _forwardFailed = true;
            if (Interlocked.Increment(ref _errorLogs) <= 20) Log.Warn($"explorer 트레이로 전달 실패 (0x{msg:X}, 오류 {err})");
            return IntPtr.Zero;
        }
        return result;
    }

    private void OnTimer(int id)
    {
        switch (id)
        {
            case TimerRaise:
                // explorer 가 자기 작업 표시줄을 다시 올렸으면 (작업 표시줄 클릭 등) 다시 위로
                if (T.FindWindow(T.TrayWndClass, null) != _trayHwnd) RaiseTopmost();
                break;
            case TimerHousekeep:
                Housekeep();
                break;
            case TimerReplay:
                T.KillTimer(_trayHwnd, (UIntPtr)TimerReplay);
                // 보내기 직전에 몽독 창이 맨 위인지 확인 (시작 직후 작업 표시줄 숨기기 등으로 explorer 창이 다시 올라와
                // 앱들의 NIM_ADD 가 explorer 로 새는 경우가 있음 — 실측: 재시작 후 14개 중 5개만 잡힘)
                if (T.FindWindow(T.TrayWndClass, null) != _trayHwnd) RaiseTopmost();
                _replayCount++;
                if (_replayCount == 1)
                {
                    int n = SendTaskbarCreatedToApps(_explorerTray);
                    Log.Info($"트레이 아이콘 재등록 요청 1회차 (TaskbarCreated → 창 {n}개)");
                }
                else
                {
                    // 늦게 반응하는 앱·위의 경우: 트레이 아이콘을 가진 적 있는 앱(윈도우 설정 목록) 중 실행 중인데 아직 없는 것만
                    string missing = SendTaskbarCreatedToMissingApps(_explorerTray);
                    Log.Info($"트레이 아이콘 재등록 확인 {_replayCount}회차: " + (missing.Length > 0 ? "빠진 앱에 다시 요청 → " + missing : "빠진 앱 없음"));
                }
                if (_replayCount < ReplayPasses.Length)
                    T.SetTimer(_trayHwnd, (UIntPtr)TimerReplay, ReplayPasses[_replayCount], IntPtr.Zero);
                break;
        }
    }

    /// <summary>
    /// explorer 로 전달한 뒤 바로 확인: explorer 는 아이콘 삭제 등을 처리하며 자기 작업 표시줄을 다시 맨 위로 올린다.
    /// 그러면 앱이 곧이어 보내는 NIM_ADD(재등록 시 DELETE → ADD)가 explorer 로만 가 몽독 목록에서 빠진다
    /// (실측: 카카오톡·디스코드·Parsec·휴대폰과 연결이 매번 빠짐). 앱은 아직 이 SendMessage 응답을 기다리는 중이므로
    /// 돌려주기 전에 다시 올리면 다음 호출은 확실히 몽독으로 온다.
    /// </summary>
    private void EnsureOnTop()
    {
        if (_trayHwnd != IntPtr.Zero && T.FindWindow(T.TrayWndClass, null) != _trayHwnd) RaiseTopmost();
    }

    private void RaiseTopmost()
    {
        IntPtr h = _trayHwnd;
        if (h == IntPtr.Zero) return;
        T.SetWindowPos(h, T.HWND_TOPMOST, 0, 0, 0, 0, T.SWP_NOMOVE | T.SWP_NOSIZE | T.SWP_NOACTIVATE);
    }

    private void Housekeep()
    {
        // explorer 창 다시 찾기 + 같은 위치·크기로 (작업 표시줄 위치를 창 사각형으로 재는 앱 대비)
        if (_explorerTray == IntPtr.Zero || !T.IsWindow(_explorerTray)) _explorerTray = T.FindExplorerTray();
        if (_explorerTray != IntPtr.Zero && T.GetWindowRect(_explorerTray, out RECT r)
            && (r.Left != _mirroredRect.Left || r.Top != _mirroredRect.Top || r.Right != _mirroredRect.Right || r.Bottom != _mirroredRect.Bottom))
        {
            _mirroredRect = r;
            T.SetWindowPos(_trayHwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, T.SWP_NOZORDER | T.SWP_NOACTIVATE | T.SWP_NOOWNERZORDER);
            MirrorNotifyRect();
        }

        // 소유 창이 사라진 아이콘 정리 (앱이 NIM_DELETE 없이 종료·크래시)
        List<string>? gone = null;
        lock (_gate)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (T.IsWindow(_entries[i].Hwnd)) continue;
                (gone ??= new()).Add(_entries[i].ProcessName);
                _entries.RemoveAt(i);
            }
        }
        if (gone is not null)
        {
            Log.Info($"트레이 아이콘 {gone.Count}개 정리 (창 없음: {string.Join(", ", gone.Distinct())})");
            QueuePublish();
        }
    }

    private void MirrorNotifyRect()
    {
        if (_notifyHwnd == IntPtr.Zero || _explorerTray == IntPtr.Zero) return;
        IntPtr exNotify = T.FindWindowEx(_explorerTray, IntPtr.Zero, T.NotifyWndClass, null);
        if (exNotify == IntPtr.Zero || !T.GetWindowRect(exNotify, out RECT n) || !T.GetWindowRect(_explorerTray, out RECT p)) return;
        T.SetWindowPos(_notifyHwnd, IntPtr.Zero, n.Left - p.Left, n.Top - p.Top, n.Width, n.Height, T.SWP_NOZORDER | T.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 다른 앱(몽독·explorer 제외)의 최상위 창에 TaskbarCreated 를 비동기로 보냄 → 앱들이 트레이 아이콘을 다시 NIM_ADD.
    /// HWND_BROADCAST 를 쓰지 않는 이유: explorer 와 몽독 자신(AppBar 재등록·작업 표시줄 숨김 재적용 등)이 반응하지 않게.
    /// </summary>
    /// <summary>재등록 요청에서 뺄 프로세스 (검증용 — 기본 null).</summary>
    internal Func<uint, bool>? SkipReplayForPid { get; set; }

    private int SendTaskbarCreatedToApps(IntPtr explorerTray)
    {
        if (_taskbarCreatedMsg == 0) return 0;
        uint explorerPid = 0;
        IntPtr ex = explorerTray != IntPtr.Zero ? explorerTray : T.FindExplorerTray();
        if (ex != IntPtr.Zero) T.GetWindowThreadProcessId(ex, out explorerPid);
        int count = 0;
        T.EnumWindows((h, _) =>
        {
            T.GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0 || pid == _selfPid || pid == explorerPid) return true;
            if (SkipReplayForPid?.Invoke(pid) == true) return true;
            if (T.SendNotifyMessage(h, _taskbarCreatedMsg, IntPtr.Zero, IntPtr.Zero)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    /// <summary>
    /// NotifyIconSettings 의 ExecutablePath(트레이 아이콘을 가진 적 있는 앱) 중 지금 실행 중인데 몽독 목록에 아이콘이 하나도 없는
    /// 프로세스에만 TaskbarCreated 를 보냄 — 최상위 창과 메시지 전용 창(HWND_MESSAGE) 모두. 이미 잡힌 앱은 건드리지 않음(깜빡임 방지).
    /// 반환: 로그용 "앱(창 수), ..." (보낸 곳 없으면 "").
    /// </summary>
    private string SendTaskbarCreatedToMissingApps(IntPtr explorerTray)
    {
        if (_taskbarCreatedMsg == 0) return "";
        var reader = NotifyIconSettingsReader.Shared;
        if (!reader.IsAvailable) return "";
        uint explorerPid = 0;
        IntPtr ex = explorerTray != IntPtr.Zero ? explorerTray : T.FindExplorerTray();
        if (ex != IntPtr.Zero) T.GetWindowThreadProcessId(ex, out explorerPid);

        var captured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var e in _entries)
                if (e.ProcessPath.Length > 0) captured.Add(e.ProcessPath);
        }

        // 1) 후보 프로세스: 파일 이름으로 먼저 거르고(싸게) 전체 경로를 확인
        var targets = new Dictionary<uint, string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                uint pid;
                string file;
                try { pid = (uint)p.Id; file = p.ProcessName + ".exe"; }
                catch { continue; }
                if (pid == 0 || pid == _selfPid || pid == explorerPid || !reader.IsKnownFileName(file)) continue;
                string path;
                try { path = Kernel32.QueryProcess(pid).Path; }
                catch { continue; }
                if (path.Length == 0 || captured.Contains(path) || !reader.IsKnownPath(path)) continue;
                targets[pid] = file;
            }
        }
        if (targets.Count == 0) return "";

        // 2) 그 프로세스들의 최상위 창 + 메시지 전용 창에 보냄
        var sent = new Dictionary<uint, int>();
        void Send(IntPtr h)
        {
            T.GetWindowThreadProcessId(h, out uint pid);
            if (!targets.ContainsKey(pid)) return;
            if (T.SendNotifyMessage(h, _taskbarCreatedMsg, IntPtr.Zero, IntPtr.Zero))
                sent[pid] = sent.TryGetValue(pid, out int c) ? c + 1 : 1;
        }
        T.EnumWindows((h, _) => { Send(h); return true; }, IntPtr.Zero);
        IntPtr after = IntPtr.Zero;
        for (int i = 0; i < 4096; i++)
        {
            after = T.FindWindowEx(T.HWND_MESSAGE, after, null, null);
            if (after == IntPtr.Zero) break;
            Send(after);
        }
        return string.Join(", ", targets.Select(t => $"{t.Value}(창 {(sent.TryGetValue(t.Key, out int c) ? c : 0)})"));
    }

    // ───────────────────────── 트레이 데이터 해석 ─────────────────────────

    private sealed class Entry
    {
        public required string Key;
        public required long Seq;
        public IntPtr Hwnd;
        public uint Uid;
        public Guid Guid;
        public uint CallbackMessage;
        public uint Version;
        public BitmapSource? Icon;
        public string Tip = "";
        public bool Hidden;
        public uint Pid;
        public string ProcessName = "";
        public string ProcessPath = "";
        public bool Own;
        public RECT? Placement;
        public TrayIconInfo? Snapshot; // null 이면 다시 만들어야 함
    }

    private static int ReadInt(IntPtr p, int len, int offset, int fallback = 0) =>
        offset + 4 <= len ? Marshal.ReadInt32(p, offset) : fallback;

    private static string ReadString(IntPtr p, int len, int offset, int maxChars)
    {
        int avail = Math.Min(maxChars, Math.Max(0, (len - offset) / 2));
        if (avail <= 0) return "";
        string s = Marshal.PtrToStringUni(p + offset, avail);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private static Guid ReadGuid(IntPtr p, int len, int offset)
    {
        if (offset + 16 > len) return Guid.Empty;
        var b = new byte[16];
        Marshal.Copy(p + offset, b, 0, 16);
        return new Guid(b);
    }

    /// <summary>SHELLTRAYDATA 처리. 반환: 우리 쪽에서 받아들였는지 (explorer 결과와 OR).</summary>
    private bool HandleTrayData(IntPtr data, int len)
    {
        const int N = T.NidOffset;
        // 최소: 시그니처 + 메시지 + cbSize·hWnd·uID·uFlags·uCallbackMessage·hIcon
        if (data == IntPtr.Zero || len < N + T.Nid_szTip) return false;
        if (Marshal.ReadInt32(data, 0) != T.ShellTraySignature) return false;
        uint nim = (uint)Marshal.ReadInt32(data, 4);
        IntPtr nid = data + N;
        int nlen = len - N;

        IntPtr hwnd = (IntPtr)(long)(uint)ReadInt(nid, nlen, T.Nid_hWnd);
        uint uid = (uint)ReadInt(nid, nlen, T.Nid_uID);
        uint flags = (uint)ReadInt(nid, nlen, T.Nid_uFlags);
        Guid guid = (flags & T.NIF_GUID) != 0 ? ReadGuid(nid, nlen, T.Nid_guidItem) : Guid.Empty;

        // GUID 만으로 식별하는 경우를 빼면 hWnd 가 있어야 함
        if (hwnd == IntPtr.Zero && (guid == Guid.Empty || nim == T.NIM_ADD)) return false;

        switch (nim)
        {
            case T.NIM_ADD:
            case T.NIM_MODIFY:
                return AddOrModify(nim, nid, nlen, hwnd, uid, flags, guid);
            case T.NIM_DELETE:
            {
                Entry? removed;
                int remaining;
                lock (_gate)
                {
                    removed = Find(hwnd, uid, guid);
                    if (removed is not null) _entries.Remove(removed);
                    remaining = _entries.Count(e => !e.Own);
                }
                if (removed is null) return false;
                if (!removed.Own)
                {
                    Log.Info($"트레이 아이콘 삭제: {removed.ProcessName} (전체 {remaining})");
                    QueuePublish();
                }
                return true;
            }
            case T.NIM_SETVERSION:
            {
                uint ver = (uint)ReadInt(nid, nlen, T.Nid_uVersion);
                if (ver > 4) return false;
                bool changed = false, own = false;
                lock (_gate)
                {
                    var e = Find(hwnd, uid, guid);
                    if (e is null) return false;
                    if (e.Version != ver) { e.Version = ver; e.Snapshot = null; changed = true; }
                    own = e.Own;
                }
                if (changed && !own) QueuePublish();
                return true;
            }
            case T.NIM_SETFOCUS:
                return true;
            default:
                return false;
        }
    }

    private bool AddOrModify(uint nim, IntPtr nid, int nlen, IntPtr hwnd, uint uid, uint flags, Guid guid)
    {
        // 아이콘 변환은 잠금 밖에서 (원본 HICON 은 앱이 이 호출에서 돌아온 뒤 지울 수 있으므로 지금 복사)
        bool hasIcon = (flags & T.NIF_ICON) != 0;
        BitmapSource? icon = null;
        IntPtr hIcon = (IntPtr)(long)(uint)ReadInt(nid, nlen, T.Nid_hIcon);
        if (hasIcon && hIcon != IntPtr.Zero) icon = CopyIcon(hIcon);

        string? tip = (flags & T.NIF_TIP) != 0 ? ReadString(nid, nlen, T.Nid_szTip, T.Nid_szTipChars) : null;
        uint state = (uint)ReadInt(nid, nlen, T.Nid_dwState);
        uint mask = (uint)ReadInt(nid, nlen, T.Nid_dwStateMask);
        uint ver = (uint)ReadInt(nid, nlen, T.Nid_uVersion);
        uint cb = (uint)ReadInt(nid, nlen, T.Nid_uCallbackMessage);

        uint pid = 0;
        if (hwnd != IntPtr.Zero) T.GetWindowThreadProcessId(hwnd, out pid);
        var (procName, procPath) = pid != 0 ? ProcessInfo(pid) : ("", "");

        bool added = false, own;
        int total;
        Entry e;
        lock (_gate)
        {
            var found = Find(hwnd, uid, guid);
            if (found is null)
            {
                if (hwnd == IntPtr.Zero) return false;
                found = new Entry
                {
                    Key = guid != Guid.Empty ? "g:" + guid.ToString("N") : $"h:{hwnd.ToInt64():X}:{uid}",
                    Seq = ++_seq,
                    Hwnd = hwnd,
                    Uid = uid,
                    Pid = pid,
                    ProcessName = procName,
                    ProcessPath = procPath,
                    Own = pid == _selfPid,
                };
                _entries.Add(found);
                added = true;
            }
            e = found;
            if ((flags & T.NIF_MESSAGE) != 0) e.CallbackMessage = cb;
            if (hasIcon)
            {
                if (hIcon == IntPtr.Zero) e.Icon = null;
                else if (icon is not null) e.Icon = icon; // 변환 실패면 이전 그림 유지
            }
            if (tip is not null) e.Tip = tip;
            if ((flags & T.NIF_STATE) != 0 && (mask & T.NIS_HIDDEN) != 0) e.Hidden = (state & T.NIS_HIDDEN) != 0;
            if (guid != Guid.Empty) e.Guid = guid;
            if (hwnd != IntPtr.Zero && hwnd != e.Hwnd)
            {
                e.Hwnd = hwnd;
                e.Uid = uid;
                e.Pid = pid;
                e.ProcessName = procName;
                e.ProcessPath = procPath;
                e.Own = pid == _selfPid;
            }
            if (ver is > 0 and <= 4) e.Version = ver;
            e.Snapshot = null;
            own = e.Own;
            total = _entries.Count(x => !x.Own);
        }
        if (!own)
        {
            if (added) Log.Info($"트레이 아이콘 추가: {procName} (전체 {total})");
            QueuePublish();
        }
        // 모르는 아이콘에 대한 NIM_MODIFY 는 목록엔 넣되 실패로 (ManagedShell 과 같음 — 앱이 NIM_ADD 를 다시 시도)
        return !(added && nim == T.NIM_MODIFY);
    }

    private Entry? Find(IntPtr hwnd, uint uid, Guid guid)
    {
        foreach (var e in _entries)
        {
            if (guid != Guid.Empty ? e.Guid == guid : e.Hwnd == hwnd && e.Uid == uid) return e;
        }
        return null;
    }

    private (string Name, string Path) ProcessInfo(uint pid)
    {
        if (_procNames.TryGetValue(pid, out var info)) return info;
        string name, path = "";
        try { path = Kernel32.QueryProcess(pid).Path; }
        catch { /* 아래 이름만 */ }
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName + ".exe";
        }
        catch
        {
            name = path.Length > 0 ? System.IO.Path.GetFileName(path) : $"pid {pid}";
        }
        info = (name, path);
        if (_procNames.Count > 512) _procNames.Clear();
        _procNames[pid] = info;
        return info;
    }

    /// <summary>HICON → 독립된 픽셀 복사본 (Pbgra32, Freeze). 원본 핸들은 건드리지 않음(앱 소유).</summary>
    private static BitmapSource? CopyIcon(IntPtr hIcon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;
            BitmapSource conv = src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
            int stride = w * 4;
            var pixels = new byte[stride * h];
            conv.CopyPixels(pixels, stride, 0);
            var copy = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            copy.Freeze();
            return copy;
        }
        catch
        {
            return null; // 이미 지워진 핸들 등
        }
    }

    private IntPtr? GetIconRect(IntPtr data, int len)
    {
        if (data == IntPtr.Zero || len < T.IconIdSize) return null;
        int which = Marshal.ReadInt32(data, T.IconId_dwMessage);
        IntPtr hwnd = (IntPtr)(long)(uint)Marshal.ReadInt32(data, T.IconId_hWnd);
        uint uid = (uint)Marshal.ReadInt32(data, T.IconId_uID);
        Guid guid = ReadGuid(data, len, T.IconId_guidItem);
        RECT r;
        lock (_gate)
        {
            var e = Find(hwnd, uid, guid);
            if (e?.Placement is not RECT p) return null;
            r = p;
        }
        return which switch
        {
            1 => T.MakeLParam(r.Left, r.Top),
            2 => T.MakeLParam(r.Width, r.Height), // 두 번째 질의는 폭·높이 (shell32 가 왼쪽 위에 더함 — 실측)
            _ => null,
        };
    }

    // ───────────────────────── UI 스레드로 ─────────────────────────

    private void QueuePublish()
    {
        lock (_gate)
        {
            if (_publishQueued) return;
            _publishQueued = true;
        }
        try { _dispatcher.BeginInvoke(Publish, DispatcherPriority.Background); }
        catch (Exception e)
        {
            lock (_gate) _publishQueued = false;
            Log.Warn($"트레이 목록 갱신 예약 실패: {e.Message}");
        }
    }

    private void Publish()
    {
        List<TrayIconInfo> list;
        lock (_gate)
        {
            _publishQueued = false;
            list = new List<TrayIconInfo>(_entries.Count);
            foreach (var e in _entries.OrderBy(x => x.Seq))
            {
                if (e.Own) continue; // 몽독 자신의 트레이 아이콘은 제외 (로고 메뉴에 같은 기능)
                e.Snapshot ??= new TrayIconInfo(e.Key, e.Hwnd, e.Uid, e.Guid, e.CallbackMessage, e.Version, e.Icon,
                    e.Tip, e.Hidden, e.Pid, e.ProcessName, e.ProcessPath);
                list.Add(e.Snapshot);
            }
        }
        _icons = list;
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception e) { Log.Error("트레이 Changed 핸들러 예외", e); }
    }

    // ───────────────────────── 클릭 전달 ─────────────────────────

    public void Click(TrayIconInfo icon, TrayMouseButton button, bool doubleClick, Rect iconScreenRect)
    {
        if (!Prepare(icon, iconScreenRect, out var e)) return;
        // 앱이 메뉴·창을 앞으로 가져올 수 있게 (몽독이 마지막 입력을 받았으므로 허용 가능)
        if (e.Pid != 0) T.AllowSetForegroundWindow(e.Pid);
        var pt = MousePoint(iconScreenRect);
        switch (button)
        {
            case TrayMouseButton.Left:
                if (doubleClick)
                {
                    Send(e, T.WM_LBUTTONDBLCLK, pt);
                    Send(e, T.WM_LBUTTONUP, pt);
                }
                else
                {
                    Send(e, T.WM_LBUTTONDOWN, pt);
                    Send(e, T.WM_LBUTTONUP, pt);
                    // 문서상 버전 4 지만 탐색기는 버전 3 에도 보냄 (ManagedShell 확인)
                    if (e.Version >= 3) Send(e, T.NIN_SELECT, pt);
                }
                break;
            case TrayMouseButton.Right:
                Send(e, T.WM_RBUTTONDOWN, pt);
                Send(e, T.WM_RBUTTONUP, pt);
                if (e.Version >= 3) Send(e, T.WM_CONTEXTMENU, pt);
                break;
            case TrayMouseButton.Middle:
                Send(e, T.WM_MBUTTONDOWN, pt);
                Send(e, T.WM_MBUTTONUP, pt);
                break;
        }
    }

    public void Hover(TrayIconInfo icon, Rect iconScreenRect)
    {
        if (!Prepare(icon, iconScreenRect, out var e)) return;
        Send(e, T.WM_MOUSEMOVE, MousePoint(iconScreenRect));
    }

    private readonly record struct Target(IntPtr Hwnd, uint Uid, uint CallbackMessage, uint Version, uint Pid);

    private bool Prepare(TrayIconInfo icon, Rect rect, out Target target)
    {
        target = default;
        Entry? e;
        bool removed = false;
        lock (_gate)
        {
            e = _entries.FirstOrDefault(x => x.Key == icon.Key);
            if (e is null) return false;
            if (!T.IsWindow(e.Hwnd))
            {
                _entries.Remove(e);
                removed = true;
            }
            else
            {
                if (!rect.IsEmpty)
                    e.Placement = new RECT((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top), (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
                target = new Target(e.Hwnd, e.Uid, e.CallbackMessage, e.Version, e.Pid);
            }
        }
        if (removed)
        {
            Log.Info($"트레이 아이콘 정리: {e!.ProcessName} (창 없음)");
            QueuePublish();
            return false;
        }
        return target.CallbackMessage != 0;
    }

    private static T.POINT MousePoint(Rect rect)
    {
        if (T.GetCursorPos(out var p) && (rect.IsEmpty || rect.Contains(p.X, p.Y))) return p;
        if (rect.IsEmpty) return p;
        return new T.POINT { X = (int)(rect.Left + rect.Width / 2), Y = (int)(rect.Top + rect.Height / 2) };
    }

    /// <summary>
    /// 콜백 메시지 보내기 (비동기 — 앱이 응답 없어도 몽독이 안 멈춤).
    /// 버전 4: wParam = 좌표(MAKEWPARAM x,y), lParam = LOWORD 메시지 / HIWORD uID. 그 외: wParam = uID, lParam = 메시지.
    /// </summary>
    private static void Send(Target t, uint mouseMsg, T.POINT pt)
    {
        IntPtr wParam, lParam;
        if (t.Version >= 4)
        {
            wParam = T.MakeLParam(pt.X, pt.Y);
            lParam = T.MakeLParam((int)mouseMsg, (int)t.Uid);
        }
        else
        {
            wParam = (IntPtr)(long)t.Uid;
            lParam = (IntPtr)(long)mouseMsg;
        }
        T.SendNotifyMessage(t.Hwnd, t.CallbackMessage, wParam, lParam);
    }
}
