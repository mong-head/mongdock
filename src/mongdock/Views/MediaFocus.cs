using System.Runtime.InteropServices;
using System.Windows;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 영상을 보고 있는지 (사용자 결정 2026-10-21, 범위 B 10-22): 미디어 세션이 「재생」인 앱의 창이 지금 앞에 있고, 그 창이 최대화됐거나
/// 모니터 작업 영역을 거의 다(90% 이상) 덮으면 true. 그동안은 전체 화면이 아니어도(브라우저 창 최대화로 넷플릭스 등) 알림 배너와 독 아이콘 튀기기를 하지 않음
/// — 알림은 알림 센터에만 쌓이고, 윈도우 기본 토스트는 그대로 숨김(NotificationService.LastBannerTick 은 배너 창과 상관없이 기록됨).
/// 작은 창이거나 다른 앱을 보면(같은 브라우저의 다른 작은 창 포함) 배너는 평소대로. 재생을 멈추거나 다른 창으로 가면 다시 보임.
/// </summary>
internal static class MediaFocus
{
    /// <summary>작업 영역을 이만큼 이상 덮으면 「화면을 거의 다 덮음」.</summary>
    internal const double CoverRatio = 0.9;

    public static bool Watching(AppServices services)
    {
        try
        {
            var playing = services.Media.PlayingApps;
            if (playing.Count == 0) return false;
            var hwnd = services.Windows.ForegroundWindow;
            var fg = hwnd == IntPtr.Zero ? null : services.Windows.Windows.FirstOrDefault(w => w.Hwnd == hwnd);
            // 한계(리뷰): 미디어 세션엔 탭·창 구분이 없어, 같은 브라우저의 다른 탭이 재생 중이어도 최대화한 그 브라우저 창이면 생략됨
            return Plays(fg, playing) && CoversScreen(fg!.Hwnd);
        }
        catch { return false; }
    }

    /// <summary>앞 창이 재생 중인 앱 중 하나의 창인지 (독의 재생 표시와 같은 맞춤: AUMID 또는 실행 파일 이름).</summary>
    internal static bool Plays(AppWindowInfo? foreground, IReadOnlyList<string> playing)
    {
        if (foreground is null || playing.Count == 0) return false;
        var one = new[] { foreground };
        return playing.Any(source => DockWindow.MediaSourceMatches(source, null, one));
    }

    /// <summary>최대화됐거나, 보이는 창 사각형이 작업 영역을 <see cref="CoverRatio"/> 이상 덮는지 (물리 px).</summary>
    internal static bool Covers(bool zoomed, Rect window, Rect work)
    {
        if (zoomed) return true;
        if (work.Width <= 0 || work.Height <= 0) return false;
        var overlap = Rect.Intersect(window, work);
        return !overlap.IsEmpty && overlap.Width * overlap.Height >= CoverRatio * work.Width * work.Height;
    }

    private static bool CoversScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        bool zoomed = IsZoomed(hwnd);
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0) return zoomed;
        IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return zoomed;
        var w = mi.rcWork;
        return Covers(zoomed, new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top));
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
}
