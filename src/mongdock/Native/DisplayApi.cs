using System.Runtime.InteropServices;

namespace Mongdock.Native;

/// <summary>
/// 화면 밝기·모니터 이름용 Win32:
///  - dxva2.dll 모니터 구성 API (DDC/CI): 외장 모니터 밝기 읽기/쓰기. 호출 하나에 수십~수백 ms 걸릴 수 있어 반드시 백그라운드에서.
///  - QueryDisplayConfig / DisplayConfigGetDeviceInfo: GDI 장치 이름(\\.\DISPLAY1) ↔ 모니터 친숙한 이름("DELL U2720Q")·연결 방식(내장 패널 여부).
/// </summary>
internal static class DisplayApi
{
    // ── dxva2 (PhysicalMonitorEnumerationAPI.h / LowLevelMonitorConfigurationAPI.h / HighLevelMonitorConfigurationAPI.h) ──

    /// <summary>PHYSICAL_MONITOR (헤더가 pack(1)). 핸들은 DestroyPhysicalMonitors 로 해제.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint numberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint physicalMonitorArraySize,
        [Out] PHYSICAL_MONITOR[] physicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyPhysicalMonitors(uint physicalMonitorArraySize, [In] PHYSICAL_MONITOR[] physicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint minimumBrightness, out uint currentBrightness, out uint maximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMonitorBrightness(IntPtr hMonitor, uint newBrightness);

    // ── Display configuration (wingdi.h) ──

    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY: 노트북 내장 패널로 보는 값들.</summary>
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS = 6;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    /// <summary>DISPLAYCONFIG_PATH_INFO (72 바이트).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>DISPLAYCONFIG_MODE_INFO (64 바이트). 공용체 내용은 쓰지 않으므로 크기만 맞춤.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    public static bool IsInternalTechnology(uint tech) =>
        tech is DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL or DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
            or DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED or DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS;

    /// <summary>
    /// 활성 디스플레이 경로마다 (GDI 장치 이름, 친숙한 이름, 내장 패널 여부). 실패하면 빈 목록.
    /// 복제(미러) 구성이면 같은 GDI 이름이 여러 번 나올 수 있다.
    /// </summary>
    public static List<(string GdiName, string FriendlyName, bool Internal)> GetActiveTargets()
    {
        var result = new List<(string, string, bool)>();
        DISPLAYCONFIG_PATH_INFO[] paths;
        uint np, nm;
        int rc;
        int tries = 0;
        do
        {
            rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm);
            if (rc != ERROR_SUCCESS) return result;
            paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
        } while (rc == ERROR_INSUFFICIENT_BUFFER && ++tries < 3); // 그 사이 구성이 바뀌면 다시
        if (rc != ERROR_SUCCESS) return result;

        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = p.sourceInfo.adapterId,
                    id = p.sourceInfo.id,
                },
                viewGdiDeviceName = "",
            };
            if (DisplayConfigGetDeviceInfo(ref src) != ERROR_SUCCESS) continue;
            var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = p.targetInfo.adapterId,
                    id = p.targetInfo.id,
                },
                monitorFriendlyDeviceName = "",
                monitorDevicePath = "",
            };
            string friendly = "";
            uint tech = p.targetInfo.outputTechnology;
            if (DisplayConfigGetDeviceInfo(ref tgt) == ERROR_SUCCESS)
            {
                friendly = tgt.monitorFriendlyDeviceName ?? "";
                tech = tgt.outputTechnology;
            }
            result.Add((src.viewGdiDeviceName ?? "", friendly, IsInternalTechnology(tech)));
        }
        return result;
    }
}
