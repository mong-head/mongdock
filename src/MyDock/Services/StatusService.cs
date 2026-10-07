using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using MyDock.Native;
using Windows.Devices.Radios;

namespace MyDock.Services;

/// <summary>
/// 상단바 상태: 볼륨(Core Audio COM), 와이파이(wlanapi + NetworkInterface), 블루투스(WinRT Radio), 네트워크 속도.
/// Start()/Stop()/Set* 는 UI 스레드에서 호출. Changed 는 값이 실제로 바뀔 때만 UI 스레드에서 발생.
/// 오디오 COM 호출은 모두 UI 스레드에서 하고, COM 콜백 스레드에서는 값만 받아 UI 스레드로 넘긴다.
/// 와이파이/속도는 스레드풀 타이머(1초: 속도, 5초: 와이파이 + NetworkChange 이벤트 즉시).
/// </summary>
public sealed partial class StatusService : IStatusService, IDisposable
{
    private readonly object _gate = new();
    private Dispatcher? _dispatcher;
    private bool _started;

    // 값
    private WifiState _wifi = WifiState.Unknown;
    private int _wifiSignal;
    private string? _wifiName;
    private bool? _bluetooth;
    private double _volume;
    private bool _muted;
    private long _up, _down;

    public WifiState Wifi { get { lock (_gate) return _wifi; } }
    public int WifiSignal { get { lock (_gate) return _wifiSignal; } }
    public string? WifiName { get { lock (_gate) return _wifiName; } }
    public bool? BluetoothOn { get { lock (_gate) return _bluetooth; } }
    public double Volume { get { lock (_gate) return _volume; } }
    public bool Muted { get { lock (_gate) return _muted; } }
    public long UploadBytesPerSec { get { lock (_gate) return _up; } }
    public long DownloadBytesPerSec { get { lock (_gate) return _down; } }

    public event EventHandler? Changed;

    private bool _pollSpeed = true;
    private bool _pollWifiBt = true;
    private bool _netEventsHooked;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _speedTimer = new Timer(_ => SafeRun(PollSpeed, "속도"), null, Timeout.Infinite, Timeout.Infinite);
        _wifiTimer = new Timer(_ => SafeRun(PollWifi, "와이파이"), null, Timeout.Infinite, Timeout.Infinite);

