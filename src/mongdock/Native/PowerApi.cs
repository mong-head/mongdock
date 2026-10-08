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

    /// <summary>SYSTEM_POWER_STATUS (GetSystemPowerStatus).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        /// <summary>0 = 배터리, 1 = 전원 연결, 255 = 모름.</summary>
        public byte ACLineStatus;
        /// <summary>1 높음, 2 낮음, 4 위험, 8 충전 중, 128 배터리 없음, 255 모름.</summary>
        public byte BatteryFlag;
        /// <summary>0~100, 255 = 모름.</summary>
        public byte BatteryLifePercent;
        /// <summary>1 = 절전 모드(battery saver) 켜짐.</summary>
        public byte SystemStatusFlag;
        /// <summary>남은 초, -1(0xFFFFFFFF) = 모름.</summary>
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public const byte BATTERY_FLAG_CHARGING = 8;
    public const byte BATTERY_FLAG_NO_BATTERY = 128;
    public const byte BATTERY_FLAG_UNKNOWN = 255;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
