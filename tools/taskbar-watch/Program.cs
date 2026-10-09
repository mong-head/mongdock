using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarWatch;

/// <summary>
/// taskbar-watch [분=30] [출력 파일]
/// 작업 표시줄 창이 보이게 된 때(EVENT_OBJECT_SHOW)와 다시 숨은 때(EVENT_OBJECT_HIDE)를 ms 단위로 적고,
/// 보일 때의 상황(포그라운드 창·프로세스, 마우스 위치, 직전 포그라운드 변화)을 같이 남긴다. 끝나면 횟수·보인 시간 요약.
/// </summary>
internal static class Program
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<IntPtr, double> ShownAt = new();
    private static readonly List<double> Durations = new();
    private static int _shows;
    private static string _lastFg = "";
    private static double _lastFgAt = -1;
    private static StreamWriter _out = null!;
    private static WinEventProc? _proc;

    private static int Main(string[] args)
    {
        double minutes = args.Length > 0 && double.TryParse(args[0], out var m) ? m : 30;
        string path = args.Length > 1 ? args[1] : "taskbar-watch.txt";
        _out = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        W($"시작 {DateTime.Now:HH:mm:ss}, {minutes}분 관찰");

        _proc = OnEvent;
        var hooks = new[]
        {
            SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT),
            SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT),
        };
        uint tid = GetCurrentThreadId();
        var timer = new Timer(_ => PostThreadMessage(tid, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero), null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        foreach (var h in hooks) UnhookWinEvent(h);
        timer.Dispose();

        double total = Durations.Sum();
        W($"끝 {DateTime.Now:HH:mm:ss}: 보임 {_shows}번, 다시 숨음 {Durations.Count}번, " +
          (Durations.Count > 0 ? $"보인 시간 평균 {Durations.Average():0} ms, 최대 {Durations.Max():0} ms, 합계 {total / 1000:0.0} 초" : "보인 시간 없음"));
        _out.Dispose();
        return 0;
    }

    private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        double now = Clock.Elapsed.TotalMilliseconds;
        if (ev == EVENT_SYSTEM_FOREGROUND)
        {
            _lastFg = Describe(hwnd);
            _lastFgAt = now;
            return;
        }
        string cls = ClassOf(hwnd);
        if (cls is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return;

        if (ev == EVENT_OBJECT_SHOW)
        {
            _shows++;
            ShownAt[hwnd] = now;
            GetCursorPos(out var c);
            GetWindowRect(hwnd, out var r);
            W($"{DateTime.Now:HH:mm:ss.fff} 보임 {cls} 0x{hwnd.ToInt64():X} rect=({r.L},{r.T})-({r.R},{r.B}) 마우스=({c.X},{c.Y}) " +
              $"포그라운드={Describe(GetForegroundWindow())} 직전 포그라운드 변화={(_lastFgAt < 0 ? "-" : $"{now - _lastFgAt:0} ms 전 → {_lastFg}")}");
        }
        else if (ShownAt.Remove(hwnd, out double at))
        {
            double d = now - at;
            Durations.Add(d);
            W($"{DateTime.Now:HH:mm:ss.fff} 숨음 {cls} 0x{hwnd.ToInt64():X} — {d:0} ms 보였음");
        }
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>클래스 + 프로세스 이름 (창 제목은 남기지 않음 — 개인정보).</summary>
    private static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(없음)";
        string proc = "?";
        try
        {
            GetWindowThreadProcessId(h, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            proc = p.ProcessName;
        }
        catch { }
        return $"{proc}/{ClassOf(h)}";
    }

    private static void W(string line)
    {
        Console.WriteLine(line);
        _out.WriteLine(line);
    }
}
