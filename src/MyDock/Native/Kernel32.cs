using System.Runtime.InteropServices;
using System.Text;

namespace MyDock.Native;

internal static class Kernel32
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    /// <summary>패키지 프로세스의 AUMID. 패키지가 아니면 APPMODEL_ERROR_NO_APPLICATION(15703).</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint applicationUserModelIdLength, StringBuilder? applicationUserModelId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>pid → (exe 전체 경로, 패키지 AUMID). 접근 거부 등 실패 시 ("", null).</summary>
    public static (string Path, string? Aumid) QueryProcess(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return ("", null);
        try
        {
            string path = "";
            uint size = 1024;
            var sb = new StringBuilder((int)size);
            if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();

            string? aumid = null;
            uint len = 0;
            int rc = GetApplicationUserModelId(h, ref len, null);
            if (rc == ERROR_INSUFFICIENT_BUFFER && len > 0)
            {
                var ab = new StringBuilder((int)len);
                if (GetApplicationUserModelId(h, ref len, ab) == 0) aumid = ab.ToString();
            }
            return (path, string.IsNullOrEmpty(aumid) ? null : aumid);
        }
        finally
        {
            CloseHandle(h);
        }
    }
}

internal static class Imm32
{
    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    public const int IMC_GETCONVERSIONMODE = 0x0001;
    public const int IMC_GETOPENSTATUS = 0x0005;
    /// <summary>한국어 IME 에서는 IME_CMODE_HANGUL 과 같은 값.</summary>
    public const int IME_CMODE_NATIVE = 0x0001;
}

internal static class Dwm
{
    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>셸이 숨긴 창 (다른 가상 데스크톱의 창 등).</summary>
    public const int DWM_CLOAKED_SHELL = 0x2;

    /// <summary>DWMWA_CLOAKED 값 (0 = 보임, 1 = 앱, 2 = 셸, 4 = 상속). 실패 시 0.</summary>
    public static int GetCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 ? cloaked : 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAP
{
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public ushort bmPlanes;
    public ushort bmBitsPixel;
    public IntPtr bmBits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    // 32bpp BI_RGB 에서는 색상 테이블이 없지만 GetDIBits 가 쓸 수 있는 여유 공간을 둔다.
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public uint[] bmiColors;
}

internal static class Gdi32
{
    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, [Out] byte[] lpvBits,
        ref BITMAPINFO lpbmi, uint usage);
}
