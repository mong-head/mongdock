using System.Reflection;
using System.Runtime.InteropServices;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>밝기를 조절할 수 있는 화면 하나. Id 는 <see cref="BrightnessService.Set"/> 에 넘기는 값.</summary>
public sealed record DisplayBrightness(string Id, string Name, int Percent, bool Internal);

/// <summary>
/// 화면 밝기 (제어 센터 "디스플레이").
///  - 노트북 내장 화면: WMI root\WMI 의 WmiMonitorBrightness(읽기) / WmiMonitorBrightnessMethods.WmiSetBrightness(쓰기).
///    System.Management(NuGet) 없이 COM "WbemScripting.SWbemLocator" 를 늦은 바인딩(IDispatch)으로 부른다.
///  - 외장 모니터: DDC/CI (dxva2 GetMonitorBrightness/SetMonitorBrightness). 지원하는 모니터만 목록에 들어감.
/// 모든 조회·쓰기는 백그라운드 스레드에서 (DDC 는 호출 하나에 수십~수백 ms). UI 스레드에서 불러도 막지 않음.
/// <see cref="Changed"/> 는 백그라운드 스레드에서 발생 — 받는 쪽이 UI 스레드로 넘길 것.
/// 쓰기는 화면마다 "마지막 값만" 남기고 합쳐서 보낸다 (드래그 중 쌓이지 않게).
/// 디버그: 환경 변수 MONGDOCK_FAKE_BRIGHTNESS="내장:70,DELL U2720Q:40" 이면 실제 조회 대신 그 목록을 쓰고,
/// 바꾼 값은 메모리에만 저장 (WMI·DDC 쓰기 안 함, 로그만). 없으면 무시 (Release 에서도 무해).
/// </summary>
public sealed class BrightnessService
{
    public static BrightnessService Instance { get; } = new();

    private const string WmiId = "wmi";
    private const string DdcPrefix = "ddc:";
    private const string FakePrefix = "fake:";
    private const string FakeVariable = "MONGDOCK_FAKE_BRIGHTNESS";

    private readonly object _gate = new();
    private IReadOnlyList<DisplayBrightness> _displays = Array.Empty<DisplayBrightness>();
    private bool _refreshing;
    private bool _refreshAgain;
    private readonly Dictionary<string, int> _pending = new();
    private bool _writing;
    private bool _loggedWmi;
    private readonly HashSet<string> _loggedDdc = new();
    /// <summary>MONGDOCK_FAKE_BRIGHTNESS 목록 (없으면 null). 값은 Set 으로 바뀜 (_gate 로 보호).</summary>
    private readonly List<DisplayBrightness>? _fake;

    private BrightnessService()
    {
        var spec = Environment.GetEnvironmentVariable(FakeVariable);
        if (string.IsNullOrWhiteSpace(spec)) return;
        _fake = ParseFake(spec);
        Log.Info($"가짜 밝기 사용 ({FakeVariable}): {string.Join(", ", _fake.Select(d => $"{d.Name} {d.Percent}%"))}");
    }

    /// <summary>마지막으로 확인한 조절 가능 화면 (없으면 빈 목록 = 행 숨김). 내장 화면 먼저.</summary>
    public IReadOnlyList<DisplayBrightness> Displays { get { lock (_gate) return _displays; } }

    /// <summary>한 번이라도 조회를 마쳤는지 (마치기 전엔 UI 가 이전 값/빈 목록을 보임).</summary>
    public bool Loaded { get; private set; }

    /// <summary>목록·값이 바뀜 (백그라운드 스레드).</summary>
    public event EventHandler? Changed;

