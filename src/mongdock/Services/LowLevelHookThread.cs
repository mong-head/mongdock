using System.Runtime.InteropServices;
using K = Mongdock.Native.KeyboardHookApi;

namespace Mongdock.Services;

/// <summary>
/// 저수준 훅(WH_KEYBOARD_LL / WH_MOUSE_LL) 하나를 전용 백그라운드 스레드에서 돌린다.
/// LL 훅 콜백은 설치한 스레드의 메시지 루프로 호출되므로 UI 스레드에 걸면 UI 가 바쁠 때 시스템 전체 입력이 늦어지고
/// LowLevelHooksTimeout 을 넘기면 훅이 조용히 제거된다 → 메시지만 펌프하는 전용 스레드에 둔다.
///
/// 콜백(<c>handler</c>)은 훅 스레드에서 호출된다: 판단만 하고 빨리 반환할 것 (UI 작업은 Dispatcher.BeginInvoke).
/// handler 가 true 를 반환하면 그 입력을 삼킨다(1 반환), 아니면 CallNextHookEx.
/// Start/Stop/Restart 는 아무 스레드에서나 호출 가능 (내부 잠금).
/// </summary>
internal sealed class LowLevelHookThread : IDisposable
{
    private const uint PM_NOREMOVE = 0x0000;

    private readonly int _hookId;
    private readonly string _name;
    private readonly Func<int, IntPtr, IntPtr, bool> _handler;
    private readonly K.LowLevelHookProc _proc; // GC 방지용으로 필드 보관
    private readonly object _gate = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _disposed;

    public LowLevelHookThread(int hookId, string name, Func<int, IntPtr, IntPtr, bool> handler)
    {
        _hookId = hookId;
        _name = name;
        _handler = handler;
        _proc = HookProc;
    }

    /// <summary>훅 스레드가 돌고 있고 훅이 설치돼 있는지.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _thread is not null && _hook != IntPtr.Zero; }
    }

    /// <summary>훅 스레드 시작 (이미 돌고 있으면 아무것도 안 함). 설치 성공 여부 반환.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_thread is not null) return _hook != IntPtr.Zero;

            var ready = new ManualResetEventSlim(false); // Dispose 안 함 — 시간 초과 뒤 스레드가 Set 할 수 있음
            var t = new Thread(() => Run(ready))
            {
                IsBackground = true,
                Name = $"mongdock {_name} hook",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread = t;
            t.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(3)))
                Log.Warn($"{_name} 훅 스레드 시작 대기 시간 초과");
            if (_hook == IntPtr.Zero)
            {
                // 설치 실패 → 스레드는 스스로 끝난다
                t.Join(1000);
                _thread = null;
                _threadId = 0;
                return false;
            }
            return true;
        }
    }

    /// <summary>훅 해제 + 스레드 종료 (WM_QUIT).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            var t = _thread;
            if (t is null) return;
            uint id = _threadId;
            if (id != 0 && !K.PostThreadMessage(id, K.WM_QUIT, IntPtr.Zero, IntPtr.Zero))
                Log.Warn($"{_name} 훅 스레드 종료 요청 실패 err={Marshal.GetLastWin32Error()}");
            if (Thread.CurrentThread != t && !t.Join(2000))
                Log.Warn($"{_name} 훅 스레드가 제때 끝나지 않음");
            _thread = null;
            _threadId = 0;
        }
    }

    /// <summary>훅 재설치 (조용히 빠졌을 가능성 대비 — 세션 잠금 해제·디스플레이 변경 때).</summary>
    public void Restart(string reason)
    {
        lock (_gate)
        {
            if (_disposed || _thread is null) return;
            Log.Info($"{_name} 훅 재설치 ({reason})");
            Stop();
            Start();
        }
    }

    private void Run(ManualResetEventSlim ready)
    {
        try
        {
            _threadId = Native.Kernel32.GetCurrentThreadId();
            // 메시지 큐를 먼저 만들어 둔다 (PostThreadMessage 가 실패하지 않도록)
            K.PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

            IntPtr hMod = K.GetModuleHandle(null);
            IntPtr h = K.SetWindowsHookEx(_hookId, _proc, hMod, 0);
            if (h == IntPtr.Zero)
            {
                hMod = K.GetModuleHandle("user32.dll");
                h = K.SetWindowsHookEx(_hookId, _proc, hMod, 0);
            }
            if (h == IntPtr.Zero)
            {
                Log.Error($"{_name} 훅 설치 실패 err={Marshal.GetLastWin32Error()}");
                ready.Set();
                return;
            }
            _hook = h;
            Log.Info($"{_name} 훅 설치 (전용 스레드 {_threadId})");
            ready.Set();

            while (true)
            {
                int r = K.GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (r == 0) break; // WM_QUIT
                if (r == -1)
                {
                    Log.Warn($"{_name} 훅 스레드 GetMessage 실패 err={Marshal.GetLastWin32Error()}");
                    break;
                }
                // 이 스레드엔 창이 없으므로 디스패치할 것 없음 (LL 훅 콜백은 GetMessage 안에서 호출됨)
            }

            if (!K.UnhookWindowsHookEx(h))
                Log.Warn($"{_name} 훅 해제 실패 err={Marshal.GetLastWin32Error()}");
            _hook = IntPtr.Zero;
            Log.Info($"{_name} 훅 해제");
        }
        catch (Exception e)
        {
            _hook = IntPtr.Zero;
            Log.Error($"{_name} 훅 스레드 예외", e);
            ready.Set();
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                if (_handler(nCode, wParam, lParam)) return new IntPtr(1);
            }
            catch
            {
                // 훅 안에서 예외를 밖으로 내보내지 않음 (로그도 생략 — 빠르게 반환)
            }
        }
        return K.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }
}
