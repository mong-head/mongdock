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

    [DllImport("user32.dll")]
    public static extern IntPtr GetMenu(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuItemInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFO lpmii);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
