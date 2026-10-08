using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Mongdock.Native;
using Windows.Devices.Radios;

namespace Mongdock.Services;

/// <summary>StatusService 확장: 와이파이 라디오, IP/링크 속도, 재생 장치 목록/기본 장치 변경. (블루투스 기기는 StatusService.Bluetooth.cs)</summary>
public sealed partial class StatusService
{
    public void OpenAvailableNetworks() => OpenUri("ms-availablenetworks:");

    // ───────────────────────── 와이파이 라디오 ─────────────────────────

    private Radio? _wifiRadio;
    private bool? _wifiRadioOn;

    public bool? WifiRadioOn { get { lock (_gate) return _wifiRadioOn; } }

    private void OnWifiRadioStateChanged(Radio sender, object args) => SafeRun(ReadWifiRadio, "와이파이 라디오");

    private void ReadWifiRadio()
    {
        var r = _wifiRadio;
        bool? on = r is null ? null : r.State == RadioState.On;
        Update(() => { if (_wifiRadioOn == on) return false; _wifiRadioOn = on; return true; });
    }

    /// <summary>와이파이 라디오 켜기/끄기. 접근 거부·어댑터 없음·실패 시 false.</summary>
    public async Task<bool> SetWifiAsync(bool on)
    {
        try
        {
            if (_wifiRadio is null) return false;
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Log.Warn($"와이파이 라디오 접근 거부: {access}");
                return false;
            }
            var result = await _wifiRadio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            ReadWifiRadio();
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Error("와이파이 라디오 설정 실패", ex);
            return false;
        }
    }

    // ───────────────────────── IP / 링크 속도 ─────────────────────────

    private string? _ip;
    private long _linkSpeed;

    public string? IpAddress { get { lock (_gate) return _ip; } }
    public long LinkSpeedBps { get { lock (_gate) return _linkSpeed; } }

    /// <summary>현재 연결 어댑터(게이트웨이 있는 물리 어댑터, 와이파이 연결이면 무선 우선)의 IPv4 와 링크 속도.</summary>
    private void UpdateIpInfo(WifiState state)
    {
        string? ip = null;
        long speed = 0;
        try
        {
            NetworkInterface? best = null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || !IsPhysical(ni)) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }
                if (props.GatewayAddresses.Count == 0) continue;
                bool wireless = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                if (best is null) best = ni;
                if (state == WifiState.Connected && wireless) { best = ni; break; }
                if (state == WifiState.Ethernet && !wireless) { best = ni; break; }
            }
            if (best is not null)
            {
                speed = Math.Max(0, best.Speed);
                ip = best.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"IP 정보 조회 실패: {ex.Message}");
        }
        Update(() =>
        {
            if (_ip == ip && _linkSpeed == speed) return false;
            _ip = ip;
            _linkSpeed = speed;
            return true;
        });
    }

    // ───────────────────────── 재생 장치 ─────────────────────────

    private IReadOnlyList<AudioDevice> _outputs = Array.Empty<AudioDevice>();
    private bool _deviceRefreshQueued;

    public IReadOnlyList<AudioDevice> OutputDevices { get { lock (_gate) return _outputs; } }

    /// <summary>오디오 알림 스레드에서 호출 → UI 스레드에서 한 번만 다시 읽음.</summary>
    internal void QueueDeviceListRefresh()
    {
        var d = _dispatcher;
        if (d is null) return;
        lock (_gate)
        {
            if (_deviceRefreshQueued) return;
            _deviceRefreshQueued = true;
        }
        d.InvokeAsync(async () =>
        {
            await Task.Delay(150); // 추가·기본 변경 알림이 연달아 오므로 모아서
            lock (_gate) _deviceRefreshQueued = false;
            if (!_started) return;
            RefreshOutputDevices();
            RefreshInputDevices();
        });
    }

    /// <summary>활성 재생 장치 목록 + 기본 장치 + 종류. UI 스레드 (열거자가 UI 스레드에서 생성됨).</summary>
    private void RefreshOutputDevices()
    {
        var list = ReadEndpoints(CoreAudio.eRender, CoreAudio.eMultimedia);
        if (list is null) return;
        Update(() =>
        {
            if (_outputs.SequenceEqual(list)) return false;
            _outputs = list;
            return true;
        });
    }

    /// <summary>
    /// 활성(DEVICE_STATE_ACTIVE) 엔드포인트 목록 + 기본 장치 표시 + 종류. 실패하면 null (이전 값 유지).
    /// UI 스레드에서 (열거자가 UI 스레드에서 생성됨).
    /// </summary>
    private List<AudioDevice>? ReadEndpoints(int flow, int defaultRole)
    {
        var en = _enumerator;
        if (en is null) return null;
        var list = new List<AudioDevice>();
        IMMDeviceCollection? col = null;
        try
        {
            string? defaultId = null;
            if (en.GetDefaultAudioEndpoint(flow, defaultRole, out IMMDevice? def) == 0 && def is not null)
            {
                def.GetId(out defaultId);
                Marshal.ReleaseComObject(def);
            }

            if (en.EnumAudioEndpoints(flow, CoreAudio.DEVICE_STATE_ACTIVE, out col) != 0 || col is null) return null;
            col.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (col.Item(i, out IMMDevice? dev) != 0 || dev is null) continue;
                IPropertyStore? store = null;
                try
                {
                    if (dev.GetId(out string? id) != 0 || string.IsNullOrEmpty(id)) continue;
                    string name = id;
                    var kind = AudioDeviceKind.Other;
                    if (dev.OpenPropertyStore(CoreAudio.STGM_READ, out store) == 0 && store is not null)
                    {
                        name = ReadString(store, CoreAudio.PKEY_Device_FriendlyName) ?? id;
                        int ff = ReadUInt(store, CoreAudio.PKEY_AudioEndpoint_FormFactor);
                        kind = flow == CoreAudio.eCapture ? InputKind(ff, name) : OutputKind(ff);
                    }
                    list.Add(new AudioDevice(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase), kind));
                }
                finally
                {
                    if (store is not null) Marshal.ReleaseComObject(store);
                    Marshal.ReleaseComObject(dev);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(flow == CoreAudio.eCapture ? "입력 장치 목록 조회 실패" : "재생 장치 목록 조회 실패", ex);
            return null;
        }
        finally
        {
            if (col is not null) Marshal.ReleaseComObject(col);
        }
        return list;
    }

    private static AudioDeviceKind OutputKind(int formFactor) => formFactor switch
    {
        CoreAudio.FF_Speakers => AudioDeviceKind.Speakers,
        CoreAudio.FF_Headphones or CoreAudio.FF_Headset => AudioDeviceKind.Headphones,
        CoreAudio.FF_DigitalAudioDisplayDevice => AudioDeviceKind.Display,
        CoreAudio.FF_SPDIF or CoreAudio.FF_UnknownDigitalPassthrough => AudioDeviceKind.Digital,
        _ => AudioDeviceKind.Other,
    };

    /// <summary>
    /// 재생 소리를 그대로 되받는 입력 ("스테레오 믹스" 등). 드라이버가 폼 팩터를 제각각(라인 입력·알 수 없음)으로 보고해
    /// 이름으로 가린다 (윈도우 표시 언어별 이름).
    /// </summary>
    private static readonly string[] LoopbackKeywords =
        { "stereo mix", "스테레오 믹스", "what u hear", "what you hear", "wave out mix", "loopback", "立体声混音", "ステレオ ミキサー", "stereomix" };

    private static AudioDeviceKind InputKind(int formFactor, string name)
    {
        string n = name.ToLowerInvariant();
        if (LoopbackKeywords.Any(n.Contains)) return AudioDeviceKind.Loopback;
        return formFactor switch
        {
            CoreAudio.FF_Headset or CoreAudio.FF_Headphones => AudioDeviceKind.Headphones,
            CoreAudio.FF_Microphone or CoreAudio.FF_Handset => AudioDeviceKind.Microphone,
            CoreAudio.FF_LineLevel => AudioDeviceKind.LineIn,
            CoreAudio.FF_SPDIF or CoreAudio.FF_UnknownDigitalPassthrough => AudioDeviceKind.Digital,
            _ => AudioDeviceKind.Microphone,
        };
    }

    private static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out PROPVARIANT pv) != 0) return null;
        try
        {
            return pv.vt == PROPVARIANT.VT_LPWSTR && pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) : null;
        }
        finally
        {
            Shell32.PropVariantClear(ref pv);
        }
    }

    private static int ReadUInt(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out PROPVARIANT pv) != 0) return -1;
        try
        {
            const ushort VT_UI4 = 19;
            return pv.vt == VT_UI4 ? unchecked((int)(pv.p.ToInt64() & 0xFFFFFFFF)) : -1;
        }
        finally
        {
            Shell32.PropVariantClear(ref pv);
        }
    }

    /// <summary>
    /// 기본 재생 장치 변경 — 비공개 IPolicyConfig.SetDefaultEndpoint 를 eConsole/eMultimedia/eCommunications 모두에.
    /// 공개 API 가 없어 Windows 업데이트로 깨질 수 있음. 실패 시 false.
    /// </summary>
    public bool SetDefaultOutput(string deviceId) => SetDefaultEndpoint(deviceId, "재생");

    /// <summary>기본 입력(녹음) 장치 변경 — 재생과 같은 IPolicyConfig 경로 (ID 가 캡처 엔드포인트라 캡처 기본값이 바뀜).</summary>
    public bool SetDefaultInput(string deviceId) => SetDefaultEndpoint(deviceId, "입력");

    private bool SetDefaultEndpoint(string deviceId, string what)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        object? obj = null;
        try
        {
            obj = new PolicyConfigClient();
            var pc = (IPolicyConfig)obj;
            bool ok = true;
            foreach (int role in new[] { CoreAudio.eConsole, CoreAudio.eMultimedia, CoreAudio.eCommunications })
            {
                int hr = pc.SetDefaultEndpoint(deviceId, role);
                if (hr != 0)
                {
                    ok = false;
                    Log.Warn($"SetDefaultEndpoint 실패 role={role} hr=0x{hr:X8}");
                }
            }
            Log.Info($"기본 {what} 장치 변경: {deviceId} ok={ok}");
            QueueDeviceListRefresh();
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error($"기본 {what} 장치 변경 실패", ex);
            return false;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }
}