    /// <summary>백그라운드에서 다시 조회 (이미 조회 중이면 끝난 뒤 한 번 더).</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_refreshing)
            {
                _refreshAgain = true;
                return;
            }
            _refreshing = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                List<DisplayBrightness> list;
                try { list = Enumerate(); }
                catch (Exception ex)
                {
                    Log.Error("밝기 조회 실패", ex);
                    list = new();
                }
                bool changed;
                lock (_gate)
                {
                    // 조회 중에 사용자가 바꾼 값(아직 쓰기 대기)은 그 값을 유지
                    for (int i = 0; i < list.Count; i++)
                        if (_pending.TryGetValue(list[i].Id, out int p)) list[i] = list[i] with { Percent = p };
                    changed = !list.SequenceEqual(_displays);
                    _displays = list;
                    Loaded = true;
                    if (!_refreshAgain)
                    {
                        _refreshing = false;
                        if (changed) RaiseChanged();
                        return;
                    }
                    _refreshAgain = false;
                }
                if (changed) RaiseChanged();
            }
        });
    }

    /// <summary>밝기 설정 (0~100). 바로 반환하고 백그라운드에서 씀. 같은 화면의 이전 대기 값은 버림.</summary>
    public void Set(string id, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        lock (_gate)
        {
            _pending[id] = percent;
            _displays = _displays.Select(d => d.Id == id ? d with { Percent = percent } : d).ToList();
            if (_writing) return;
            _writing = true;
        }
        ThreadPool.QueueUserWorkItem(_ => WriteLoop());
    }

    private void WriteLoop()
    {
        while (true)
        {
            string id;
            int value;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _writing = false;
                    return;
                }
                var first = _pending.First();
                id = first.Key;
                value = first.Value;
                _pending.Remove(id);
            }
            try
            {
                if (_fake is not null)
                {
                    FakeSet(id, value);
                    continue;
                }
                bool ok = id == WmiId ? WmiSet(value) : DdcSet(id, value);
                if (!ok) Log.Warn($"밝기 설정 실패: {id} → {value}");
            }
            catch (Exception ex) { Log.Error($"밝기 설정 실패: {id}", ex); }
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("BrightnessService.Changed 핸들러 예외", ex); }
    }

    // ───────────────────────── 조회 ─────────────────────────

    private List<DisplayBrightness> Enumerate()
    {
        if (_fake is not null)
            lock (_gate) return new List<DisplayBrightness>(_fake);
        var list = new List<DisplayBrightness>();
        List<(string GdiName, string FriendlyName, bool Internal)> targets;
        try { targets = DisplayApi.GetActiveTargets(); }
        catch (Exception ex)
        {
            Log.Warn($"디스플레이 구성 조회 실패: {ex.Message}");
            targets = new();
        }

        int? wmi = WmiGet();
        if (wmi is int w) list.Add(new DisplayBrightness(WmiId, "내장 디스플레이", w, true));

        foreach (var m in Monitors.GetAll())
        {
            var matches = targets.Where(t => string.Equals(t.GdiName, m.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList();
            // 내장 패널은 DDC 를 지원하지 않고 WMI 로 이미 다룸
            if (wmi is not null && matches.Count > 0 && matches.All(t => t.Internal)) continue;
            IntPtr h = MonitorHandle(m.DeviceName);
            if (h == IntPtr.Zero) continue;
            var ddc = DdcGetAll(h, m.DeviceName);
            for (int i = 0; i < ddc.Count; i++)
            {
                string friendly = i < matches.Count && !string.IsNullOrWhiteSpace(matches[i].FriendlyName)
                    ? matches[i].FriendlyName
                    : !string.IsNullOrWhiteSpace(ddc[i].Description) && !ddc[i].Description.StartsWith("Generic", StringComparison.OrdinalIgnoreCase)
                        ? ddc[i].Description
                        : $"디스플레이 {m.Number}";
                list.Add(new DisplayBrightness($"{DdcPrefix}{m.DeviceName}#{i}", friendly, ddc[i].Percent, false));
            }
        }
        return list;
    }

    private static IntPtr MonitorHandle(string deviceName)
    {
        foreach (IntPtr h in DesktopApi.EnumMonitorHandles())
            if (DesktopApi.TryGetMonitorInfoEx(h, out var mi) && string.Equals(mi.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
                return h;
        return IntPtr.Zero;
    }

    // ───────────────────────── 디버그 (가짜 밝기) ─────────────────────────

    /// <summary>가짜 화면 값만 바꿈 (실제 밝기는 그대로).</summary>
    private void FakeSet(string id, int percent)
    {
        lock (_gate)
        {
            int i = _fake!.FindIndex(d => d.Id == id);
            if (i < 0) return;
            _fake[i] = _fake[i] with { Percent = percent };
        }
        Log.Info($"가짜 밝기 설정 (실제 화면은 그대로): {id} → {percent}%");
    }

    /// <summary>
    /// "내장:70,DELL U2720Q:40" → 가짜 목록. 이름에 내장/internal/built-in 이 있으면 내장 화면("내장" 만이면 "내장 디스플레이"),
    /// 실제 조회처럼 내장 먼저. 퍼센트를 생략하거나 못 읽으면 50.
    /// </summary>
    internal static List<DisplayBrightness> ParseFake(string spec)
    {
        var list = new List<DisplayBrightness>();
        foreach (var raw in spec.Split(',', ';'))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            int colon = part.LastIndexOf(':');
            string name = colon > 0 ? part[..colon].Trim() : part;
            int percent = 50;
            if (colon > 0 && int.TryParse(part[(colon + 1)..].Trim().TrimEnd('%'), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int p))
                percent = Math.Clamp(p, 0, 100);
            if (name.Length == 0) continue;
            bool isInternal = name.Contains("내장", StringComparison.Ordinal)
                || name.Contains("internal", StringComparison.OrdinalIgnoreCase)
                || name.Contains("built-in", StringComparison.OrdinalIgnoreCase);
            if (name == "내장") name = "내장 디스플레이";
            list.Add(new DisplayBrightness($"{FakePrefix}{list.Count}", name, percent, isInternal));
        }
        return list.Where(d => d.Internal).Concat(list.Where(d => !d.Internal)).ToList();
    }

    // ───────────────────────── DDC/CI ─────────────────────────

    /// <summary>HMONITOR 의 물리 모니터 각각의 (설명, 밝기%). 밝기를 못 읽는 모니터는 빠짐. 핸들은 모두 해제.</summary>
    private List<(string Description, int Percent)> DdcGetAll(IntPtr hMonitor, string deviceName)
    {
        var result = new List<(string, int)>();
        WithPhysicalMonitors(hMonitor, arr =>
        {
            for (int i = 0; i < arr.Length; i++)
            {
                if (DisplayApi.GetMonitorBrightness(arr[i].hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                {
                    int pct = (int)Math.Round(100.0 * (Math.Clamp(cur, min, max) - min) / (max - min));
                    result.Add((arr[i].szPhysicalMonitorDescription ?? "", pct));
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    string key = $"{deviceName}#{i}";
                    lock (_loggedDdc)
                        if (_loggedDdc.Add(key)) Log.Info($"DDC/CI 밝기 미지원: {key} {arr[i].szPhysicalMonitorDescription} (0x{err:X})");
                }
            }
        });
        return result;
    }

    private static bool DdcSet(string id, int percent)
    {
        // id = "ddc:\\.\DISPLAY1#0"
        int hash = id.LastIndexOf('#');
        if (!id.StartsWith(DdcPrefix, StringComparison.Ordinal) || hash < 0 || !int.TryParse(id.AsSpan(hash + 1), out int index)) return false;
        string device = id[DdcPrefix.Length..hash];
        IntPtr h = MonitorHandle(device);
        if (h == IntPtr.Zero) return false;
        bool ok = false;
        WithPhysicalMonitors(h, arr =>
        {
            if (index >= arr.Length) return;
            var pm = arr[index].hPhysicalMonitor;
            if (!DisplayApi.GetMonitorBrightness(pm, out uint min, out _, out uint max) || max <= min) return;
            uint value = (uint)Math.Round(min + (max - min) * percent / 100.0);
            ok = DisplayApi.SetMonitorBrightness(pm, value);
        });
        return ok;
    }

    private static void WithPhysicalMonitors(IntPtr hMonitor, Action<DisplayApi.PHYSICAL_MONITOR[]> use)
    {
        if (!DisplayApi.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint n) || n == 0 || n > 16) return;
        var arr = new DisplayApi.PHYSICAL_MONITOR[n];
        if (!DisplayApi.GetPhysicalMonitorsFromHMONITOR(hMonitor, n, arr)) return;
        try { use(arr); }
        finally { DisplayApi.DestroyPhysicalMonitors(n, arr); }
    }

    // ───────────────────────── WMI (내장 화면) ─────────────────────────

    /// <summary>내장 화면 밝기 (지원 안 하면 null). 활성 인스턴스 첫 번째.</summary>
    private int? WmiGet()
    {
        object? services = null, set = null, item = null;
        try
        {
            services = WmiConnect();
            if (services is null) return null;
            set = Invoke(services, "ExecQuery", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            if (set is null || Convert.ToInt32(Get(set, "Count")) <= 0) return null;
            item = Invoke(set, "ItemIndex", 0);
            object? v = item is null ? null : Get(item, "CurrentBrightness");
            return v is null ? null : Math.Clamp(Convert.ToInt32(v), 0, 100);
        }
        catch (Exception ex)
        {
            // 데스크톱 모니터: WBEM_E_NOT_SUPPORTED (0x8004100C) — 정상
            if (!_loggedWmi)
            {
                _loggedWmi = true;
                Log.Info($"WMI 밝기 미지원: {(ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message)}");
            }
            return null;
        }
        finally
        {
            Release(item);
            Release(set);
            Release(services);
        }
    }

    private static bool WmiSet(int percent)
    {
        object? services = null, set = null, item = null;
        try
        {
            services = WmiConnect();
            if (services is null) return false;
            set = Invoke(services, "ExecQuery", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            if (set is null || Convert.ToInt32(Get(set, "Count")) <= 0) return false;
            item = Invoke(set, "ItemIndex", 0);
            if (item is null) return false;
            // WmiSetBrightness(uint32 Timeout, uint8 Brightness) — SWbemObject 가 WMI 메서드를 디스패치 메서드로 노출
            Invoke(item, "WmiSetBrightness", 1, (byte)percent);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"WMI 밝기 설정 실패: {(ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message)}");
            return false;
        }
        finally
        {
            Release(item);
            Release(set);
            Release(services);
        }
    }

    private static object? WmiConnect()
    {
        var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: false);
        if (t is null) return null;
        object? locator = Activator.CreateInstance(t);
        if (locator is null) return null;
        try { return Invoke(locator, "ConnectServer", ".", @"root\WMI"); }
        finally { Release(locator); }
    }

    private static object? Invoke(object target, string name, params object[] args)
        => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    private static object? Get(object target, string name)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o))
        {
            try { Marshal.ReleaseComObject(o); } catch { }
        }
    }
}
