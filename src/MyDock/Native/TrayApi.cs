using System.Runtime.InteropServices;

namespace MyDock.Native;

/// <summary>
/// 알림 영역(트레이) 가로채기용 Win32 선언 — TrayIconService 전용.
/// Shell_NotifyIcon 은 FindWindow("Shell_TrayWnd") 로 찾은 창에 WM_COPYDATA(dwData=1, SHELLTRAYDATA)를 보낸다.
/// 32/64비트 앱 모두 같은 레이아웃(핸들 32비트)으로 보낸다 (shell32 가 변환).
/// </summary>
internal static class TrayApi
{
    public const string TrayWndClass = "Shell_TrayWnd";
    public const string NotifyWndClass = "TrayNotifyWnd";

    // ── 메시지 ──
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_ENDSESSION = 0x0016;
    public const uint WM_WINDOWPOSCHANGING = 0x0046;
    public const uint WM_COPYDATA = 0x004A;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_USER = 0x0400;
    /// <summary>RegisterWindowMessage 범위 시작 — 브로드캐스트일 수 있어 explorer 로 전달하지 않음.</summary>
    public const uint RegisteredMessageFirst = 0xC000;
    public const uint WM_QUIT = 0x0012;

    // ── NOTIFYICON ──
    public const uint NIN_SELECT = WM_USER + 0;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETFOCUS = 3, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_STATE = 0x08, NIF_GUID = 0x20;
    public const uint NIS_HIDDEN = 0x01;
    /// <summary>WM_SYSCOMMAND wParam (하위 4비트 제외) — 닫기.</summary>
    public const int SC_CLOSE = 0xF060;

    /// <summary>SHELLTRAYDATA 시그니처 (dwSignature).</summary>
    public const int ShellTraySignature = 0x34753423;

    // SHELLTRAYDATA = { int dwSignature; uint dwMessage; NOTIFYICONDATA32 nid; } — nid 오프셋 8.
    // NOTIFYICONDATA32 (핸들 32비트, 유니코드) 필드 오프셋 (쓰는 것만. 나머지: cbSize 0, szInfo 288 WCHAR[256],
    // szInfoTitle 804 WCHAR[64], dwInfoFlags 932, hBalloonIcon 952, 전체 956):
    public const int NidOffset = 8;
    public const int Nid_hWnd = 4;
    public const int Nid_uID = 8;
    public const int Nid_uFlags = 12;
    public const int Nid_uCallbackMessage = 16;
    public const int Nid_hIcon = 20;
    public const int Nid_szTip = 24;          // WCHAR[128]
    public const int Nid_szTipChars = 128;
    public const int Nid_dwState = 280;
    public const int Nid_dwStateMask = 284;
    public const int Nid_uVersion = 800;      // union uTimeout/uVersion
    public const int Nid_guidItem = 936;

    // WINNOTIFYICONIDENTIFIER (Shell_NotifyIconGetRect, dwData=3):
    // { int dwMagic; int dwMessage; int cbSize; int dwPadding; uint hWnd; uint uID; GUID guidItem; } = 40 바이트
    public const int IconId_dwMessage = 4;
    public const int IconId_hWnd = 16;
    public const int IconId_uID = 20;
    public const int IconId_guidItem = 24;
    public const int IconIdSize = 40;

    // ── 창 ──
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CHILD = 0x40000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    public const uint MSGFLT_ALLOW = 1;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const uint ISMEX_SEND = 0x00000001;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public uint cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string? lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendNotifyMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    public static extern uint InSendMessageEx(IntPtr lpReserved);

    [DllImport("user32.dll", EntryPoint = "SetTimer", SetLastError = true)]
    public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW", SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW", SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ── AppBar 메시지 (SHAppBarMessage → WM_COPYDATA dwData=0) ──
    // APPBARMSGDATAV3 (32/64비트 공통 고정 레이아웃, 64바이트):
    // { APPBARDATAV2 abd(40) ; int dwMessage ; int pad ; long hSharedMemory ; int dwSourceProcessId ; int pad }
    // APPBARDATAV2 = { int cbSize; uint hWnd; uint uCallbackMessage; uint uEdge; RECT rc; long lParam } = 40바이트
    public const int AppBarMsgSize = 64;
    public const int AppBarDataSize = 40;
    public const int AppBar_hSharedMemory = 48;
    public const int AppBar_dwSourceProcessId = 56;

