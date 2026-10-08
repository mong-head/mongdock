using System.Globalization;
using System.Runtime.InteropServices;

namespace TouchTest;

/// <summary>
/// 터치 장치 없이 손가락 입력을 흉내 낸다 (InitializeTouchInjection + InjectTouchInput).
/// 진짜 터치처럼 윈도우가 처리하므로 WPF 의 터치→마우스 승격, "누르고 있기 = 오른쪽 클릭" 제스처까지 그대로 시험된다.
/// 화면 전체에 입력이 들어가므로 몽독 상단바·독 위 좌표에만 쓸 것. 사용법: README.md 또는 인자 없이 실행.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var list = args.ToList();
        bool dryRun = TakeFlag(list, "--dry-run");
        bool dip = TakeFlag(list, "--dip");
        double wait = TakeValue(list, "--wait", 3);
        int repeat = (int)TakeValue(list, "--repeat", 1);
        if (list.Count == 0 || list[0] is "help" or "-h" or "--help" or "/?")
        {
            PrintUsage();
            return list.Count == 0 ? 1 : 0;
        }

        var screen = Screen.Primary();
        string cmd = list[0].ToLowerInvariant();
        try
        {
            if (cmd == "info")
            {
                PrintInfo(screen);
                return 0;
            }

            double scale = dip ? screen.Scale : 1.0;
            int X(string s) => Coord(s, screen.Width, scale) + screen.Left;
            int Y(string s) => Coord(s, screen.Height, scale) + screen.Top;
            int Arg(int i) => i < list.Count ? (int)double.Parse(list[i], CultureInfo.InvariantCulture) : -1;

            Action action;
            string what;
            switch (cmd)
            {
                case "tap" when list.Count >= 3:
                {
                    int x = X(list[1]), y = Y(list[2]);
                    what = $"탭 ({x},{y})";
                    action = () => Touch.Hold(x, y, 60);
                    break;
                }
                case "hold" when list.Count >= 3:
                {
                    int x = X(list[1]), y = Y(list[2]);
                    int ms = Arg(3) > 0 ? Arg(3) : 800;
                    what = $"길게 누르기 ({x},{y}) {ms}ms";
                    action = () => Touch.Hold(x, y, ms);
                    break;
                }
                case "drag" when list.Count >= 5:
                {
                    int x1 = X(list[1]), y1 = Y(list[2]), x2 = X(list[3]), y2 = Y(list[4]);
                    int ms = Arg(5) > 0 ? Arg(5) : 600;
                    int holdMs = Arg(6) >= 0 ? Arg(6) : 0;
                    what = $"끌기 ({x1},{y1}) → ({x2},{y2}) {ms}ms" + (holdMs > 0 ? $", 먼저 {holdMs}ms 누름" : "");
                    action = () => Touch.Drag(x1, y1, x2, y2, ms, holdMs);
                    break;
                }
                default:
                    Console.Error.WriteLine($"알 수 없는 명령 또는 인자 부족: {string.Join(' ', list)}");
                    PrintUsage();
                    return 1;
            }

            Console.WriteLine($"{what}{(repeat > 1 ? $" ×{repeat}" : "")}  (주 모니터 {screen})");
            if (dryRun)
            {
                Console.WriteLine("--dry-run: 보내지 않음");
                return 0;
            }
            if (wait > 0)
            {
                Console.WriteLine($"{wait:0.#}초 뒤 보냄 (취소: Ctrl+C)");
                Thread.Sleep(TimeSpan.FromSeconds(wait));
            }
            Touch.Initialize();
            for (int i = 0; i < repeat; i++)
            {
                action();
                if (i < repeat - 1) Thread.Sleep(400);
            }
            Console.WriteLine("완료");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"실패: {ex.Message}");
            return 2;
        }
    }

    /// <summary>"120" = 그 값, "r-40" = 오른쪽 끝에서 40, "b-40" = 아래 끝에서 40, "c" / "c+30" / "c-30" = 가운데 기준. --dip 이면 배율을 곱함.</summary>
    private static int Coord(string s, int extentPx, double scale)
    {
        s = s.Trim().ToLowerInvariant();
        double Num(string t) => t.Length == 0 ? 0 : double.Parse(t, CultureInfo.InvariantCulture);
        if (s.StartsWith('r') || s.StartsWith('b'))
            return (int)Math.Round(extentPx - Num(s[1..].TrimStart('-')) * scale) - 1;
        if (s.StartsWith('c'))
            return (int)Math.Round(extentPx / 2.0 + Num(s[1..].Replace("+", "")) * scale);
        return (int)Math.Round(Num(s) * scale);
    }

    private static bool TakeFlag(List<string> list, string name)
    {
        int i = list.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        list.RemoveAt(i);
        return true;
    }

    private static double TakeValue(List<string> list, string name, double fallback)
    {
        int i = list.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= list.Count) return fallback;
        double v = double.Parse(list[i + 1], CultureInfo.InvariantCulture);
        list.RemoveRange(i, 2);
        return v;
    }

    private static void PrintInfo(Screen s)
    {
        Console.WriteLine($"주 모니터: {s}");
        Console.WriteLine($"터치 장치 수(SM_MAXIMUMTOUCHES): {Native.GetSystemMetrics(Native.SM_MAXIMUMTOUCHES)} (0 이면 실제 터치 없음 — 주입은 그래도 됨)");
        double k = s.Scale;
        Console.WriteLine("몽독 기본 배치 기준 예시 좌표 (물리 px, 상단바 높이 26·독 아이콘 52 기준):");
        Console.WriteLine($"  상단바 세로 가운데      y = {Math.Round(13 * k)}");
        Console.WriteLine($"  상단바 시계 근처        x = {s.Width - (int)Math.Round(60 * k)}");
        Console.WriteLine($"  독 아이콘 줄 (아래 끝)  y ≈ {s.Height - (int)Math.Round(40 * k)}");
        Console.WriteLine($"  화면 가운데             x = {s.Width / 2}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            touch-test — 몽독 터치 시험용 (InjectTouchInput). 실제 화면에 손가락 입력이 들어갑니다.

            사용법:
              touch-test info                         주 모니터 크기·배율과 상단바·독 예시 좌표
              touch-test tap  X Y                     탭
              touch-test hold X Y [ms=800]            길게 누르기 (윈도우 "누르고 있기" → 손 떼면 오른쪽 클릭)
              touch-test drag X1 Y1 X2 Y2 [ms=600] [먼저누름ms=0]
                                                      끌기 (먼저누름ms 를 주면 그만큼 누른 뒤 움직임 = 길게 눌러 순서 바꾸기)

            좌표: 물리 픽셀 (주 모니터 왼쪽 위 기준). "r-40" = 오른쪽 끝에서 40, "b-40" = 아래 끝에서 40, "c+30" = 가운데에서 +30.
            옵션: --dip (좌표를 DIP 로 보고 배율을 곱함)  --wait 초(기본 3)  --repeat N  --dry-run (보내지 않고 좌표만 출력)

            예:
              touch-test tap r-60 13 --dip            상단바 오른쪽 시계 근처 탭
              touch-test hold c b-40 --dip 900        독 가운데 아이콘 길게 누르기
              touch-test drag r-200 13 r-300 13 500 500 --dip   상단바 아이콘 0.5초 누른 뒤 왼쪽으로 끌기
            """);
    }
}

/// <summary>주 모니터 (물리 px).</summary>
internal readonly record struct Screen(int Left, int Top, int Width, int Height, double Scale)
{
    public static Screen Primary()
    {
        IntPtr mon = Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (mon == IntPtr.Zero || !Native.GetMonitorInfo(mon, ref mi))
            return new Screen(0, 0, Native.GetSystemMetrics(0), Native.GetSystemMetrics(1), 1);
        double scale = Native.GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        var r = mi.rcMonitor;
        return new Screen(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, scale);
    }

    public override string ToString() => $"({Left},{Top}) {Width}×{Height} px, 배율 {Scale * 100:0}%";
}

/// <summary>손가락 하나 주입.</summary>
internal static class Touch
{
    private const int FrameMs = 16;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        if (!Native.InitializeTouchInjection(1, Native.TOUCH_FEEDBACK_DEFAULT))
            throw new InvalidOperationException($"InitializeTouchInjection 실패 (오류 {Marshal.GetLastWin32Error()})");
        _initialized = true;
    }

    /// <summary>누르고 ms 동안 그대로 있다가 뗌 (탭 = 짧게). 누르는 동안 프레임마다 UPDATE — 없으면 윈도우가 접촉을 끊음.</summary>
    public static void Hold(int x, int y, int ms)
    {
        Send(x, y, Native.POINTER_FLAG_DOWN | Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT);
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Thread.Sleep(FrameMs);
            Send(x, y, Native.POINTER_FLAG_UPDATE | Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT);
        }
        Send(x, y, Native.POINTER_FLAG_UP);
    }

    public static void Drag(int x1, int y1, int x2, int y2, int ms, int holdMs)
    {
        Send(x1, y1, Native.POINTER_FLAG_DOWN | Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT);
        var holdUntil = Environment.TickCount64 + holdMs;
        while (Environment.TickCount64 < holdUntil)
        {
            Thread.Sleep(FrameMs);
            Send(x1, y1, Native.POINTER_FLAG_UPDATE | Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT);
        }
        int steps = Math.Max(2, ms / FrameMs);
        int x = x1, y = y1;
        for (int i = 1; i <= steps; i++)
        {
            Thread.Sleep(FrameMs);
            x = x1 + (x2 - x1) * i / steps;
            y = y1 + (y2 - y1) * i / steps;
            Send(x, y, Native.POINTER_FLAG_UPDATE | Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT);
        }
        Thread.Sleep(FrameMs);
        Send(x, y, Native.POINTER_FLAG_UP);
    }

    private static void Send(int x, int y, uint flags)
    {
        var info = new Native.POINTER_TOUCH_INFO
        {
            pointerInfo = new Native.POINTER_INFO
            {
                pointerType = Native.PT_TOUCH,
                pointerId = 0,
                pointerFlags = flags,
                ptPixelLocation = new Native.POINT { X = x, Y = y },
            },
            touchFlags = 0,
            touchMask = Native.TOUCH_MASK_CONTACTAREA | Native.TOUCH_MASK_ORIENTATION | Native.TOUCH_MASK_PRESSURE,
            rcContact = new Native.RECT { Left = x - 4, Top = y - 4, Right = x + 4, Bottom = y + 4 },
            orientation = 90,
            pressure = 32000,
        };
        if (!Native.InjectTouchInput(1, new[] { info }))
            throw new InvalidOperationException($"InjectTouchInput 실패 (오류 {Marshal.GetLastWin32Error()}, flags 0x{flags:X})");
    }
}

internal static class Native
{
    public const int SM_MAXIMUMTOUCHES = 95;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint TOUCH_FEEDBACK_DEFAULT = 0x1;
    public const int PT_TOUCH = 2;
    public const uint POINTER_FLAG_INRANGE = 0x00000002;
    public const uint POINTER_FLAG_INCONTACT = 0x00000004;
    public const uint POINTER_FLAG_DOWN = 0x00010000;
    public const uint POINTER_FLAG_UPDATE = 0x00020000;
    public const uint POINTER_FLAG_UP = 0x00040000;
    public const uint TOUCH_MASK_CONTACTAREA = 0x1;
    public const uint TOUCH_MASK_ORIENTATION = 0x2;
    public const uint TOUCH_MASK_PRESSURE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public int pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
