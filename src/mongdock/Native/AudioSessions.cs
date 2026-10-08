using System.Runtime.InteropServices;

namespace Mongdock.Native;

/// <summary>
/// 녹음(캡처) 장치에서 지금 소리를 받고 있는 프로세스 찾기 — WASAPI 세션 (IAudioSessionManager2).
/// 윈도우 개인 정보 기록(ConsentStore)에 안 남는 앱(새 버전 Discord 등)도 마이크를 쓰면 여기 Active 세션으로 보인다.
/// 읽기만 한다. COM 은 호출한 스레드(감시 스레드, MTA)에서.
/// </summary>
internal static class AudioSessions
{
    private const int eCapture = 1;
    private const int DEVICE_STATE_ACTIVE = 1;
    private const int CLSCTX_ALL = 23;
    private const int AudioSessionStateActive = 1;

    /// <summary>캡처 장치들의 Active 세션 프로세스 id (pid 0·자기 자신 제외). 실패하면 빈 집합.</summary>
    public static HashSet<uint> ActiveCaptureProcessIds()
    {
        var pids = new HashSet<uint>();
        object? enumObj = null;
        try
        {
            enumObj = new MMDeviceEnumeratorClass();
            var en = (IMMDeviceEnumerator)enumObj;
            if (en.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, out var devices) != 0 || devices == null) return pids;
            try
            {
                devices.GetCount(out uint count);
                var iid = typeof(IAudioSessionManager2).GUID;
                for (uint i = 0; i < count; i++)
                {
                    if (devices.Item(i, out var device) != 0 || device == null) continue;
                    try
                    {
                        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr) continue;
                        try { Collect(mgr, pids); }
                        finally { Marshal.ReleaseComObject(mgr); }
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(devices); }
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"녹음 장치 세션 조회 실패: {ex.Message}");
        }
        finally
        {
            if (enumObj != null) Marshal.ReleaseComObject(enumObj);
        }
        pids.Remove(0);
        pids.Remove((uint)Environment.ProcessId);
        return pids;
    }

    private static void Collect(IAudioSessionManager2 mgr, HashSet<uint> pids)
    {
        if (mgr.GetSessionEnumerator(out var sessions) != 0 || sessions == null) return;
        try
        {
            sessions.GetCount(out int n);
            for (int j = 0; j < n; j++)
            {
                if (sessions.GetSession(j, out var ctl) != 0 || ctl == null) continue;
                try
                {
                    if (ctl is not IAudioSessionControl2 c2) continue;
                    if (c2.GetState(out int state) != 0 || state != AudioSessionStateActive) continue;
                    if (c2.IsSystemSoundsSession() == 0) continue; // S_OK = 시스템 소리 세션
                    if (c2.GetProcessId(out uint pid) == 0) pids.Add(pid);
                }
                finally { Marshal.ReleaseComObject(ctl); }
            }
        }
        finally { Marshal.ReleaseComObject(sessions); }
    }
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);
    [PreserveSig] int GetSessionEnumerator([MarshalAs(UnmanagedType.Interface)] out IAudioSessionEnumerator? sessionEnum);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int sessionCount);
    [PreserveSig] int GetSession(int sessionIndex, [MarshalAs(UnmanagedType.IUnknown)] out object? session);
}

/// <summary>IAudioSessionControl2 — IAudioSessionControl 메서드 9개 다음에 2 의 메서드 (vtable 순서만 맞춤).</summary>
[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid overrideValue, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference(bool optOut);
}
