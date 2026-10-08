using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Mongdock.Services;

/// <summary>
/// StatusService 확장: 페어링된 블루투스 기기 목록(클래식 + LE, DeviceWatcher/AssociationEndpoint)과
/// 오디오 기기 연결/해제(<see cref="BluetoothAudioControl"/>).
/// </summary>
public sealed partial class StatusService
{
    private const string IsConnectedProp = "System.Devices.Aep.IsConnected";
    private const string ContainerIdProp = "System.Devices.Aep.ContainerId";
    private const string CategoryProp = "System.Devices.Aep.Category";
    private const string CodMajorProp = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string CodMinorProp = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string ProtocolIdProp = "System.Devices.Aep.ProtocolId";
    private static readonly Guid BluetoothLeProtocol = new("bb7bb05e-5972-42b5-94fc-76eaa7084d49");

    /// <summary>KS 속성 요청 자체의 제한 시간 (실제 연결 완료는 UI 가 DeviceWatcher 로 따로 기다림).</summary>
    private static readonly TimeSpan BtRequestTimeout = TimeSpan.FromSeconds(8);

    private sealed record BtEntry(string Name, bool Connected, Guid Container, BluetoothDeviceKind Kind, bool IsLe);

    private DeviceWatcher? _btWatcher;
    private readonly Dictionary<string, BtEntry> _btDevices = new();
    private IReadOnlyList<BluetoothDeviceInfo> _btList = Array.Empty<BluetoothDeviceInfo>();
    /// <summary>블루투스 오디오 KS 필터가 있는 컨테이너 (백그라운드 스캔 결과).</summary>
    private HashSet<Guid> _btAudioContainers = new();
    private HashSet<Guid> _btScannedFor = new();
    private bool _btScanRunning;

    public IReadOnlyList<BluetoothDeviceInfo> BluetoothDevices { get { lock (_gate) return _btList; } }

