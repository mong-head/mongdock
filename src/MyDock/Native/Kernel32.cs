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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "K32GetProcessImageFileNameW")]
    public static extern uint GetProcessImageFileName(IntPtr hProcess, StringBuilder lpImageFileName, uint nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryDosDeviceW")]
    public static extern uint QueryDosDevice(string? lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

    /// <summary>
    /// pid → (exe 전체 경로, 패키지 AUMID).
    /// 1) QueryFullProcessImageName (관리자 권한 프로세스도 PROCESS_QUERY_LIMITED_INFORMATION 이면 보통 됨)
    /// 2) GetProcessImageFileName (NT 경로 \Device\HarddiskVolumeN\... → C:\...)
    /// 3) 열 수 없는 프로세스(SYSTEM 의 consent.exe 등) → 프로세스 이름만 얻어 System32\이름.exe 가 있으면 그 경로
    /// 모두 실패하면 ("", null).
    /// </summary>
    public static (string Path, string? Aumid) QueryProcess(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (PathFromProcessName(pid), null);
        try
        {
            string path = "";
            uint size = 1024;
            var sb = new StringBuilder((int)size);
            if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            if (path.Length == 0)
            {
                var nb = new StringBuilder(1024);
                if (GetProcessImageFileName(h, nb, (uint)nb.Capacity) > 0) path = NtPathToDos(nb.ToString()) ?? "";
            }
            if (path.Length == 0) path = PathFromProcessName(pid);

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

    /// <summary>"\Device\HarddiskVolume3\Windows\x.exe" → "C:\Windows\x.exe". 실패 시 null.</summary>
    public static string? NtPathToDos(string ntPath)
    {
        try
        {
            foreach (var drive in Environment.GetLogicalDrives())
            {
                string letter = drive.TrimEnd('\\');
                var target = new StringBuilder(512);
                if (QueryDosDevice(letter, target, (uint)target.Capacity) == 0) continue;
                string dev = target.ToString();
                if (ntPath.StartsWith(dev + "\\", StringComparison.OrdinalIgnoreCase))
                    return letter + ntPath[dev.Length..];
            }
        }
        catch { }
        return null;
    }

    /// <summary>열 수 없는 프로세스: 이름만 얻어(접근 권한 불필요) System32\이름.exe 가 있으면 그 경로. 없으면 "".</summary>
    private static string PathFromProcessName(uint pid)
    {
        string? name = ProcessName(pid);
        if (string.IsNullOrEmpty(name)) return "";
        string sys = System.IO.Path.Combine(Environment.SystemDirectory, name + ".exe");
        return System.IO.File.Exists(sys) ? sys : "";
    }

    /// <summary>프로세스 이름 (경로를 모를 때 키·로그용, 접근 권한 불필요). 실패 시 null.</summary>
    public static string? ProcessName(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    // ── 무결성 수준 ──
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>프로세스 무결성 RID (0x2000 보통, 0x3000 높음(관리자), 0x4000 시스템). pid 0 = 자기 자신. 토큰을 못 열면 null.</summary>
    public static int? GetIntegrityLevel(uint pid)
    {
        IntPtr h = pid == 0 ? GetCurrentProcess() : OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try { return IntegrityOf(h); }
        finally { if (pid != 0) CloseHandle(h); }
    }

    private static int? IntegrityOf(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
            if (len <= 0) return null;
            buf = Marshal.AllocHGlobal(len);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out _)) return null;
            IntPtr sid = Marshal.ReadIntPtr(buf); // TOKEN_MANDATORY_LABEL.Label.Sid
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) return null;
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(token);
        }
    }

    private static int? _ownIntegrity;

    /// <summary>프로세스가 우리보다 높은 무결성 수준인지 (관리자 권한 창 등 — UIPI 로 AttachThreadInput·입력이 막힘).
    /// 토큰을 못 열면(대개 상승된·SYSTEM 프로세스) true.</summary>
    public static bool IsHigherIntegrity(uint pid)
    {
        _ownIntegrity ??= GetIntegrityLevel(0) ?? 0x2000;
        int? other = GetIntegrityLevel(pid);
        return other is null || other > _ownIntegrity;
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
