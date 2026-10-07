using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MyDock.Native;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace MyDock.Services;

/// <summary>StatusService 확장: 와이파이 라디오, IP/링크 속도, 재생 장치 목록/기본 장치 변경, 페어링된 블루투스 장치.</summary>
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
            if (_started) RefreshOutputDevices();
        });
    }

    /// <summary>활성 재생 장치 목록 + 기본 장치 + 종류. UI 스레드 (열거자가 UI 스레드에서 생성됨).</summary>
    private void RefreshOutputDevices()
    {
        var en = _enumerator;
        if (en is null) return;
        var list = new List<AudioDevice>();
        IMMDeviceCollection? col = null;
        try
        {
            string? defaultId = null;
            if (en.GetDefaultAudioEndpoint(CoreAudio.eRender, CoreAudio.eMultimedia, out IMMDevice? def) == 0 && def is not null)
            {
                def.GetId(out defaultId);
                Marshal.ReleaseComObject(def);
            }

            if (en.EnumAudioEndpoints(CoreAudio.eRender, CoreAudio.DEVICE_STATE_ACTIVE, out col) != 0 || col is null) return;
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
                        kind = ReadUInt(store, CoreAudio.PKEY_AudioEndpoint_FormFactor) switch
                        {
                            CoreAudio.FF_Speakers => AudioDeviceKind.Speakers,
                            CoreAudio.FF_Headphones or CoreAudio.FF_Headset => AudioDeviceKind.Headphones,
                            CoreAudio.FF_DigitalAudioDisplayDevice => AudioDeviceKind.Display,
                            CoreAudio.FF_SPDIF or CoreAudio.FF_UnknownDigitalPassthrough => AudioDeviceKind.Digital,
                            _ => AudioDeviceKind.Other,
                        };
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
            Log.Error("재생 장치 목록 조회 실패", ex);
            return;
        }
        finally
        {
            if (col is not null) Marshal.ReleaseComObject(col);
        }

        Update(() =>
        {
            if (_outputs.SequenceEqual(list)) return false;
            _outputs = list;
            return true;
        });
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
    public bool SetDefaultOutput(string deviceId)
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
            Log.Info($"기본 재생 장치 변경: {deviceId} ok={ok}");
            QueueDeviceListRefresh();
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("기본 재생 장치 변경 실패", ex);
            return false;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    // ───────────────────────── 블루투스 장치 ─────────────────────────

    private const string IsConnectedProp = "System.Devices.Aep.IsConnected";
    private DeviceWatcher? _btWatcher;
    private readonly Dictionary<string, (string Name, bool Connected)> _btDevices = new();
    private IReadOnlyList<BluetoothDeviceInfo> _btList = Array.Empty<BluetoothDeviceInfo>();

    public IReadOnlyList<BluetoothDeviceInfo> BluetoothDevices { get { lock (_gate) return _btList; } }

    /// <summary>페어링된 블루투스 장치 감시 (DeviceWatcher, AssociationEndpoint + IsConnected 속성).</summary>
    private void StartBluetoothWatcher()
    {
        if (_btWatcher is not null) return;
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var w = DeviceInformation.CreateWatcher(selector, new[] { IsConnectedProp }, DeviceInformationKind.AssociationEndpoint);
            w.Added += OnBtAdded;
            w.Updated += OnBtUpdated;
            w.Removed += OnBtRemoved;
            w.Stopped += OnBtStopped;
            _btWatcher = w;
            w.Start();
        }
        catch (Exception ex)
        {
            Log.Error("블루투스 장치 감시 시작 실패", ex);
            _btWatcher = null;
        }
    }

    private void StopBluetoothWatcher()
    {
        var w = _btWatcher;
        _btWatcher = null;
        if (w is null) return;
        lock (_gate) _btDevices.Clear(); // 다시 시작하면 Added 로 새로 채움
        try
        {
            w.Added -= OnBtAdded;
            w.Updated -= OnBtUpdated;
            w.Removed -= OnBtRemoved;
            w.Stopped -= OnBtStopped;
            if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"블루투스 장치 감시 정지 실패: {ex.Message}");
        }
    }

    /// <summary>감시자가 스스로 멈춤(Aborted 포함 — Stopped 이벤트로 옴) → 폴링이 켜져 있으면 2초 뒤 재시작.</summary>
    private void OnBtStopped(DeviceWatcher sender, object args)
    {
        if (sender != _btWatcher) return; // 우리가 Stop 한 경우는 이미 _btWatcher 가 바뀌어 있음
        Log.Warn($"블루투스 장치 감시가 멈춤 (status={sender.Status}) → 재시작 예약");
        _dispatcher?.InvokeAsync(async () =>
        {
            await Task.Delay(2000);
            if (!_started || !_pollWifiBt || _btWatcher != sender) return;
            StopBluetoothWatcher();
            StartBluetoothWatcher();
        });
    }

    private static bool ReadConnected(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue(IsConnectedProp, out object? v) && v is bool b && b;

    private void OnBtAdded(DeviceWatcher sender, DeviceInformation info)
    {
        if (sender != _btWatcher) return;
        lock (_gate) _btDevices[info.Id] = (string.IsNullOrWhiteSpace(info.Name) ? info.Id : info.Name, ReadConnected(info.Properties));
        PublishBt();
    }

    private void OnBtUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (sender != _btWatcher) return;
        lock (_gate)
        {
            if (!_btDevices.TryGetValue(update.Id, out var cur)) return;
            if (update.Properties.ContainsKey(IsConnectedProp)) cur.Connected = ReadConnected(update.Properties);
            _btDevices[update.Id] = cur;
        }
        PublishBt();
    }

    private void OnBtRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (sender != _btWatcher) return;
        lock (_gate) _btDevices.Remove(update.Id);
        PublishBt();
    }

    private void PublishBt() => Update(() =>
    {
        var list = _btDevices.Select(kv => new BluetoothDeviceInfo(kv.Key, kv.Value.Name, kv.Value.Connected))
                             .OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                             .ToList();
        if (_btList.SequenceEqual(list)) return false;
        _btList = list;
        return true;
    });
}
