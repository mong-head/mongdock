using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Mongdock.Models;
using Mongdock.ViewModels;
using T = Mongdock.Native.TrayApi;

namespace Mongdock.Services;

public enum PrivacyCapability { Camera, Microphone, Location }

/// <summary>지금 카메라·마이크(·위치)를 쓰는 앱 하나.</summary>
/// <param name="Key">패키지 앱 = 패키지 패밀리명, 데스크톱 앱 = exe 전체 경로.</param>
/// <param name="Since">사용 시작 시각 (UTC, 모르면 MinValue).</param>
public sealed record PrivacyUsage(PrivacyCapability Capability, string Name, string Key, bool Packaged, DateTime Since);

/// <summary>
/// 맥 메뉴 막대의 초록/주황 점처럼 "지금 카메라·마이크를 쓰는 앱" 감지.
/// 윈도우가 기록하는 HKCU\...\CapabilityAccessManager\ConsentStore\{webcam,microphone,location}\ 아래 앱별 키의
/// LastUsedTimeStart / LastUsedTimeStop (FILETIME QWORD) 에서 Start &gt; 0 이고 Stop == 0 이면 사용 중.
/// 패키지 앱은 하위 키 이름 = 패키지 패밀리, 데스크톱 앱은 NonPackaged\ 아래 경로의 \ 를 # 로 바꾼 이름.
/// 읽기만 한다. RegNotifyChangeKeyValue(하위 트리, webcam·microphone) + 2초 폴링 보조, 백그라운드 스레드 하나.
/// 상단바가 여러 개(모니터별)여도 하나만 돌도록 공유 인스턴스 + 참조 수(<see cref="Acquire"/>/<see cref="Release"/>).
/// 사용이 끝난 항목은 <see cref="LingerMs"/> 동안 남겨 둔다 (<see cref="Linger"/>).
/// 디버그: 환경 변수 MONGDOCK_FAKE_PRIVACY=camera:Zoom,mic:Discord 면 레지스트리 대신 그 값 (",blink" 를 붙이면 6초 사용·1초 끊김 반복).
/// </summary>
public sealed class PrivacyUsageService
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string FakeVariable = "MONGDOCK_FAKE_PRIVACY";
    private const int PollMs = 2000;
    /// <summary>변경 알림이 둘 다 등록됐을 때의 보조 확인 주기.</summary>
    private const int SlowPollMs = 30000;
    /// <summary>실행 중 프로세스 이름 캐시 (회의 중 2초마다 전체 프로세스 목록을 만들지 않게).</summary>
    private const int RunningCacheMs = 10000;
    private const int DebounceMs = 150;
    /// <summary>사용이 끝난 뒤 점·카드 항목을 남겨 두는 시간 (잠깐 끊겼다 다시 쓰는 경우 깜빡임·카드 닫힘 방지).</summary>
    internal const int LingerMs = 3000;

    private static readonly (PrivacyCapability Cap, string Key)[] Capabilities =
    {
        (PrivacyCapability.Camera, "webcam"),
        (PrivacyCapability.Microphone, "microphone"),
        // 위치는 맥처럼 표시하지 않으므로 읽지 않음
    };

    public static PrivacyUsageService Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PrivacyUsage> _active = Array.Empty<PrivacyUsage>();
    private string _signature = "";
    private int _refs;
    /// <summary>Acquire 마다 +1 — Release 뒤 곧바로 Acquire 해도 옛 스레드의 늦은 결과가 섞이지 않게.</summary>
    private int _generation;
    private HashSet<string>? _running;
    private long _runningAt;
    private Thread? _thread;
    private ManualResetEvent? _stop;
    private System.Windows.Threading.Dispatcher? _dispatcher;

    private PrivacyUsageService() { }

    /// <summary>지금 사용 중인 항목 (카메라 → 마이크 → 위치, 이름 순).</summary>
    public IReadOnlyList<PrivacyUsage> Active
    {
        get { lock (_gate) return _active; }
    }

    public bool CameraInUse => Active.Any(u => u.Capability == PrivacyCapability.Camera);
    public bool MicrophoneInUse => Active.Any(u => u.Capability == PrivacyCapability.Microphone);

    /// <summary>사용 중 목록이 바뀜 (Acquire 를 처음 부른 스레드 = UI 스레드에서).</summary>
    public event EventHandler? Changed;

    /// <summary>감시 시작 (참조 수 +1). UI 스레드에서 부를 것 — Changed 를 그 스레드로 보냄.</summary>
    public void Acquire()
    {
        lock (_gate)
        {
            if (_refs++ > 0) return;
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _stop = new ManualResetEvent(false);
            var stop = _stop;
            int gen = ++_generation;
            _thread = new Thread(() => Run(stop, gen)) { IsBackground = true, Name = "mongdock privacy watch" };
            _thread.Start();
        }
    }

    /// <summary>감시 끝 (참조 수 -1, 0 이 되면 스레드 정지 + 목록 비움).</summary>
    public void Release()
    {
        bool changed;
        lock (_gate)
        {
            if (_refs == 0 || --_refs > 0) return;
            try { _stop?.Set(); } // 스레드가 스스로 이벤트·키를 정리 (예외로 이미 끝났으면 해제된 핸들)
            catch (ObjectDisposedException) { }
            _generation++;
            _stop = null;
            _thread = null;
            changed = _active.Count > 0;
            _active = Array.Empty<PrivacyUsage>();
            _signature = "";
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ms-settings: URI 를 여는 함수. 시험 하네스가 실제 설정 앱을 띄우지 않게 바꿔 끼울 수 있음 (기본 = 셸 실행).</summary>
    internal static Action<string> UriOpener = uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

    public static string SettingsUri(PrivacyCapability cap) => cap switch
    {
        PrivacyCapability.Camera => "ms-settings:privacy-webcam",
        PrivacyCapability.Microphone => "ms-settings:privacy-microphone",
        _ => "ms-settings:privacy-location",
    };

    public static void OpenSettings(PrivacyCapability cap)
    {
        string uri = SettingsUri(cap);
        // 성공도 남김: "눌러도 아무 일 없음" 보고 때 클릭이 여기까지 왔는지 로그로 구분하려고
        Log.Info($"개인 정보 설정 열기: {uri}");
        try { UriOpener(uri); }
        catch (Exception ex) { Log.Error($"설정 열기 실패: {uri}", ex); }
    }

    // ───────────────────────── 감시 스레드 ─────────────────────────

    private void Run(ManualResetEvent stop, int gen)
    {
        var fake = Environment.GetEnvironmentVariable(FakeVariable);
        if (!string.IsNullOrWhiteSpace(fake))
        {
            RunFake(fake, stop, gen);
            return;
        }

        var linger = new Linger();
        RegistryKey? cam = null, mic = null;
        AutoResetEvent? camEvt = null, micEvt = null;
        try
        {
            cam = Registry.CurrentUser.OpenSubKey(ConsentStore + @"\webcam", writable: false);
            mic = Registry.CurrentUser.OpenSubKey(ConsentStore + @"\microphone", writable: false);
            camEvt = new AutoResetEvent(false);
            micEvt = new AutoResetEvent(false);
            // 둘 다 등록되면 30초 보조 확인, 하나라도 실패하면 2초 폴링
            bool camOk = Register(cam, camEvt);
            bool micOk = Register(mic, micEvt);
            var handles = new WaitHandle[] { stop, camEvt, micEvt };

            Publish(linger.Merge(Scan(), Environment.TickCount64), gen);
            while (true)
            {
                int timeout = camOk && micOk ? SlowPollMs : PollMs;
                // 끝난 항목을 잠깐 남겨 둔 경우: 그 시간이 지나면 다시 읽어 지움
                long due = linger.MsUntilExpiry(Environment.TickCount64);
                if (due >= 0) timeout = (int)Math.Min(timeout, due + 20);
                int w = WaitHandle.WaitAny(handles, timeout);
                if (w == 0) return;
                if (w != WaitHandle.WaitTimeout)
                {
                    // 알림은 한 번 쓰면 끝 → 다시 등록. 바로 이어서 오는 쓰기(Start·Stop 등)를 묶어서 한 번만 읽음
                    if (w == 1) camOk = Register(cam, camEvt);
                    else micOk = Register(mic, micEvt);
                    if (stop.WaitOne(DebounceMs)) return;
                }
                else
                {
                    // 키가 없어 감시 못 했으면(처음 카메라를 쓰기 전) 생겼는지 다시 봄
                    if (cam == null && (cam = Registry.CurrentUser.OpenSubKey(ConsentStore + @"\webcam", false)) != null) camOk = Register(cam, camEvt);
                    if (mic == null && (mic = Registry.CurrentUser.OpenSubKey(ConsentStore + @"\microphone", false)) != null) micOk = Register(mic, micEvt);
                }
                Publish(linger.Merge(Scan(), Environment.TickCount64), gen);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"카메라·마이크 사용 감시 중단: {ex.Message}");
            Publish(new List<PrivacyUsage>(), gen); // 마지막 상태(초록 점)로 굳지 않게
        }
        finally
        {
            cam?.Dispose();
            mic?.Dispose();
            camEvt?.Dispose();
            micEvt?.Dispose();
            stop.Dispose();
        }
    }

    private static bool Register(RegistryKey? key, AutoResetEvent evt)
    {
        if (key == null) return false;
        int rc = T.RegNotifyChangeKeyValue(key.Handle, true,
            T.REG_NOTIFY_CHANGE_NAME | T.REG_NOTIFY_CHANGE_LAST_SET, evt.SafeWaitHandle, true);
        if (rc == 0) return true;
        Log.Warn($"카메라·마이크 사용 감시 등록 실패 (오류 {rc}) → 2초 주기 확인");
        return false;
    }

    private void Publish(List<PrivacyUsage> list, int gen)
    {
        list.Sort((a, b) =>
        {
            int c = a.Capability.CompareTo(b.Capability);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        string sig = string.Join("|", list.Select(u => $"{u.Capability}:{u.Key}:{u.Name}"));
        System.Windows.Threading.Dispatcher? dispatcher;
        lock (_gate)
        {
            if (_refs == 0 || gen != _generation || sig == _signature) return;
            int before = _active.Count;
            _signature = sig;
            _active = list;
            dispatcher = _dispatcher;
            if (before == 0 && list.Count > 0 || before > 0 && list.Count == 0)
                Log.Info($"카메라·마이크 사용 {(list.Count > 0 ? "시작" : "끝")}: 카메라 {list.Count(u => u.Capability == PrivacyCapability.Camera)}, 마이크 {list.Count(u => u.Capability == PrivacyCapability.Microphone)}");
        }
        dispatcher?.BeginInvoke(() =>
        {
            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("카메라·마이크 사용 알림 실패", ex); }
        });
    }

    /// <summary>레지스트리 한 번 읽기 (읽기 전용). 테스트용으로 공개.</summary>
    internal List<PrivacyUsage> Scan()
    {
        var result = new List<PrivacyUsage>();
        HashSet<string>? running = null;
        foreach (var (cap, name) in Capabilities)
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentStore + @"\" + name, false);
            if (root == null) continue;
            foreach (var (sub, packaged, since) in ActiveEntries(root))
            {
                string key = packaged ? sub : sub.Replace('#', '\\');
                if (!packaged)
                {
                    // 앱이 비정상 종료해 Stop 이 안 쓰인 기록 방지: 그 exe 이름의 프로세스가 없으면 무시
                    running ??= CachedRunningProcessNames();
                    if (running.Count > 0 && !running.Contains(Path.GetFileNameWithoutExtension(key))) continue;
                }
                result.Add(new PrivacyUsage(cap, ResolveName(key, packaged), key, packaged, since));
            }
        }
        return result;
    }

    /// <summary>(하위 키 이름, 패키지 앱인지, 시작 시각) — Start &gt; 0 이고 Stop == 0 인 것만.</summary>
    internal static IEnumerable<(string Sub, bool Packaged, DateTime Since)> ActiveEntries(RegistryKey root)
    {
        var found = new List<(string, bool, DateTime)>();
        foreach (var sub in SafeSubKeys(root))
        {
            if (string.Equals(sub, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                using var np = Open(root, sub);
                if (np == null) continue;
                foreach (var app in SafeSubKeys(np))
                    if (InUse(np, app, out var t)) found.Add((app, false, t));
            }
            else if (InUse(root, sub, out var t)) found.Add((sub, true, t));
        }
        return found;
    }

    private static bool InUse(RegistryKey parent, string sub, out DateTime since)
    {
        since = DateTime.MinValue;
        using var k = Open(parent, sub);
        if (k == null) return false;
        long start = ReadQword(k, "LastUsedTimeStart");
        if (start <= 0) return false;
        if (k.GetValue("LastUsedTimeStop") is not { } stopValue || ToLong(stopValue) != 0) return false;
        try { since = DateTime.FromFileTimeUtc(start); }
        catch (ArgumentOutOfRangeException) { }
        return true;
    }

    private static long ReadQword(RegistryKey k, string name) => k.GetValue(name) is { } v ? ToLong(v) : 0;

    private static long ToLong(object v) => v switch
    {
        long l => l,
        int i => i,
        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
        _ => -1,
    };

    private static RegistryKey? Open(RegistryKey parent, string sub)
    {
        try { return parent.OpenSubKey(sub, false); }
        catch { return null; } // 권한·삭제 경합
    }

    private static string[] SafeSubKeys(RegistryKey k)
    {
        try { return k.GetSubKeyNames(); }
        catch { return Array.Empty<string>(); }
    }

    private HashSet<string> CachedRunningProcessNames()
    {
        long now = Environment.TickCount64;
        if (_running == null || now - _runningAt > RunningCacheMs)
        {
            _running = RunningProcessNames();
            _runningAt = now;
        }
        return _running;
    }

    private static HashSet<string> RunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { set.Add(p.ProcessName); }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Warn($"프로세스 목록 조회 실패: {ex.Message}"); }
        return set;
    }

    // ───────────────────────── 앱 이름 ─────────────────────────

    /// <summary>패키지: 시작 메뉴(AppsFolder) 표시 이름, 데스크톱: AppNames (시작 메뉴 이름 → 파일 설명 → 파일명). 캐시.</summary>
    private string ResolveName(string key, bool packaged)
    {
        lock (_gate)
            if (_names.TryGetValue(key, out var cached)) return cached;
        string name;
        bool final = true;
        try
        {
            if (packaged)
            {
                var aumid = AppsFolder.FindAumidByFamily(key);
                name = (aumid != null ? AppsFolder.GetAppDisplayName(aumid) : null) ?? FamilyFallback(key);
            }
            else
            {
                name = AppNames.Get(new AppWindowInfo(IntPtr.Zero, "", key, null, false));
                // AppNames 가 아직 시작 메뉴 매핑 전이면 임시 이름 — 캐시하지 않고 다음에 다시
                final = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"카메라·마이크 사용 앱 이름 조회 실패: {ex.Message}");
            name = packaged ? FamilyFallback(key) : Path.GetFileNameWithoutExtension(key);
        }
        if (string.IsNullOrWhiteSpace(name)) name = packaged ? FamilyFallback(key) : Path.GetFileName(key);
        if (final)
            lock (_gate)
            {
                if (_names.Count > 256) _names.Clear();
                _names[key] = name;
            }
        return name;
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera".</summary>
    private static string FamilyFallback(string family)
    {
        string s = family;
        int u = s.LastIndexOf('_');
        if (u > 0) s = s[..u];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return s;
    }

    // ───────────────────────── 끝난 사용 잠깐 유지 ─────────────────────────

    /// <summary>
    /// 사용이 끝난 항목을 <see cref="LingerMs"/> 동안 목록에 남겨 둠 (감시 스레드 전용, 세대마다 새로).
    /// 마이크는 앱이 스트림을 닫았다 곧 다시 여는 일이 잦아(장치 전환·음성 감지·통화 재연결 등) 그대로 보내면 점이 깜빡이고,
    /// 점이 사라지는 순간 상단바가 카드를 닫아 "마이크 개인 정보 설정…" 을 누르려던 클릭이 허공에 떨어진다.
    /// 맥도 사용이 끝난 뒤 점을 몇 초 남긴다.
    /// </summary>
    internal sealed class Linger
    {
        private readonly Dictionary<string, (PrivacyUsage Usage, long GoneAt)> _gone = new();
        private List<PrivacyUsage> _last = new();

        private static string KeyOf(PrivacyUsage u) => $"{u.Capability}:{u.Key}";

        /// <summary>이번에 읽은 목록 + 끝난 지 LingerMs 안 된 항목. <paramref name="now"/> = TickCount64.</summary>
        public List<PrivacyUsage> Merge(List<PrivacyUsage> current, long now)
        {
            var keys = new HashSet<string>(current.Select(KeyOf));
            foreach (var u in _last)
            {
                string k = KeyOf(u);
                if (!keys.Contains(k) && !_gone.ContainsKey(k)) _gone[k] = (u, now);
            }
            foreach (var k in keys) _gone.Remove(k);
            foreach (var k in _gone.Where(p => now - p.Value.GoneAt >= LingerMs).Select(p => p.Key).ToList()) _gone.Remove(k);
            var result = new List<PrivacyUsage>(current);
            result.AddRange(_gone.Values.Select(v => v.Usage));
            _last = result;
            return result;
        }

        /// <summary>남겨 둔 항목 중 가장 먼저 지울 때까지 남은 ms (없으면 -1).</summary>
        public long MsUntilExpiry(long now)
            => _gone.Count == 0 ? -1 : Math.Max(0, _gone.Values.Min(v => v.GoneAt) + LingerMs - now);
    }

    // ───────────────────────── 디버그 ─────────────────────────

    /// <summary>
    /// MONGDOCK_FAKE_PRIVACY: 고정 목록. 값에 "blink" 가 있으면 6초 사용 → 1초 끊김을 되풀이 (마이크가 잠깐 끊기는 상황 재현,
    /// 실제 감시와 같은 <see cref="Linger"/> 를 거치므로 점·카드가 그대로 남아야 정상).
    /// </summary>
    private void RunFake(string spec, ManualResetEvent stop, int gen)
    {
        try
        {
            var list = ParseFake(spec);
            bool blink = spec.Split(',', ';').Any(p => p.Trim().Equals("blink", StringComparison.OrdinalIgnoreCase));
            if (!blink)
            {
                Publish(list, gen);
                stop.WaitOne();
                return;
            }
            var linger = new Linger();
            bool on = true;
            long switchAt = Environment.TickCount64 + 6000;
            while (true)
            {
                long now = Environment.TickCount64;
                if (now >= switchAt)
                {
                    on = !on;
                    switchAt = now + (on ? 6000 : 1000);
                    Log.Info($"가짜 카메라·마이크 사용 {(on ? "다시 시작" : "잠깐 끊김")} (blink)");
                }
                Publish(linger.Merge(on ? new List<PrivacyUsage>(list) : new List<PrivacyUsage>(), now), gen);
                long wait = switchAt - now;
                long due = linger.MsUntilExpiry(now);
                if (due >= 0) wait = Math.Min(wait, due + 20);
                if (stop.WaitOne((int)Math.Max(10, wait))) return;
            }
        }
        finally { stop.Dispose(); }
    }

    /// <summary>"camera:Zoom,mic:Discord,location:지도" → 가짜 사용 목록.</summary>
    internal static List<PrivacyUsage> ParseFake(string spec)
    {
        var list = new List<PrivacyUsage>();
        foreach (var raw in spec.Split(',', ';'))
        {
            var part = raw.Trim();
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string kind = part[..colon].Trim().ToLowerInvariant();
            string name = part[(colon + 1)..].Trim();
            if (name.Length == 0) continue;
            PrivacyCapability? cap = kind switch
            {
                "camera" or "cam" or "webcam" => PrivacyCapability.Camera,
                "mic" or "microphone" => PrivacyCapability.Microphone,
                "location" or "loc" => PrivacyCapability.Location,
                _ => null,
            };
            if (cap is { } c) list.Add(new PrivacyUsage(c, name, "fake:" + name, false, DateTime.UtcNow));
        }
        return list;
    }
}
