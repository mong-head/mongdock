using System.Runtime.InteropServices;

namespace MyDock.Native;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MENUITEMINFO
{
    public uint cbSize;
    public uint fMask;
    public uint fType;
    public uint fState;
    public uint wID;
    public IntPtr hSubMenu;
    public IntPtr hbmpChecked;
    public IntPtr hbmpUnchecked;
    public IntPtr dwItemData;
    public IntPtr dwTypeData;
    public uint cch;
    public IntPtr hbmpItem;
}

/// <summary>Win32 메뉴 읽기 (다른 프로세스 창의 메뉴도 읽기만 — 메뉴 객체는 데스크톱 힙에 있어 프로세스와 무관하게 조회 가능).</summary>
internal static class MenuApi
{
    public const uint MIIM_STATE = 0x01;
    public const uint MIIM_ID = 0x02;
    public const uint MIIM_SUBMENU = 0x04;
    public const uint MIIM_STRING = 0x40;
    public const uint MIIM_FTYPE = 0x100;

    public const uint MFT_SEPARATOR = 0x800;

    public const uint MFS_DISABLED = 0x03; // MFS_GRAYED | MFS_DISABLED
    public const uint MFS_CHECKED = 0x08;

    public const uint WM_COMMAND = 0x0111;
    public const uint WM_SYSCOMMAND = 0x0112;

    // WM_SYSCOMMAND wParam (앱 메뉴 규칙의 action)
    public const int SC_MINIMIZE = 0xF020;
    public const int SC_MAXIMIZE = 0xF030;
    public const int SC_CLOSE = 0xF060;
    public const int SC_RESTORE = 0xF120;

    [DllImport("user32.dll")]
    public static extern IntPtr GetMenu(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuItemInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFO lpmii);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsMenu(IntPtr hMenu);

    public const uint WM_INITMENUPOPUP = 0x0117;

    /// <summary>
    /// 창의 메뉴 막대를 바꿈/뗌 (hMenu = Zero). 다른 프로세스 창에도 동작함을 직접 확인(msinfo32, Windows 11 26200).
    /// 뗀 HMENU 는 없어지지 않으므로 보관해 두었다가 다시 붙여야 한다. 내부적으로 대상 창 스레드에 WM_NCCALCSIZE 를
    /// 보내므로(동기) 응답 없는 앱이면 막힐 수 있다 → UI 스레드에서 부르지 말 것.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenu(IntPtr hWnd, IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
