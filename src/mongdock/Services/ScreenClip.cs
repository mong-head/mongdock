using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Mongdock.Services;

/// <summary>영역 캡처 결과 — 그림(있으면)과 끝난 까닭 (로그·시험용).</summary>
public enum ScreenClipOutcome { Captured, Cancelled, NoTool, LaunchFailed, TimedOut, Stopped }

/// <summary>
/// 문제 신고 영역 캡처 (#13, 0.6.3): 윈도우 캡처 도구(ms-screenclip:)를 띄우고 클립보드에 새 그림이 들어오면 그 그림을 돌려줌.
/// 클립보드는 읽기만 함(지우거나 바꾸지 않음). 캡처 도구 화면(모니터 전체를 덮는 창)이 보였다가 그림 없이 사라지면 취소로 봄.
/// 도구 화면을 못 찾으면(도구·윈도우 버전 차이) 바로 끝내지 않고 시간 제한까지 클립보드를 기다림 — 신고 창은 그동안 다시 보임.
/// </summary>
public static class ScreenClip
{
    /// <summary>캡처 도구를 닫은 뒤 클립보드가 늦게 바뀌는 것을 기다리는 시간.</summary>
    private const int AfterOverlayMs = 1500;
    /// <summary>도구 화면이 이 시간 안에 안 보이면 "못 찾음"으로 보고 신고 창을 다시 띄움 (기다리기는 계속).</summary>
    public const int OverlayWaitMs = 3000;
    public const int TimeoutMs = 120_000;

    private static readonly string[] OverlayProcesses = { "ScreenClippingHost", "SnippingTool", "ScreenSketch" };

    // 시험에서 바꿔 끼움 (실제 도구·클립보드를 건드리지 않게)
    internal static Func<bool> AvailableProbe = () => ProtocolRegistered("ms-screenclip");
    internal static Func<bool> Launch = DefaultLaunch;
    internal static Func<uint> Sequence = GetClipboardSequenceNumber;
    internal static Func<bool> OverlayVisible = DefaultOverlayVisible;
    internal static Func<ReportAttachment?> ReadClipboard = () => FromDataObject(SafeClipboard());

    /// <summary>캡처 도구가 있는지 (ms-screenclip: 프로토콜 등록 — 윈도우 10 1809+·11). 없으면 버튼을 숨김.</summary>
    public static bool Available()
    {
        try { return AvailableProbe(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>
    /// 캡처 도구를 띄우고 새 그림을 기다림 (UI 스레드에서 — 클립보드는 STA). <paramref name="overlayGone"/> 은 도구 화면이 닫혔거나
    /// 못 찾았을 때 한 번 부름 (신고 창 다시 띄우기). <paramref name="stop"/> 이 취소되면 Stopped.
    /// </summary>
    public static async Task<(ReportAttachment? Shot, ScreenClipOutcome Outcome)> CaptureAsync(Action? overlayGone, CancellationToken stop, int timeoutMs = TimeoutMs)
    {
        uint seq = Sequence();
        bool launched;
        try { launched = Launch(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn($"영역 캡처: 캡처 도구 열기 실패 ({ex.GetType().Name})");
            launched = false;
        }
        if (!launched) { overlayGone?.Invoke(); return (null, ScreenClipOutcome.LaunchFailed); }
        var clock = Stopwatch.StartNew();
        bool sawOverlay = false, toldGone = false;
        long goneAt = -1;
        void Gone() { if (!toldGone) { toldGone = true; overlayGone?.Invoke(); } }
        try
        {
            while (true)
            {
                await Task.Delay(150, stop);
                if (Sequence() != seq)
                {
                    await Task.Delay(120, stop); // 클립보드에 여러 형식을 차례로 넣는 동안
                    seq = Sequence();
                    if (ReadClipboard() is { } shot)
                    {
                        Log.Info($"영역 캡처: 그림 받음 ({shot.Data.Length / 1024}KB, {clock.ElapsedMilliseconds}ms, 도구 화면 {(sawOverlay ? "봄" : "못 찾음")})");
                        Gone();
                        return (shot, ScreenClipOutcome.Captured);
                    }
                    // 그림이 아닌 것(글자 복사 등) — 계속 기다림
                }
                bool overlay = OverlayVisible();
                if (overlay) { sawOverlay = true; goneAt = -1; }
                else if (sawOverlay)
                {
                    if (goneAt < 0) goneAt = clock.ElapsedMilliseconds;
                    else if (clock.ElapsedMilliseconds - goneAt > AfterOverlayMs)
                    {
                        Log.Info($"영역 캡처: 그림 없이 도구가 닫힘 → 취소 ({clock.ElapsedMilliseconds}ms)");
                        Gone();
                        return (null, ScreenClipOutcome.Cancelled);
                    }
                }
                else if (clock.ElapsedMilliseconds > OverlayWaitMs && !toldGone)
                {
                    Log.Info("영역 캡처: 캡처 도구 화면을 못 찾음 — 신고 창을 다시 띄우고 클립보드를 계속 기다림");
                    Gone();
                }
                if (clock.ElapsedMilliseconds > timeoutMs)
                {
                    Log.Info($"영역 캡처: 시간 제한 ({timeoutMs / 1000}초)");
                    Gone();
                    return (null, sawOverlay ? ScreenClipOutcome.TimedOut : ScreenClipOutcome.NoTool);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Gone();
            return (null, ScreenClipOutcome.Stopped);
        }
    }

    /// <summary>클립보드(또는 붙여넣기) 데이터의 그림 → 첨부 (그림 없으면 null). 캡처 도구는 PNG 형식도 넣어 주므로 그것을 먼저.</summary>
    public static ReportAttachment? FromDataObject(IDataObject? data)
    {
        if (data is null) return null;
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png && png.Length > 0)
                return ReportAttachments.FromImageBytes(png.ToArray(), "clip");
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ReportAttachments.FromImageBytes(ms.ToArray(), "clip");
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException or NotSupportedException or ArgumentException or OutOfMemoryException)
        {
            Log.Warn($"클립보드 그림 읽기 실패: {ex.GetType().Name}");
        }
        return null;
    }

    /// <summary>클립보드 내용 (다른 앱이 잡고 있으면 잠깐 뒤 한 번 더, 그래도 안 되면 null).</summary>
    public static IDataObject? SafeClipboard()
    {
        for (int i = 0; i < 3; i++)
        {
            try { return Clipboard.GetDataObject(); }
            catch (Exception ex) when (ex is COMException or ExternalException) { Thread.Sleep(40); }
        }
        return null;
    }

    private static bool DefaultLaunch()
    {
        using var p = Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        Log.Info("영역 캡처: 윈도우 캡처 도구 띄움 (ms-screenclip:)");
        return true;
    }

    private static bool ProtocolRegistered(string scheme)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(scheme);
        return key?.GetValue("URL Protocol") is not null;
    }

    /// <summary>캡처 도구 화면: 캡처 도구 프로세스의 보이는 창이 모니터 하나를 거의 다 덮음.</summary>
    private static bool DefaultOverlayVisible()
    {
        bool found = false;
        var monitors = Monitors.GetAll();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || !GetWindowRect(h, out var r)) return true;
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w < 400 || ht < 300) return true;
            bool covers = monitors.Any(m => w >= (m.BoundsRect.Right - m.BoundsRect.Left) * 0.9 && ht >= (m.BoundsRect.Bottom - m.BoundsRect.Top) * 0.9);
            if (!covers) return true;
            GetWindowThreadProcessId(h, out uint pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (OverlayProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) { found = true; return false; }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
