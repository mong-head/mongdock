using System.Runtime.InteropServices;

namespace Mongdock.Native;

/// <summary>저수준 키보드/마우스 훅(WH_KEYBOARD_LL / WH_MOUSE_LL) · 훅 전용 스레드 메시지 루프 P/Invoke.</summary>
internal static class KeyboardHookApi
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_USER = 0x0400;

    /// <summary>KBDLLHOOKSTRUCT.flags: 주입된 입력 (SendInput 등).</summary>
    public const uint LLKHF_INJECTED = 0x10;

    public const ushort VK_SPACE = 0x20;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    /// <summary>할당되지 않은 VK — 시작 메뉴/메뉴 막대 억제용 더미 키 (PowerToys 방식).</summary>
    public const ushort VK_DUMMY = 0xE8;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    /// <summary>LL 훅 콜백 (키보드·마우스 공통 시그니처).</summary>
    public delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetModuleHandleW")]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>반환: 0 = WM_QUIT, -1 = 오류, 그 외 = 메시지.</summary>
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetMessageW")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>지금 눌려 있는지 (비동기 키 상태의 최상위 비트).</summary>
    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