    // SHAllocShared 등 (shlwapi 서수 7~10, 문서화된 함수)
    [DllImport("shlwapi.dll", EntryPoint = "#7", SetLastError = true)]
    public static extern IntPtr SHAllocShared(IntPtr pvData, uint dwSize, uint dwProcessId);

    [DllImport("shlwapi.dll", EntryPoint = "#8", SetLastError = true)]
    public static extern IntPtr SHLockShared(IntPtr hData, uint dwProcessId);

    [DllImport("shlwapi.dll", EntryPoint = "#9", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHUnlockShared(IntPtr pvData);

    [DllImport("shlwapi.dll", EntryPoint = "#10", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHFreeShared(IntPtr hData, uint dwProcessId);

    /// <summary>MAKELPARAM — 16비트 둘을 하나로 (좌표는 부호 있는 16비트로 잘림).</summary>
    public static IntPtr MakeLParam(int low, int high) => (IntPtr)(int)((uint)(ushort)low | ((uint)(ushort)high << 16));

    /// <summary>
    /// explorer 의 Shell_TrayWnd (우리 프로세스 창 제외). FindWindow 는 Z 순서 맨 위의 같은 클래스 창을 주므로,
    /// 몽독이 트레이를 가로채는 동안엔 몽독 창이 먼저 잡힌다 → 같은 클래스 창을 차례로 훑어 다른 프로세스 것을 고른다.
    /// </summary>
    public static IntPtr FindExplorerTray()
    {
        // 셸(바탕 화면 창 소유 = explorer) 프로세스의 것을 우선 — RetroBar 처럼 같은 방식으로 가로채는 다른 프로그램과
        // 서로에게 전달하며 무한히 돌지 않게. 셸을 못 찾으면 몽독 것이 아닌 첫 창.
        uint self = (uint)Environment.ProcessId;
        uint shellPid = 0;
        IntPtr shell = GetShellWindow();
        if (shell != IntPtr.Zero) GetWindowThreadProcessId(shell, out shellPid);
        IntPtr after = IntPtr.Zero, firstOther = IntPtr.Zero;
        for (int i = 0; i < 16; i++)
        {
            after = FindWindowEx(IntPtr.Zero, after, TrayWndClass, null);
            if (after == IntPtr.Zero) break;
            GetWindowThreadProcessId(after, out uint pid);
            if (pid == self) continue;
            if (shellPid != 0 && pid == shellPid) return after;
            if (firstOther == IntPtr.Zero) firstOther = after;
        }
        // 셸이 있는데 그 Shell_TrayWnd 가 없으면(탐색기 재시작 중) 다른 가로채기 프로그램 쪽으로 보내지 않음
        return shellPid != 0 && shellPid != self ? IntPtr.Zero : firstOther;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    /// <summary>FindWindowEx 의 부모로 주면 메시지 전용 창(HWND_MESSAGE)을 열거.</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // ── NotifyIconSettings (윈도우 11 트레이 아이콘 설정) 읽기 ──

    public const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    /// <summary>KNOWNFOLDERID → 경로 (ppszPath 는 CoTaskMemFree 로 해제). 성공 시 0(S_OK).</summary>
    [DllImport("shell32.dll", ExactSpelling = true)]
    public static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public const uint REG_NOTIFY_CHANGE_NAME = 0x1;
    public const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    /// <summary>레지스트리 키 변경 알림 (비동기: hEvent 가 신호됨). 성공 시 0(ERROR_SUCCESS). 오류 코드를 직접 반환.</summary>
    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree, uint dwNotifyFilter, Microsoft.Win32.SafeHandles.SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);
}
