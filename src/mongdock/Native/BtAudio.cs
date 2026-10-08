using System.Runtime.InteropServices;

namespace Mongdock.Native;

// 블루투스 오디오 연결/해제용 최소 선언 (DeviceTopology + IKsControl).
// 값 출처: Windows SDK ksmedia.h / devicetopology.h (microsoft/windows-rs 메타데이터로 교차 확인).

internal static class BtAudioNative
{
    /// <summary>KSPROPSETID_BtAudio (ksmedia.h).</summary>
    public static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    /// <summary>KSPROPERTY_BTAUDIO 열거: ONESHOT_RECONNECT = 0, ONESHOT_DISCONNECT = 1. 둘 다 Filter 대상 GET, 값 버퍼 없음.</summary>
    public const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
    public const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;
    public const uint KSPROPERTY_TYPE_GET = 0x00000001;

    public static readonly Guid IID_IDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    public static readonly Guid IID_IKsControl = new("28F54685-06FD-11D2-B27A-00A0C9223196");

    /// <summary>DEVICE_STATE_ACTIVE | DISABLED | NOTPRESENT | UNPLUGGED.</summary>
    public const int DEVICE_STATEMASK_ALL = 0x0000000F;
    public const int eRender = 0;
    public const int CLSCTX_ALL = 0x17;
    public const int STGM_READ = 0;
    public const ushort VT_CLSID = 72;

    /// <summary>PKEY_Device_ContainerId (devpkey.h) — 오디오 엔드포인트 속성 저장소에서 블루투스 기기의 컨테이너 ID.</summary>
    public static readonly PROPERTYKEY PKEY_Device_ContainerId = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);
}

/// <summary>KSPROPERTY (= KSIDENTIFIER). 16 + 4 + 4 = 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KSPROPERTY
{
    public Guid Set;
    public uint Id;
    public uint Flags;
}

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceTopology
{
    [PreserveSig] int GetConnectorCount(out uint count);
    [PreserveSig] int GetConnector(uint index, [MarshalAs(UnmanagedType.Interface)] out IConnector? connector);
    [PreserveSig] int GetSubunitCount(out uint count);
    [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
    [PreserveSig] int GetPartById(uint id, out IntPtr part);
    [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string? deviceId);
    [PreserveSig] int GetSignalPath(IntPtr partFrom, IntPtr partTo, int rejectMixedPaths, out IntPtr parts);
}

[ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConnector
{
    [PreserveSig] int GetConnectorType(out int type);
    [PreserveSig] int GetDataFlow(out int flow);
    [PreserveSig] int ConnectTo(IntPtr connectTo);
    [PreserveSig] int Disconnect();
    [PreserveSig] int IsConnected(out int connected);
    [PreserveSig] int GetConnectedTo([MarshalAs(UnmanagedType.Interface)] out IConnector? connectedTo);
    [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? connectorId);
    [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? deviceId);
}

[ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPart
{
    [PreserveSig] int GetName(out IntPtr name);
    [PreserveSig] int GetLocalId(out uint id);
    [PreserveSig] int GetGlobalId(out IntPtr globalId);
    [PreserveSig] int GetPartType(out int partType);
    [PreserveSig] int GetSubType(out Guid subType);
    [PreserveSig] int GetControlInterfaceCount(out uint count);
    [PreserveSig] int GetControlInterface(uint index, out IntPtr desc);
    [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
    [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
    [PreserveSig] int GetTopologyObject([MarshalAs(UnmanagedType.Interface)] out IDeviceTopology? topology);
}

[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IKsControl
{
    [PreserveSig] int KsProperty(ref KSPROPERTY property, uint propertyLength, IntPtr propertyData, uint dataLength, out uint bytesReturned);
    [PreserveSig] int KsMethod(IntPtr method, uint methodLength, IntPtr methodData, uint dataLength, out uint bytesReturned);
    [PreserveSig] int KsEvent(IntPtr evt, uint eventLength, IntPtr eventData, uint dataLength, out uint bytesReturned);
}
