using System.Runtime.InteropServices;

namespace MyDock.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct APPBARDATA
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public IntPtr lParam;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEINFO
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHSTOCKICONINFO
{
    public uint cbSize;
    public IntPtr hIcon;
    public int iSysImageIndex;
    public int iIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szPath;
}

internal static class Shell32
{
    // AppBar
    public const uint ABM_NEW = 0x0;
    public const uint ABM_REMOVE = 0x1;
    public const uint ABM_QUERYPOS = 0x2;
    public const uint ABM_SETPOS = 0x3;
    public const uint ABM_ACTIVATE = 0x6;
    public const uint ABM_WINDOWPOSCHANGED = 0x9;

    public const uint ABE_LEFT = 0;
    public const uint ABE_TOP = 1;
    public const uint ABE_RIGHT = 2;
    public const uint ABE_BOTTOM = 3;

    public const int ABN_STATECHANGE = 0;
    public const int ABN_POSCHANGED = 1;
    public const int ABN_FULLSCREENAPP = 2;

    // Shell hook (wParam)
    public const int HSHELL_WINDOWCREATED = 1;
    public const int HSHELL_WINDOWDESTROYED = 2;
    public const int HSHELL_WINDOWACTIVATED = 4;
    public const int HSHELL_REDRAW = 6;
    public const int HSHELL_WINDOWREPLACED = 13;
    public const int HSHELL_RUDEAPPACTIVATED = 0x8004;
    public const int HSHELL_FLASH = 0x8006;

    // SHGetFileInfo
    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;

    // SHGetImageList
    public const int SHIL_EXTRALARGE = 0x2;
    public const int SHIL_JUMBO = 0x4;
    public const int ILD_TRANSPARENT = 0x1;

    // SHGetStockIconInfo
    public const uint SIID_APPLICATION = 2;
    public const uint SHGSI_ICON = 0x000000100;
    public const uint SHGSI_LARGEICON = 0x000000000;

    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    public static extern int SHGetImageList(int iImageList, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

    [DllImport("shell32.dll")]
    public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    // ── 헬퍼 ──

    /// <summary>창의 PKEY_AppUserModel_ID (명시적으로 설정된 경우만). 없으면 null.</summary>
    public static string? GetWindowAumid(IntPtr hwnd)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store is null) return null;
            var key = PropertyKeys.AppUserModel_ID;
            if (store.GetValue(ref key, out PROPVARIANT pv) != 0) return null;
            try
            {
                if (pv.vt == PROPVARIANT.VT_LPWSTR && pv.p != IntPtr.Zero)
                {
                    string? s = Marshal.PtrToStringUni(pv.p);
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }
                return null;
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }
}
