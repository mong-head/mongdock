using System.Runtime.InteropServices;

namespace Mongdock.Native;

/// <summary>다른 앱 창 위치 조정(WindowNudger)용 P/Invoke.</summary>
internal static class WindowPosApi
{
    public const uint GA_ROOT = 2;

    public const long WS_CHILD = 0x40000000;
    public const long WS_CAPTION = 0x00C00000;
    public const long WS_THICKFRAME = 0x00040000;

    /// <summary>SetWindowPos: 대상 창 스레드가 바빠도 호출한 쪽이 기다리지 않게.</summary>
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    /// <summary>DWM 이 그리는 실제 창 테두리 (보이지 않는 크기 조절 테두리 제외, 물리 px).</summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    /// <summary>hWnd = 0 이면 스레드 타이머: 이 스레드 메시지 큐에 WM_TIMER(hwnd 0)를 넣는다.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    public const uint WM_TIMER = 0x0113;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    /// <summary>보이는 창 테두리 (DWM). 실패하면 GetWindowRect.</summary>
    public static bool TryGetFrameBounds(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0 && rect.Width > 0)
            return true;
        return User32.GetWindowRect(hwnd, out rect);
    }
}