    /// <summary>페어링된 블루투스 기기 감시 (클래식 + LE, AssociationEndpoint).</summary>
    private void StartBluetoothWatcher()
    {
        if (_btWatcher is not null) return;
        try
        {
            string selector = "(" + BluetoothDevice.GetDeviceSelectorFromPairingState(true) + ") OR ("
                            + BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) + ")";
            var props = new[] { IsConnectedProp, ContainerIdProp, CategoryProp, CodMajorProp, CodMinorProp, ProtocolIdProp };
            var w = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
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

    private static Guid ReadGuidProp(IReadOnlyDictionary<string, object> props, string key) =>
        props.TryGetValue(key, out object? v) && v is Guid g ? g : Guid.Empty;

    private static int ReadIntProp(IReadOnlyDictionary<string, object> props, string key)
    {
        try
        {
            return props.TryGetValue(key, out object? v) && v is IConvertible c ? c.ToInt32(null) : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 기기 종류: Aep Category(예: Audio.Earbuds, Communication.Headset.Bluetooth, Input.Mouse, Input.Gaming) 우선,
    /// 없으면 클래식 블루투스 Class of Device (Major 4 오디오 / 5 주변기기 / 2 전화 / 1 컴퓨터).
    /// </summary>
    private static BluetoothDeviceKind ReadKind(IReadOnlyDictionary<string, object> props)
    {
        if (props.TryGetValue(CategoryProp, out object? v) && v is string[] cats)
        {
            foreach (var c in cats)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (c.StartsWith("Audio.Speaker", StringComparison.OrdinalIgnoreCase)) return BluetoothDeviceKind.Speaker;
                if (c.StartsWith("Audio.", StringComparison.OrdinalIgnoreCase)
                    || c.StartsWith("Communication.Headset", StringComparison.OrdinalIgnoreCase)) return BluetoothDeviceKind.Headphones;
                if (c.StartsWith("Input.Mouse", StringComparison.OrdinalIgnoreCase)
                    || c.StartsWith("Input.Touchpad", StringComparison.OrdinalIgnoreCase)) return BluetoothDeviceKind.Mouse;
                if (c.StartsWith("Input.Keyboard", StringComparison.OrdinalIgnoreCase)) return BluetoothDeviceKind.Keyboard;
                if (c.StartsWith("Input.Gaming", StringComparison.OrdinalIgnoreCase)) return BluetoothDeviceKind.Gamepad;
                if (c.StartsWith("Communication.Phone", StringComparison.OrdinalIgnoreCase)) return BluetoothDeviceKind.Phone;
                if (c.StartsWith("Computer", StringComparison.OrdinalIgnoreCase)) return BluetoothDeviceKind.Computer;
            }
        }
        int major = ReadIntProp(props, CodMajorProp);
        int minor = ReadIntProp(props, CodMinorProp);
        switch (major)
        {
            case 1: return BluetoothDeviceKind.Computer;
            case 2: return BluetoothDeviceKind.Phone;
            case 4: return minor is 5 or 7 ? BluetoothDeviceKind.Speaker : BluetoothDeviceKind.Headphones; // 5 확성기, 7 휴대용 오디오
            case 5:
                if (minor >= 0 && (minor & 0x20) != 0) return BluetoothDeviceKind.Mouse;     // 포인팅
                if (minor >= 0 && (minor & 0x10) != 0) return BluetoothDeviceKind.Keyboard;
                if (minor >= 0 && (minor & 0x0F) is 1 or 2) return BluetoothDeviceKind.Gamepad; // 조이스틱/게임패드
                break;
        }
        return BluetoothDeviceKind.Other;
    }

    private void OnBtAdded(DeviceWatcher sender, DeviceInformation info)
    {
        if (sender != _btWatcher) return;
        try
        {
            var p = info.Properties;
            var entry = new BtEntry(
                string.IsNullOrWhiteSpace(info.Name) ? info.Id : info.Name.Trim(),
                ReadConnected(p),
                ReadGuidProp(p, ContainerIdProp),
                ReadKind(p),
                ReadGuidProp(p, ProtocolIdProp) == BluetoothLeProtocol);
            lock (_gate) _btDevices[info.Id] = entry;
            PublishBt();
        }
        catch (Exception ex)
        {
            Log.Warn($"블루투스 기기 추가 처리 실패: {ex.Message}");
        }
    }

    private void OnBtUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (sender != _btWatcher) return;
        try
        {
            lock (_gate)
            {
                if (!_btDevices.TryGetValue(update.Id, out var cur)) return;
                if (update.Properties.ContainsKey(IsConnectedProp)) cur = cur with { Connected = ReadConnected(update.Properties) };
                _btDevices[update.Id] = cur;
            }
            PublishBt();
        }
        catch (Exception ex)
        {
            Log.Warn($"블루투스 기기 갱신 처리 실패: {ex.Message}");
        }
    }

    private void OnBtRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (sender != _btWatcher) return;
        lock (_gate) _btDevices.Remove(update.Id);
        PublishBt();
    }

    private static bool IsAudioKind(BluetoothDeviceKind k) => k is BluetoothDeviceKind.Headphones or BluetoothDeviceKind.Speaker;

