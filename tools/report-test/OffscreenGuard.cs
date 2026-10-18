using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TestShared;

/// <summary>
/// 화면 밖 지킴이 (2026-10-18 사용자: PC 를 쓰는 중에 「앱 모음」이 혼자 열렸다 닫힘 — 시험 프로세스가 화면 안에 창을 띄운 것으로 보임).
/// 시험 프로세스의 최상위 창이 실제 모니터 안에 보이거나(EVENT_OBJECT_SHOW) 들어오면(LOCATIONCHANGE) 바로 x 를 -40000 쪽으로 옮기고
/// C:\dev\mongdock-tmp\onscreen-guard.log 에 누가(제목·클래스·자리·명령줄) 그랬는지 남김. 또 이 스레드의 창은 활성화(포커스)를 못 가져가게 막음(WH_CBT).
/// 화면을 일부러 쓰는 측정(--flashmatrix·--minimize 등)에서는 켜지 않음.
/// </summary>
internal static class OffscreenGuard
{
    private const string LogPath = @"C:\dev\mongdock-tmp\onscreen-guard.log";
    private static WinEventProc? _proc;
    private static HookProc? _cbt;
    private static IntPtr _showHook, _moveHook, _cbtHook;
    private static int _moved;
    [ThreadStatic] private static bool _inside;

    public static int Moved => _moved;

    /// <summary>UI 스레드에서 한 번 (창을 만들기 전에).</summary>
    public static void Install()
    {
        if (_proc is not null) return;
        _proc = OnEvent;
        uint pid = (uint)Environment.ProcessId;
        _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, pid, 0, WINEVENT_OUTOFCONTEXT);
        _moveHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, pid, 0, WINEVENT_OUTOFCONTEXT);
        _cbt = OnCbt;
        _cbtHook = SetWindowsHookEx(WH_CBT, _cbt, IntPtr.Zero, GetCurrentThreadId());
    }

    private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero || _inside) return;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd || !IsWindowVisible(hwnd)) return;
        if (!GetWindowRect(hwnd, out RECT r) || r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return;
        if (!OnAnyMonitor(r)) return;
        _inside = true;
        try
        {
            SetWindowPos(hwnd, IntPtr.Zero, r.Left - 40000, r.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Interlocked.Increment(ref _moved);
            var cls = new StringBuilder(128); GetClassName(hwnd, cls, cls.Capacity);
            var title = new StringBuilder(256); GetWindowText(hwnd, title, title.Capacity);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {(ev == EVENT_OBJECT_SHOW ? "보임" : "옮김")} 화면 안 창 → 밖으로: 제목 '{title}' 클래스 {cls} 자리 ({r.Left},{r.Top})-({r.Right},{r.Bottom}) | {Process.GetCurrentProcess().ProcessName} {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}";
            Console.WriteLine("  지킴이: " + line[24..]);
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
        finally { _inside = false; }
    }

    // 이 스레드의 창은 활성화(포커스 가져가기)를 못 함 — 사용자가 쓰던 창에서 포커스를 뺏지 않게
    private static IntPtr OnCbt(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HCBT_ACTIVATE) return (IntPtr)1;
        return CallNextHookEx(_cbtHook, code, wParam, lParam);
    }

    private static bool OnAnyMonitor(RECT r)
    {
        bool hit = false;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT mr, IntPtr d) =>
        {
            if (r.Left < mr.Right && r.Right > mr.Left && r.Top < mr.Bottom && r.Bottom > mr.Top) hit = true;
            return true;
        }, IntPtr.Zero);
        return hit;
    }

    private const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0;
    private const uint GA_ROOT = 2, SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const int WH_CBT = 5, HCBT_ACTIVATE = 5;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr m, IntPtr dc, ref RECT r, IntPtr d);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hmod, uint tid);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
}
