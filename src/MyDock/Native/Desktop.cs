using System.Runtime.InteropServices;

namespace MyDock.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ACCENT_POLICY
{
    public int AccentState;
    public int AccentFlags;
    public uint GradientColor; // ABGR
    public int AnimationId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWCOMPOSITIONATTRIBDATA
{
    public int Attrib;
    public IntPtr pvData;
    public int cbData;
}

/// <summary>모니터·DPI·화면 캡처·창 모양·합성(블러) 관련 API.</summary>
internal static class DesktopApi
{
    public const uint MONITOR_DEFAULTTONULL = 0;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const int MDT_EFFECTIVE_DPI = 0;

    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_DISABLED = 0;
    // ACCENT_ENABLE_BLURBEHIND(3) 는 Win11 26200 에서 검게 나와 사용하지 않음
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_ROUND = 2;
    public const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    public const uint SRCCOPY = 0x00CC0020;
    public const int WS_CAPTION = 0x00C00000;

    // 브로드캐스트/설정 변경
    public const int SPI_SETDESKWALLPAPER = 0x0014;
    public const int SPI_SETWORKAREA = 0x002F;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    // ── 저수준 마우스 훅 ──
    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetModuleHandleW")]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    // ── 헬퍼 ──

    public static IntPtr PrimaryMonitor => MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);

    /// <summary>모니터 영역/작업 영역 (물리 픽셀, PerMonitorV2 기준).</summary>
    public static bool TryGetMonitorRects(IntPtr monitor, out RECT bounds, out RECT work)
    {
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        bool ok = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref mi);
        bounds = mi.rcMonitor;
        work = mi.rcWork;
        return ok;
    }

    /// <summary>모니터 배율 (1.0 = 96 DPI).</summary>
    public static double GetMonitorScale(IntPtr monitor)
    {
        try
        {
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint x, out _) == 0 && x > 0)
                return x / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1.0;
    }

    public static int SetAccent(IntPtr hwnd, int state, uint abgr)
    {
        var policy = new ACCENT_POLICY { AccentState = state, AccentFlags = state == ACCENT_DISABLED ? 0 : 2, GradientColor = abgr };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, p, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attrib = WCA_ACCENT_POLICY, pvData = p, cbData = size };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    public static void SetDwmInt(IntPtr hwnd, int attr, int value)
    {
        try { DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
    }
}

/// <summary>IVirtualDesktopManager (문서화된 COM). 창이 현재 가상 데스크톱에 있는지 / 어느 데스크톱인지.</summary>
[ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
    [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

[ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
internal class VirtualDesktopManagerClass
{
}

/// <summary>dwmapi.h 는 이 구조체를 pshpack1 (1바이트 정렬)로 선언 → Pack=1 (45바이트).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DWM_THUMBNAIL_PROPERTIES
{
    public uint dwFlags;
    public RECT rcDestination;
    public RECT rcSource;
    public byte opacity;
    [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
    [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
}

internal static class DwmThumbnail
{
    public const uint DWM_TNP_RECTDESTINATION = 0x1;
    public const uint DWM_TNP_OPACITY = 0x4;
    public const uint DWM_TNP_VISIBLE = 0x8;
    public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnail, out SIZE pSize);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);
}