    /// <summary>
    /// 같은 이름의 항목(클래식 + LE 로 두 번 잡히는 이어폰 등)은 하나로 합침: 대표 = 클래식 항목.
    /// </summary>
    private void PublishBt()
    {
        bool needScan = false;
        Update(() =>
        {
            var list = new List<BluetoothDeviceInfo>();
            foreach (var group in _btDevices.GroupBy(kv => kv.Value.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var rep = group.OrderBy(kv => kv.Value.IsLe).First();
                // 클래식 항목이 있으면 그 연결 상태만 (이어폰의 LE 보조 링크가 붙어 있어도 오디오는 끊긴 상태일 수 있음)
                bool hasClassic = group.Any(kv => !kv.Value.IsLe);
                bool connected = group.Any(kv => kv.Value.Connected && (!hasClassic || !kv.Value.IsLe));
                var kind = rep.Value.Kind != BluetoothDeviceKind.Other
                    ? rep.Value.Kind
                    : group.Select(kv => kv.Value.Kind).FirstOrDefault(k => k != BluetoothDeviceKind.Other);
                bool canConnect = group.Any(kv => _btAudioContainers.Contains(kv.Value.Container)) || IsAudioKind(kind);
                list.Add(new BluetoothDeviceInfo(rep.Key, rep.Value.Name, connected, kind, canConnect));
            }
            list = list.OrderByDescending(d => d.Connected)
                       .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();

            var containers = _btDevices.Values.Select(e => e.Container).Where(g => g != Guid.Empty).ToHashSet();
            if (!containers.SetEquals(_btScannedFor) && !_btScanRunning)
            {
                _btScannedFor = containers;
                _btScanRunning = true;
                needScan = true;
            }

            if (_btList.SequenceEqual(list)) return false;
            _btList = list;
            return true;
        });
        if (needScan) _ = ScanAudioContainersAsync();
    }

    /// <summary>블루투스 오디오 KS 필터가 있는 컨테이너를 백그라운드에서 찾아 CanConnect 에 반영.</summary>
    private async Task ScanAudioContainersAsync()
    {
        HashSet<Guid>? found = null;
        try
        {
            await Task.Delay(500).ConfigureAwait(false); // Added 가 연달아 오므로 모아서
            var filters = await Task.Run(() => BluetoothAudioControl.Enumerate(m => Log.Warn(m)))
                                    .WaitAsync(BtRequestTimeout).ConfigureAwait(false);
            found = filters.Select(f => f.ContainerId).Where(g => g != Guid.Empty).ToHashSet();
            Log.Info($"블루투스 오디오 필터 {filters.Count}개, 기기 {found.Count}개");
        }
        catch (Exception ex)
        {
            Log.Warn($"블루투스 오디오 필터 조회 실패: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _btScanRunning = false;
                if (found is not null) _btAudioContainers = found;
            }
        }
        PublishBt();
    }

    public async Task<BluetoothConnectResult> SetBluetoothDeviceConnectedAsync(string id, bool connect)
    {
        string name;
        List<Guid> containers;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(id) || !_btDevices.TryGetValue(id, out var entry)) return BluetoothConnectResult.NotSupported;
            name = entry.Name;
            containers = _btDevices.Values
                .Where(e => string.Equals(e.Name, name, StringComparison.CurrentCultureIgnoreCase))
                .Select(e => e.Container)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();
            // 오디오 필터가 있는 컨테이너(이어폰의 클래식 오디오 쪽)에만 보냄. 하나도 없을 때만(아직 조사 전 등) 전부 시도.
            var audio = containers.Where(_btAudioContainers.Contains).ToList();
            if (audio.Count > 0) containers = audio;
        }
        if (containers.Count == 0)
        {
            Log.Warn($"블루투스 {(connect ? "연결" : "해제")}: '{name}' 컨테이너 ID 없음");
            return BluetoothConnectResult.NotSupported;
        }

        try
        {
            var outcomes = await Task.Run(() => containers
                    .Select(c => BluetoothAudioControl.Send(c, connect, m => Log.Warn(m)))
                    .ToList())
                .WaitAsync(BtRequestTimeout).ConfigureAwait(false);
            // 요청당 한 줄 요약 (컨테이너별 결과는 결과 종류만 묶어서)
            string summary = string.Join(", ", outcomes.Select(o => o.Detail is { Length: > 0 } d ? $"{o.Outcome}({d})" : o.Outcome.ToString()));
            Log.Info($"블루투스 {(connect ? "연결" : "해제")} 요청 '{name}' 컨테이너 {containers.Count}개: {summary}");
            if (outcomes.Any(o => o.Outcome == BluetoothAudioControl.Outcome.Sent)) return BluetoothConnectResult.Requested;
            if (outcomes.Any(o => o.Outcome == BluetoothAudioControl.Outcome.Failed)) return BluetoothConnectResult.Failed;
            return BluetoothConnectResult.NotSupported;
        }
        catch (TimeoutException)
        {
            Log.Warn($"블루투스 {(connect ? "연결" : "해제")} 요청 시간 초과 '{name}'");
            return BluetoothConnectResult.Failed;
        }
        catch (Exception ex)
        {
            Log.Error($"블루투스 {(connect ? "연결" : "해제")} 요청 실패 '{name}'", ex);
            return BluetoothConnectResult.Failed;
        }
    }
}
