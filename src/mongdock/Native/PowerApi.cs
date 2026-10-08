using System.Runtime.InteropServices;

namespace Mongdock.Native;

internal static class PowerApi
{
    public const uint EWX_LOGOFF = 0x00000000;
    public const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);
}
