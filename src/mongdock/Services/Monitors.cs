using System.Windows;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 연결된 모니터 하나의 스냅샷 (조회 시점 값 — 디스플레이 구성이 바뀌면 다시 조회할 것).
///
/// 좌표 규칙: 앱은 PerMonitorV2 이므로 WPF 창의 Left/Top 은 "물리 픽셀 / 그 창이 있는 모니터의 배율" 이다.
/// 그래서 이 모니터 위에 놓일 창의 DIP 좌표 = 물리 픽셀 / <see cref="Scale"/> (원점 보정 없음).
/// <see cref="Bounds"/>·<see cref="WorkArea"/> 가 이 규칙의 DIP 값이라 이 모니터 위 창의 Left/Top 과 바로 비교할 수 있다.
/// 배율이 다른 모니터끼리는 DIP 영역이 겹칠 수 있으므로 DIP 좌표는 항상 "어느 모니터 기준인지" 와 함께 다룬다.
/// </summary>
public sealed class MonitorInfo
{
    internal MonitorInfo(string deviceName, RECT bounds, RECT work, double scale, bool isPrimary, int number)
    {
        DeviceName = deviceName;
        BoundsRect = bounds;
        WorkRect = work;
        Scale = scale <= 0 ? 1 : scale;
        IsPrimary = isPrimary;
        Number = number;
    }

    /// <summary>MONITORINFOEX.szDevice (예 "\\.\DISPLAY2"). 설정 Dock.Monitor 에 저장하는 값.</summary>
    public string DeviceName { get; }
    /// <summary>배율 (1.0 = 96 DPI, 1.25 = 125%).</summary>
    public double Scale { get; }
    public bool IsPrimary { get; }
    /// <summary>표시 번호 (장치 이름 끝 숫자, 예 DISPLAY2 → 2). 윈도우 설정의 번호와 대개 같지만 다를 수 있음.</summary>
    public int Number { get; }

    internal RECT BoundsRect { get; }
    internal RECT WorkRect { get; }

    /// <summary>모니터 전체 영역 (이 모니터 기준 DIP = px / Scale).</summary>
    public Rect Bounds => ToDip(BoundsRect);
    /// <summary>작업 영역 (이 모니터 기준 DIP).</summary>
    public Rect WorkArea => ToDip(WorkRect);
    /// <summary>표시용 이름 (예 "디스플레이 2 (주 모니터) — 2560×1440, 125%").</summary>
    public string DisplayName =>
        Loc.F($"디스플레이 {Number}{(IsPrimary ? Loc.T(" (주 모니터)") : "")} — {BoundsRect.Width}×{BoundsRect.Height}, {Math.Round(Scale * 100)}%");

    public bool ContainsPx(Point px) =>
        px.X >= BoundsRect.Left && px.X < BoundsRect.Right && px.Y >= BoundsRect.Top && px.Y < BoundsRect.Bottom;

    public Point ToDip(Point px) => new(px.X / Scale, px.Y / Scale);
    public Point ToPx(Point dip) => new(dip.X * Scale, dip.Y * Scale);

    internal Rect ToDip(RECT r) => new(r.Left / Scale, r.Top / Scale, r.Width / Scale, r.Height / Scale);

    internal RECT ToPx(Rect dip) =>
        new((int)Math.Floor(dip.Left * Scale), (int)Math.Floor(dip.Top * Scale),
            (int)Math.Ceiling(dip.Right * Scale), (int)Math.Ceiling(dip.Bottom * Scale));

    internal bool SameBounds(RECT r) =>
        r.Left == BoundsRect.Left && r.Top == BoundsRect.Top && r.Right == BoundsRect.Right && r.Bottom == BoundsRect.Bottom;

    public override string ToString() => $"{DeviceName} {BoundsRect} work={WorkRect} x{Scale:0.##}{(IsPrimary ? " primary" : "")}";
}

