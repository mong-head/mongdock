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
        => ActiveCaptureByDevice().Values.SelectMany(p => p).ToHashSet();

    /// <summary>캡처 장치 id → 그 장치에서 지금 녹음 중인 프로세스 id (pid 0·자기 자신 제외). 실패하면 빈 사전.</summary>
    public static Dictionary<string, HashSet<uint>> ActiveCaptureByDevice()
    {
        var byDevice = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        object? enumObj = null;
        try
        {
            enumObj = new MMDeviceEnumeratorClass();
            var en = (IMMDeviceEnumerator)enumObj;
            if (en.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, out var devices) != 0 || devices == null) return byDevice;
            try
            {
                devices.GetCount(out uint count);
                var iid = typeof(IAudioSessionManager2).GUID;
                for (uint i = 0; i < count; i++)
                {
                    if (devices.Item(i, out var device) != 0 || device == null) continue;
                    try
                    {
                        if (device.GetId(out string? id) != 0 || id == null) continue;
                        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr) continue;
                        var pids = new HashSet<uint>();
                        try { Collect(mgr, pids); }
                        finally { Marshal.ReleaseComObject(mgr); }
                        pids.Remove(0);
                        pids.Remove((uint)Environment.ProcessId);
                        if (pids.Count > 0) byDevice[id] = pids;
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
        return byDevice;
    }

    /// <summary>
    /// 기본 출력(재생) 장치의 세션 하나 — 앱별 볼륨 (#21). IdPath = 세션 식별자 안의 실행 파일 경로(DOS 경로, 없으면 "") —
    /// 여러 프로세스가 함께 쓰는 세션(크롬·엣지·디스코드 등의 오디오 하위 프로세스)은 pid 가 0 으로 와서 이것으로 앱을 앎.
    /// </summary>
    public readonly record struct RenderSession(uint Pid, bool System, bool Active, float Volume, bool Muted, string IdPath = "");

    /// <summary>
    /// 세션 식별자 "{장치}|\Device\HarddiskVolume3\…\chrome.exe%b{…}" 에서 실행 파일 경로 (DOS 경로로). 없으면 "".
    /// 시스템 소리 세션은 "…|#%b{…}" 라 "".
    /// </summary>
    internal static string ExeFromSessionId(string? id, Func<string, string?>? ntToDos = null)
    {
        if (string.IsNullOrEmpty(id)) return "";
        int bar = id.IndexOf('|');
        if (bar < 0) return "";
        int end = id.IndexOf("%b", bar + 1, StringComparison.Ordinal);
        string raw = (end < 0 ? id[(bar + 1)..] : id[(bar + 1)..end]).Trim();
        if (!raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
        if (raw.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return (ntToDos ?? Kernel32.NtPathToDos)(raw) ?? "";
        return raw.Length > 2 && raw[1] == ':' ? raw : "";
    }

    /// <summary>
    /// 기본 출력 장치의 오디오 세션들 (만료된 것 빼고). <paramref name="apply"/> 가 있으면 각 세션의 볼륨 인터페이스에 그것을 해 봄
    /// (pid·시스템 여부를 보고 맞는 세션만 바꾸게). 실패하면 빈 목록. 호출한 스레드(MTA 권장)에서 COM.
    /// </summary>
    public static List<RenderSession> RenderSessions(Func<RenderSession, ISimpleAudioVolume, bool>? apply = null)
    {
        var list = new List<RenderSession>();
        object? enumObj = null;
        try
        {
            enumObj = new MMDeviceEnumeratorClass();
            var en = (IMMDeviceEnumerator)enumObj;
            if (en.GetDefaultAudioEndpoint(CoreAudio.eRender, CoreAudio.eMultimedia, out var device) != 0 || device == null) return list;
            try
            {
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr) return list;
                try
                {
                    if (mgr.GetSessionEnumerator(out var sessions) != 0 || sessions == null) return list;
                    try
                    {
                        sessions.GetCount(out int n);
                        for (int j = 0; j < n; j++)
                        {
                            if (sessions.GetSession(j, out var ctl) != 0 || ctl == null) continue;
                            try
                            {
                                if (ctl is not IAudioSessionControl2 c2 || ctl is not ISimpleAudioVolume vol) continue;
                                if (c2.GetState(out int state) != 0 || state == AudioSessionStateExpired) continue;
                                bool system = c2.IsSystemSoundsSession() == 0;
                                c2.GetProcessId(out uint pid);
                                if (!system && pid == (uint)Environment.ProcessId) continue;
                                string idPath = "";
                                if (!system)
                                {
                                    // 여러 프로세스 세션(pid 0)도 세션 식별자의 실행 파일로 앱을 앎 (#21 — 크롬 소리가 빠지던 것)
                                    c2.GetSessionIdentifier(out IntPtr idPtr);
                                    try { idPath = ExeFromSessionId(idPtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(idPtr)); }
                                    finally { if (idPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(idPtr); }
                                    if (pid == 0 && idPath.Length == 0) continue;
                                }
                                vol.GetMasterVolume(out float level);
                                vol.GetMute(out bool mute);
                                var rs = new RenderSession(pid, system, state == AudioSessionStateActive, level, mute, idPath);
                                if (apply is not null && apply(rs, vol))
                                {
                                    vol.GetMasterVolume(out level);
                                    vol.GetMute(out mute);
                                    rs = rs with { Volume = level, Muted = mute };
                                }
                                list.Add(rs);
                            }
                            finally { Marshal.ReleaseComObject(ctl); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(sessions); }
                }
                finally { Marshal.ReleaseComObject(mgr); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"재생 장치 세션 조회 실패: {ex.Message}");
        }
        finally
        {
            if (enumObj != null) Marshal.ReleaseComObject(enumObj);
        }
        return list;
    }

    private const int AudioSessionStateExpired = 2;

    /// <summary>
    /// 점검용 (report-test --audiosessions, 읽기만): 모든 재생 장치(기본 장치 표시)의 모든 세션 — 상태·pid·GetProcessId 결과·표시 이름·아이콘 경로·볼륨 인터페이스 여부.
    /// </summary>
    internal static List<string> DescribeRenderForDiag()
    {
        var lines = new List<string>();
        object? enumObj = null;
        try
        {
            enumObj = new MMDeviceEnumeratorClass();
            var en = (IMMDeviceEnumerator)enumObj;
            string? Id(int role)
            {
                if (en.GetDefaultAudioEndpoint(CoreAudio.eRender, role, out var d) != 0 || d == null) return null;
                try { return d.GetId(out string? id) == 0 ? id : null; } finally { Marshal.ReleaseComObject(d); }
            }
            string? multimedia = Id(CoreAudio.eMultimedia), console = Id(0), comms = Id(2);
            if (en.EnumAudioEndpoints(CoreAudio.eRender, DEVICE_STATE_ACTIVE, out var devices) != 0 || devices == null) return lines;
            try
            {
                devices.GetCount(out uint count);
                var iid = typeof(IAudioSessionManager2).GUID;
                for (uint i = 0; i < count; i++)
                {
                    if (devices.Item(i, out var device) != 0 || device == null) continue;
                    try
                    {
                        device.GetId(out string? id);
                        lines.Add($"장치 {id}{(id == multimedia ? " [기본 멀티미디어]" : "")}{(id == console ? " [기본 콘솔]" : "")}{(id == comms ? " [기본 통신]" : "")}");
                        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr) { lines.Add("  세션 관리자 없음"); continue; }
                        try
                        {
                            if (mgr.GetSessionEnumerator(out var sessions) != 0 || sessions == null) continue;
                            try
                            {
                                sessions.GetCount(out int n);
                                for (int j = 0; j < n; j++)
                                {
                                    if (sessions.GetSession(j, out var ctl) != 0 || ctl == null) { lines.Add($"  세션 {j}: 못 받음"); continue; }
                                    try
                                    {
                                        if (ctl is not IAudioSessionControl2 c2) { lines.Add($"  세션 {j}: IAudioSessionControl2 아님"); continue; }
                                        c2.GetState(out int state);
                                        int hr = c2.GetProcessId(out uint pid);
                                        bool system = c2.IsSystemSoundsSession() == 0;
                                        string Str(Func<IntPtr> get) { var ptr = get(); try { return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(ptr) ?? ""; } finally { if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr); } }
                                        string dn = Str(() => { c2.GetDisplayName(out var x); return x; });
                                        string icon = Str(() => { c2.GetIconPath(out var x); return x; });
                                        string sid = Str(() => { c2.GetSessionIdentifier(out var x); return x; });
                                        var (path, _) = Kernel32.QueryProcess(pid);
                                        lines.Add($"  세션 {j}: 상태 {state} pid {pid} (hr 0x{hr:X8}) 시스템 {system} 볼륨IF {ctl is ISimpleAudioVolume} 경로 '{path}' 이름 '{dn}' 아이콘 '{icon}' id '{sid}'");
                                    }
                                    finally { Marshal.ReleaseComObject(ctl); }
                                }
                            }
                            finally { Marshal.ReleaseComObject(sessions); }
                        }
                        finally { Marshal.ReleaseComObject(mgr); }
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(devices); }
        }
        catch (Exception ex) { lines.Add($"실패: {ex.GetType().Name} {ex.Message}"); }
        finally { if (enumObj != null) Marshal.ReleaseComObject(enumObj); }
        return lines;
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

/// <summary>세션 볼륨 (앱별 볼륨 — #21).</summary>
[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