        StartAudio();
        ApplyPolling();
        Log.Info("StatusService 시작");
    }

    /// <summary>표시 안 하는 항목의 폴링을 끔. 기본은 둘 다 켜짐. Start 전에 불러도 됨.</summary>
    public void SetPolling(bool networkSpeed, bool wifiAndBluetooth)
    {
        if (_pollSpeed == networkSpeed && _pollWifiBt == wifiAndBluetooth) return;
        _pollSpeed = networkSpeed;
        _pollWifiBt = wifiAndBluetooth;
        if (_started) ApplyPolling();
    }

    private void ApplyPolling()
    {
        // 네트워크 속도: 1초 차분. 다시 켤 때 이전 누적값과 섞이지 않게 기준 초기화.
        if (_pollSpeed)
        {
            _lastTick = 0;
            _speedTimer?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
        else
        {
            _speedTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            Update(() => { bool c = _up != 0 || _down != 0; _up = _down = 0; return c; });
        }

        // 와이파이(5초 + NetworkChange) / 블루투스(StateChanged 구독)
        if (_pollWifiBt)
        {
            if (!_netEventsHooked)
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
                _netEventsHooked = true;
            }
            _wifiTimer?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));
            if (_radio is null && _wifiRadio is null) _ = StartRadiosAsync();
            StartBluetoothWatcher();
        }
        else
        {
            UnhookWifiBt();
        }
    }

    private void UnhookWifiBt()
    {
        _wifiTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        if (_netEventsHooked)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _netEventsHooked = false;
        }
        if (_radio is not null)
        {
            try { _radio.StateChanged -= OnRadioStateChanged; } catch { }
            _radio = null;
        }
        if (_wifiRadio is not null)
        {
            try { _wifiRadio.StateChanged -= OnWifiRadioStateChanged; } catch { }
            _wifiRadio = null;
        }
        StopBluetoothWatcher();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        UnhookWifiBt();
        _speedTimer?.Dispose();
        _wifiTimer?.Dispose();
        _speedTimer = _wifiTimer = null;
        StopAudio();
    }

    public void Dispose() => Stop();

    private static void SafeRun(Action a, string what)
    {
        try { a(); }
        catch (Exception ex) { Log.Error($"상태 갱신 실패({what})", ex); }
    }

    /// <summary>값을 바꾸고, 바뀌었으면 UI 스레드에서 Changed.</summary>
    private void Update(Func<bool> apply)
    {
        bool changed;
        lock (_gate) changed = apply();
        if (!changed) return;
        var d = _dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) RaiseChanged();
        else d.InvokeAsync(RaiseChanged);
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("StatusService.Changed 핸들러 예외", ex); }
    }

    // ───────────────────────── 설정 열기 ─────────────────────────

    public void OpenWifiSettings() => OpenUri("ms-settings:network-wifi");
    public void OpenBluetoothSettings() => OpenUri("ms-settings:bluetooth");
    public void OpenSoundSettings() => OpenUri("ms-settings:sound");

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error($"설정 열기 실패: {uri}", ex); }
    }

    // ───────────────────────── 볼륨 (Core Audio) ─────────────────────────

    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private VolumeCallback? _volumeCallback;
    private DeviceCallback? _deviceCallback;
    private bool _reconnectQueued;

    private void StartAudio()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
            _deviceCallback = new DeviceCallback(this);
            int hr = _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
            if (hr != 0) Log.Warn($"오디오 장치 변경 알림 등록 실패 hr=0x{hr:X8}");
            ConnectDefaultDevice();
            RefreshOutputDevices();
        }
        catch (Exception ex)
        {
            Log.Error("오디오 초기화 실패", ex);
        }
    }

    private void ConnectDefaultDevice()
    {
        DisconnectEndpoint();
        if (_enumerator is null) return;
        IMMDevice? device = null;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(CoreAudio.eRender, CoreAudio.eMultimedia, out device) != 0 || device is null)
            {
                Log.Warn("기본 출력 장치 없음");
                return;
            }
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out object? o) != 0 || o is null) return;
            _endpoint = (IAudioEndpointVolume)o;
            _volumeCallback = new VolumeCallback(this);
            int hr = _endpoint.RegisterControlChangeNotify(_volumeCallback);
            if (hr != 0) Log.Warn($"볼륨 변경 알림 등록 실패 hr=0x{hr:X8}");
            ReadVolume();
        }
        catch (Exception ex)
        {
            Log.Error("기본 출력 장치 연결 실패", ex);
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
        }
    }

    private void DisconnectEndpoint()
    {
        if (_endpoint is null) return;
        try { if (_volumeCallback is not null) _endpoint.UnregisterControlChangeNotify(_volumeCallback); } catch { }
        try { Marshal.ReleaseComObject(_endpoint); } catch { }
        _endpoint = null;
        _volumeCallback = null;
    }

    private void StopAudio()
    {
        DisconnectEndpoint();
        if (_enumerator is not null)
        {
            try { if (_deviceCallback is not null) _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback); } catch { }
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
        }
    }

    private void ReadVolume()
    {
        var ep = _endpoint;
        if (ep is null) return;
        if (ep.GetMasterVolumeLevelScalar(out float level) != 0) return;
        ep.GetMute(out bool mute);
        SetVolumeValues(level, mute);
    }

    private void SetVolumeValues(double level, bool mute) => Update(() =>
    {
        level = Math.Round(Math.Clamp(level, 0, 1), 3);
        if (Math.Abs(_volume - level) < 0.0005 && _muted == mute) return false;
        _volume = level;
        _muted = mute;
        return true;
    });

    public void SetVolume(double volume)
    {
        try
        {
            var g = Guid.Empty;
            _endpoint?.SetMasterVolumeLevelScalar((float)Math.Clamp(volume, 0, 1), ref g);
            ReadVolume();
        }
        catch (Exception ex) { Log.Error("볼륨 설정 실패", ex); }
    }

    public void SetMuted(bool muted)
    {
        try
        {
            var g = Guid.Empty;
            _endpoint?.SetMute(muted, ref g);
            ReadVolume();
        }
        catch (Exception ex) { Log.Error("음소거 설정 실패", ex); }
    }

    private void QueueReconnect()
    {
        if (_reconnectQueued || _dispatcher is null) return;
        _reconnectQueued = true;
        _dispatcher.InvokeAsync(() =>
        {
            _reconnectQueued = false;
            if (_started) ConnectDefaultDevice();
        });
    }

    /// <summary>볼륨 변경 알림 (오디오 스레드) — AUDIO_VOLUME_NOTIFICATION_DATA: Guid(16), BOOL bMuted(4), float fMasterVolume(4).</summary>
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly StatusService _owner;
        public VolumeCallback(StatusService owner) => _owner = owner;
        public int OnNotify(IntPtr data)
        {
            try
            {
                if (data == IntPtr.Zero) return 0;
                bool muted = Marshal.ReadInt32(data, 16) != 0;
                float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
                _owner.SetVolumeValues(level, muted);
            }
            catch { }
            return 0;
        }
    }

    private sealed class DeviceCallback : IMMNotificationClient
    {
        private readonly StatusService _owner;
        public DeviceCallback(StatusService owner) => _owner = owner;
        public int OnDeviceStateChanged(string deviceId, int newState) { _owner.QueueDeviceListRefresh(); return 0; }
        public int OnDeviceAdded(string deviceId) { _owner.QueueDeviceListRefresh(); return 0; }
        public int OnDeviceRemoved(string deviceId) { _owner.QueueDeviceListRefresh(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow == CoreAudio.eRender && role == CoreAudio.eMultimedia)
            {
                _owner.QueueReconnect();
                _owner.QueueDeviceListRefresh();
            }
            return 0;
        }
        public int OnPropertyValueChanged(string deviceId, PROPERTYKEY key) => 0;
    }

    // ───────────────────────── 블루투스 (WinRT) ─────────────────────────

    private Radio? _radio;

    /// <summary>블루투스·와이파이 라디오 조회 (UI 스레드에서 시작 → await 후에도 UI 스레드).</summary>
    private async Task StartRadiosAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            if (!_started || !_pollWifiBt || _radio is not null || _wifiRadio is not null) return; // 그 사이 꺼졌거나 이미 연결됨
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            _wifiRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (_radio is not null) _radio.StateChanged += OnRadioStateChanged;
            if (_wifiRadio is not null) _wifiRadio.StateChanged += OnWifiRadioStateChanged;
            ReadRadio();
            ReadWifiRadio();
        }
        catch (Exception ex)
        {
            Log.Error("라디오 조회 실패", ex);
        }
    }

    private void OnRadioStateChanged(Radio sender, object args) => SafeRun(ReadRadio, "블루투스");

    private void ReadRadio()
    {
        var r = _radio;
        bool? on = r is null ? null : r.State == RadioState.On;
        Update(() => { if (_bluetooth == on) return false; _bluetooth = on; return true; });
    }

    /// <summary>블루투스 라디오 켜기/끄기. 접근 거부·어댑터 없음·실패 시 false.</summary>
    public async Task<bool> SetBluetoothAsync(bool on)
    {
        try
        {
            if (_radio is null) return false;
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Log.Warn($"블루투스 라디오 접근 거부: {access}");
                return false;
            }
            var result = await _radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            ReadRadio();
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Error("블루투스 설정 실패", ex);
            return false;
        }
    }

    // ───────────────────────── 와이파이 ─────────────────────────

    private Timer? _wifiTimer;
    private Timer? _speedTimer;

    private void OnNetworkChanged(object? sender, EventArgs e) => ThreadPool.QueueUserWorkItem(_ => SafeRun(PollWifi, "와이파이"));

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        ThreadPool.QueueUserWorkItem(_ => SafeRun(PollWifi, "와이파이"));

    private int _wifiPolling;

    private void PollWifi()
    {
        if (Interlocked.Exchange(ref _wifiPolling, 1) == 1) return;
        try
        {
            var (connected, ssid, signal) = QueryWlan();
            WifiState state;
            if (connected) state = WifiState.Connected;
            else if (HasWiredConnection()) state = WifiState.Ethernet;
            else state = WifiState.Disconnected;

            Update(() =>
            {
                int sig = connected ? signal : 0;
                string? name = connected ? ssid : null;
                if (_wifi == state && _wifiSignal == sig && _wifiName == name) return false;
                _wifi = state;
                _wifiSignal = sig;
                _wifiName = name;
                return true;
            });
            UpdateIpInfo(state);
        }
        finally
        {
            Interlocked.Exchange(ref _wifiPolling, 0);
        }
    }

    /// <summary>
    /// 연결된 무선 인터페이스의 SSID·신호(0~100). Windows 11 24H2+ 는 SSID 조회에 위치 권한이 필요해
    /// current_connection 조회가 거부될 수 있다 → 그때는 인터페이스 상태(connected)만으로 연결 판정, SSID null.
    /// </summary>
    private static (bool Connected, string? Ssid, int Signal) QueryWlan()
    {
        IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (Wlan.WlanOpenHandle(Wlan.WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out h) != Wlan.ERROR_SUCCESS) return (false, null, 0);
            if (Wlan.WlanEnumInterfaces(h, IntPtr.Zero, out list) != Wlan.ERROR_SUCCESS) return (false, null, 0);
            int count = Marshal.ReadInt32(list, 0);
            for (int i = 0; i < count; i++)
            {
                IntPtr info = list + 8 + i * Wlan.InterfaceInfoSize;
                var guid = Marshal.PtrToStructure<Guid>(info);
                int state = Marshal.ReadInt32(info, 16 + 512);
                if (state != Wlan.wlan_interface_state_connected) continue;

                if (Wlan.WlanQueryInterface(h, ref guid, Wlan.wlan_intf_opcode_current_connection, IntPtr.Zero,
                        out _, out IntPtr data, IntPtr.Zero) != Wlan.ERROR_SUCCESS || data == IntPtr.Zero)
                    return (true, null, 0);
                try
                {
                    int len = Math.Clamp(Marshal.ReadInt32(data, Wlan.Conn_SsidLength), 0, 32);
                    var bytes = new byte[len];
                    Marshal.Copy(data + Wlan.Conn_Ssid, bytes, 0, len);
                    string? ssid = len > 0 ? Encoding.UTF8.GetString(bytes) : null;
                    int signal = Math.Clamp(Marshal.ReadInt32(data, Wlan.Conn_SignalQuality), 0, 100);
                    return (true, ssid, signal);
                }
                finally
                {
                    Wlan.WlanFreeMemory(data);
                }
            }
            return (false, null, 0);
        }
        catch (DllNotFoundException)
        {
            return (false, null, 0); // WLAN 서비스 없는 PC
        }
        finally
        {
            if (list != IntPtr.Zero) Wlan.WlanFreeMemory(list);
            if (h != IntPtr.Zero) Wlan.WlanCloseHandle(h, IntPtr.Zero);
        }
    }

    private static readonly string[] VirtualKeywords =
        { "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "tunnel", "loopback", "bluetooth", "wsl", "pseudo", "npcap", "wan miniport", "tailscale", "zerotier", "wireguard" };

    private static bool IsPhysical(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Unknown) return false;
        string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
        return !VirtualKeywords.Any(d.Contains);
    }

    private static bool HasWiredConnection()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !IsPhysical(ni)) continue;
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit))
                continue;
            try
            {
                if (ni.GetIPProperties().GatewayAddresses.Count > 0) return true;
            }
            catch { }
        }
        return false;
    }

    // ───────────────────────── 네트워크 속도 ─────────────────────────

    private readonly Dictionary<string, (long Sent, long Recv)> _lastBytes = new();
    private long _lastTick;

    private int _speedPolling;

    private void PollSpeed()
    {
        if (Interlocked.Exchange(ref _speedPolling, 1) == 1) return;
        try { PollSpeedCore(); }
        finally { Interlocked.Exchange(ref _speedPolling, 0); }
    }

    private void PollSpeedCore()
    {
        long now = Stopwatch.GetTimestamp();
        var current = new Dictionary<string, (long, long)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !IsPhysical(ni)) continue;
            try
            {
                var st = ni.GetIPStatistics(); // IPv4 + IPv6
                current[ni.Id] = (st.BytesSent, st.BytesReceived);
            }
            catch { }
        }

        long up = 0, down = 0;
        double secs = _lastTick == 0 ? 0 : (now - _lastTick) / (double)Stopwatch.Frequency;
        if (secs > 0.2)
        {
            foreach (var (id, (sent, recv)) in current)
            {
                if (!_lastBytes.TryGetValue(id, out var prev)) continue;
                up += Math.Max(0, sent - prev.Sent);
                down += Math.Max(0, recv - prev.Recv);
            }
            up = (long)(up / secs);
            down = (long)(down / secs);
        }
        _lastBytes.Clear();
        foreach (var kv in current) _lastBytes[kv.Key] = kv.Value;
        _lastTick = now;
        if (secs <= 0.2) return;

        Update(() =>
        {
            if (_up == up && _down == down) return false;
            _up = up;
            _down = down;
            return true;
        });
    }
}
