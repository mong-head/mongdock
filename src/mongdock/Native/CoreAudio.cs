using System.Runtime.InteropServices;

namespace Mongdock.Native;

// Core Audio (MMDevice API / EndpointVolume) 최소 선언. vtable 순서 유지.

internal static class CoreAudio
{
    public const int eRender = 0;
    public const int eConsole = 0;
    public const int eMultimedia = 1;
    public const int eCommunications = 2;
    public const int DEVICE_STATE_ACTIVE = 0x1;
    public const int STGM_READ = 0;

    public static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static readonly PROPERTYKEY PKEY_AudioEndpoint_FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    // EndpointFormFactor
    public const int FF_Speakers = 1, FF_Headphones = 3, FF_Headset = 5, FF_UnknownDigitalPassthrough = 7, FF_SPDIF = 8, FF_DigitalAudioDisplayDevice = 9;
    public const int CLSCTX_ALL = 0x17;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorClass
{
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, [MarshalAs(UnmanagedType.Interface)] out IMMDeviceCollection? devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);
    [PreserveSig] int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);
}

/// <summary>
/// 비공개 IPolicyConfig (Windows 10/11, IID f8679f50-...). 기본 재생 장치 변경용. SetDefaultEndpoint 까지 vtable 순서만 맞춤.
/// </summary>
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(IntPtr deviceName, IntPtr format);
    [PreserveSig] int GetDeviceFormat(IntPtr deviceName, int defaultFormat, IntPtr format);
    [PreserveSig] int ResetDeviceFormat(IntPtr deviceName);
    [PreserveSig] int SetDeviceFormat(IntPtr deviceName, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod(IntPtr deviceName, int defaultPeriod, IntPtr defPeriod, IntPtr minPeriod);
    [PreserveSig] int SetProcessingPeriod(IntPtr deviceName, IntPtr period);
    [PreserveSig] int GetShareMode(IntPtr deviceName, IntPtr mode);
    [PreserveSig] int SetShareMode(IntPtr deviceName, IntPtr mode);
    [PreserveSig] int GetPropertyValue(IntPtr deviceName, int fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue(IntPtr deviceName, int fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient
{
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);
    [PreserveSig] int UnregisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr notifyData);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PROPERTYKEY key);
}

internal static class Wlan
{
    public const uint WLAN_CLIENT_VERSION_2 = 2;
    public const int wlan_intf_opcode_current_connection = 7;
    public const int wlan_interface_state_connected = 1;
    public const int ERROR_SUCCESS = 0;

    // WLAN_INTERFACE_INFO: GUID(16) + WCHAR[256](512) + enum(4)
    public const int InterfaceInfoSize = 532;
    // WLAN_CONNECTION_ATTRIBUTES 오프셋
    public const int Conn_SsidLength = 520;
    public const int Conn_Ssid = 524;
    public const int Conn_SignalQuality = 576;

    [DllImport("wlanapi.dll")]
    public static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    public static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    public static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    public static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out uint dataSize, out IntPtr data, IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr memory);
}