/// <summary>모니터 목록 도우미 (어느 스레드에서나 호출 가능, 매번 새로 조회 — 가벼운 Win32 호출뿐).</summary>
public static class Monitors
{
    /// <summary>연결된 모든 모니터 (주 모니터 먼저, 그다음 표시 번호 순). 조회 실패 시 주 모니터 하나라도 반환.</summary>
    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        try
        {
            foreach (IntPtr h in DesktopApi.EnumMonitorHandles())
                if (Create(h, list.Count) is { } m) list.Add(m);
        }
        catch (Exception e)
        {
            Log.Warn($"모니터 목록 조회 실패: {e.Message}");
        }
        if (list.Count == 0) list.Add(Fallback());
        list.Sort((a, b) => a.IsPrimary != b.IsPrimary ? (a.IsPrimary ? -1 : 1) : a.Number.CompareTo(b.Number));
        return list;
    }

    public static MonitorInfo GetPrimary() => Create(DesktopApi.PrimaryMonitor, 0) ?? Fallback();

    /// <summary>장치 이름으로 찾음 (대소문자 무시). 없거나 빈 이름이면 null.</summary>
    public static MonitorInfo? Find(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;
        return GetAll().FirstOrDefault(m => string.Equals(m.DeviceName, deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>장치 이름의 모니터. "" 이거나 연결돼 있지 않으면 주 모니터.</summary>
    public static MonitorInfo Resolve(string? deviceName) => Find(deviceName) ?? GetPrimary();

    internal static MonitorInfo FromHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return GetPrimary();
        return Create(DesktopApi.MonitorFromWindow(hwnd, DesktopApi.MONITOR_DEFAULTTONEAREST), 0) ?? GetPrimary();
    }

    /// <summary>커서가 있는 모니터 (없으면 주 모니터). MONGDOCK_FAKE_SCREEN 이 반영된 값 — 화면 크기 계산은 DesktopApi 직접 호출 대신 이것을.</summary>
    public static MonitorInfo FromCursor()
    {
        if (!DesktopApi.GetCursorPos(out POINT pt)) return GetPrimary();
        return Create(DesktopApi.MonitorFromPoint(pt, DesktopApi.MONITOR_DEFAULTTONEAREST), 0) ?? GetPrimary();
    }

    /// <summary>구성 비교용 서명: 장치·영역·DPI·주 모니터 (작업 영역 제외 — AppBar 변경으로 바뀌므로).</summary>
    internal static string Signature(IReadOnlyList<MonitorInfo> all) =>
        string.Join(";", all.Select(m => $"{m.DeviceName}|{m.BoundsRect}|{Math.Round(m.Scale * 96)}|{(m.IsPrimary ? 1 : 0)}"));

    internal static MonitorInfo? Create(IntPtr h, int index)
    {
        if (!DesktopApi.TryGetMonitorInfoEx(h, out MONITORINFOEX mi)) return null;
        string device = mi.szDevice ?? "";
        bool primary = (mi.dwFlags & DesktopApi.MONITORINFOF_PRIMARY) != 0;
        double scale = DesktopApi.GetMonitorScale(h);
        RECT bounds = mi.rcMonitor, work = mi.rcWork;
        if (primary) FakeScreen.Apply(ref bounds, ref work, scale); // MONGDOCK_FAKE_SCREEN (없으면 그대로)
        return new MonitorInfo(device, bounds, work, scale, primary, ParseNumber(device, index + 1));
    }

    private static int ParseNumber(string device, int fallback)
    {
        int end = device.Length;
        int start = end;
        while (start > 0 && char.IsAsciiDigit(device[start - 1])) start--;
        return start < end && int.TryParse(device.AsSpan(start, end - start), out int n) && n > 0 ? n : fallback;
    }

    private static MonitorInfo Fallback()
    {
        var r = new RECT(0, 0, User32.GetSystemMetrics(User32.SM_CXSCREEN), User32.GetSystemMetrics(User32.SM_CYSCREEN));
        return new MonitorInfo("", r, r, 1.0, true, 1);
    }
}
