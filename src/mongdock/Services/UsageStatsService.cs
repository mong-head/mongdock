using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Win32;
using Mongdock.Models;
using Mongdock.ViewModels;

namespace Mongdock.Services;

/// <summary>
/// 사용 통계 (#20). PC 번호·이름·경로·창 제목 없이, 몽독이 켜진 날 하루 한 번 작은 신호를 보낸다.
/// - 그날 첫 실행 2분 뒤(또는 켜 둔 채 자정을 넘기면 다음 확인 때) 그날 것 하나를 만들어 %APPDATA%\mongdock\stats.json 에 모아 두고 보냄.
/// - 못 보내면(오프라인·받는 쪽 오류) 날짜를 붙여 두었다가 다음 확인(1시간마다) 때 한꺼번에. 최근 7일 안의 것만, 7개까지 (오래된 것부터 버림).
/// - 하루치마다 무작위 nonce(그 하루치를 다시 보낼 때 같은 값 — 받는 쪽이 응답 시간 초과 뒤 재전송을 한 번만 적게. 날짜·PC 를 잇지 않음).
/// - 오류 수 = 지난 신호 이후 로그의 ERROR 줄 수 (로그 파일 시각으로 셈 — 다시 시작·크래시에도 이어짐).
/// - 동의(SendUsageStats): null(아직 안 물음) = 모아만 두고 보내지 않음, true = 보냄, false = 만들지 않고 모아 둔 것도 지움.
/// 받는 쪽: 저장소 tools/support-intake/Code.gs 의 {type:"stats"} → 구글 시트 한 줄씩.
/// 시험: 환경 변수 MONGDOCK_STATS=log 면 보내지 않고 로그에 내용만 (MONGDOCK_DATA_DIR 로 띄운 시험 실행은 log 를 주지 않으면 아예 안 함).
/// </summary>
public sealed class UsageStatsService : IDisposable
{
    private const int MaxPendingDays = 7;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly Regex ErrorLine = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ERROR ", RegexOptions.Compiled);

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly string _path = Path.Combine(AppInfo.DataDirectory, "stats.json");
    private readonly bool _logOnly;
    private bool _sending;

    /// <summary>지금 도는 인스턴스 (동의 직후 바로 한 번 확인하려고). 시험 폴더라 안 돌면 null.</summary>
    public static UsageStatsService? Instance { get; private set; }

    /// <summary>동의가 바뀜 → 곧바로 한 번 확인 (보내기 또는 지우기). UI 스레드.</summary>
    public void Poke() => Tick();

    private UsageStatsService(AppServices services)
    {
        _services = services;
        _logOnly = string.Equals(Environment.GetEnvironmentVariable("MONGDOCK_STATS"), "log", StringComparison.OrdinalIgnoreCase);
        _timer = new DispatcherTimer { Interval = FirstDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = Interval;
            Tick();
        };
    }

    /// <summary>시작 2분 뒤 첫 확인, 그 뒤 1시간마다. 시험용 데이터 폴더로 띄웠으면 MONGDOCK_STATS=log 일 때만.</summary>
    public static UsageStatsService? Start(AppServices services)
    {
        var svc = new UsageStatsService(services);
        if (AppInfo.UsesCustomDataDirectory && !svc._logOnly)
        {
            Log.Info("사용 통계: 시험용 데이터 폴더라 보내지 않음 (MONGDOCK_STATS=log 면 로그로만)");
            return null;
        }
        svc._timer.Start();
        Instance = svc;
        return svc;
    }

    public void Dispose() => _timer.Stop();

    private void Tick()
    {
        try
        {
            var state = Load();
            bool? consent = _services.Settings.Current.SendUsageStats;
            if (consent == false)
            {
                if (state.Pending.Count > 0) { state.Pending.Clear(); Save(state); Log.Info("사용 통계 안 보냄: 모아 둔 것 지움"); }
                return;
            }
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (state.LastDay != today)
            {
                var now = DateTime.Now;
                state.Pending.Add(Snapshot(today, CountErrorsSince(state.LastAt ?? now.AddDays(-1))));
                state.LastDay = today;
                state.LastAt = now;
                Save(state);
            }
            if (DropStale(state)) Save(state);
            if (consent == true && state.Pending.Count > 0 && !_sending) _ = SendAsync(); // 묻기 전(null)엔 모아만 둠
        }
        catch (Exception ex)
        {
            Log.Warn($"사용 통계 확인 실패: {ex.GetType().Name} {ex.Message}");
        }
    }

    // ───────────────────────── 보낼 내용 ─────────────────────────

    /// <summary>하루치 신호 하나. 여기 있는 것 말고는 아무것도 보내지 않는다 (README·개인정보처리방침과 같게 유지).</summary>
    private JsonObject Snapshot(string day, int errors)
    {
        var s = _services.Settings.Current;
        var (win, build) = WindowsVersion();
        int monitors = 1, scale = 100;
        try
        {
            monitors = Monitors.GetAll().Count;
            scale = (int)Math.Round(Monitors.GetPrimary().Scale * 100);
        }
        catch { /* 모르면 기본값 */ }
        bool calendar;
        try { calendar = _services.Calendars.Feeds.Count > 0; }
        catch { calendar = false; }

        return new JsonObject
        {
            ["date"] = day,
            ["appVersion"] = ReportService.AppVersion(),
            ["install"] = AppInfo.IsPackaged ? "store" : AppInfo.InstallKind == "설치 프로그램" ? "installer" : "zip",
            ["windows"] = win,
            ["build"] = build,
            ["lang"] = Loc.Code,
            ["laptop"] = DeviceInfo.HasBattery,
            ["monitors"] = monitors,
            ["scale"] = scale,
            ["dockAutoHide"] = s.Dock.Enabled && s.Dock.Mode == DockMode.AutoHide,
            ["topBar"] = s.TopBar.Enabled,
            ["notifications"] = !s.Notifications.ShowNotificationBanners ? "windows"
                : s.Notifications.HideWindowsToastPopups ? "mongdock" : "both",
            ["calendar"] = calendar,
            ["searchButton"] = s.TopBar.Enabled && s.TopBar.ShowQuickButtons,
            ["hideTaskbar"] = s.HideWindowsTaskbar,
            ["lightMode"] = PerfMode.IsOn(s),
            ["folders"] = s.Pins.Count(p => p.Kind == PinKind.Folder),
            ["errors"] = errors,
            ["nonce"] = Guid.NewGuid().ToString("N"),
        };
    }

    /// <summary>최근 7일 밖의 것과 7개 넘는 앞쪽을 버림 (한 달 꺼져 있던 PC 가 옛날 것을 보내지 않게). 바뀌었으면 true.</summary>
    private static bool DropStale(State state)
    {
        string oldest = DateTime.Now.AddDays(-(MaxPendingDays - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int before = state.Pending.Count;
        state.Pending.RemoveAll(d => string.CompareOrdinal(d["date"]?.GetValue<string>() ?? "", oldest) < 0);
        if (state.Pending.Count > MaxPendingDays) state.Pending.RemoveRange(0, state.Pending.Count - MaxPendingDays);
        bool changed = state.Pending.Count != before;
        foreach (var d in state.Pending.Where(d => d["nonce"] is null)) // nonce 전에 모아 둔 것
        {
            d["nonce"] = Guid.NewGuid().ToString("N");
            changed = true;
        }
        return changed;
    }

    /// <summary>("10" 또는 "11", "26200.6584"). ProductName 은 11 에서도 "Windows 10" 이라 빌드 번호로 판단.</summary>
    private static (string Windows, string Build) WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
            string ubr = key?.GetValue("UBR") is int u ? "." + u.ToString(CultureInfo.InvariantCulture) : "";
            return (int.TryParse(build, out int b) && b >= 22000 ? "11" : "10", build + ubr);
        }
        catch
        {
            int b = Environment.OSVersion.Version.Build;
            return (b >= 22000 ? "11" : "10", b.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>since 이후 로그(mongdock.log, .log.1)의 ERROR 줄 수. 줄 내용은 읽기만 하고 보내지 않는다.</summary>
    private static int CountErrorsSince(DateTime since)
    {
        int count = 0;
        foreach (string path in new[] { Log.LogPath + ".1", Log.LogPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var m = ErrorLine.Match(line);
                    if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at > since)
                        count++;
                }
            }
            catch (IOException) { }
        }
        return count;
    }

    // ───────────────────────── 보내기 ─────────────────────────

    private async Task SendAsync()
    {
        _sending = true;
        try
        {
            var state = Load();
            if (state.Pending.Count == 0) return;
            var days = new JsonArray(state.Pending.Select(d => (JsonNode?)d.DeepClone()).ToArray());
            int count = state.Pending.Count;
            var sent = state.Pending.Select(d => d["nonce"]?.GetValue<string>()).OfType<string>().ToHashSet();
            if (_logOnly)
            {
                Log.Info($"사용 통계 (MONGDOCK_STATS=log — 보내지 않음): {days.ToJsonString()}");
                Clear(sent);
                return;
            }
            var body = new JsonObject { ["token"] = ReportService.Token, ["type"] = "stats", ["days"] = days };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await ReportService.Http.PostAsync(ReportService.Endpoint, content).ConfigureAwait(true);
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            int status = response.IsSuccessStatusCode ? ReportService.ParseStatus(text) : (int)response.StatusCode;
            if (status == 200)
            {
                Clear(sent);
                Log.Info($"사용 통계 보냄: {count}일치");
            }
            else
            {
                // 400 = 받는 쪽이 아직 stats 를 모르는 옛 Code.gs — 모아 두었다가 다음에
                Log.Info($"사용 통계 못 보냄 (응답 {status}) — {count}일치 모아 둠");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"사용 통계 못 보냄 ({ex.GetType().Name}) — 다음에 다시");
        }
        finally
        {
            _sending = false;
        }
    }

    /// <summary>보낸 것만 지움 (nonce 로 — 보내는 동안 새로 붙은 것은 남김).</summary>
    private void Clear(IReadOnlyCollection<string> sent)
    {
        var state = Load();
        state.Pending.RemoveAll(d => d["nonce"]?.GetValue<string>() is { } n && sent.Contains(n));
        Save(state);
    }

    // ───────────────────────── 저장 ─────────────────────────

    private sealed class State
    {
        public string? LastDay { get; set; }
        public DateTime? LastAt { get; set; }
        public List<JsonObject> Pending { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private State Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(_path), JsonOptions) ?? new State();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"stats.json 읽기 실패 — 새로 시작: {ex.Message}");
        }
        return new State();
    }

    private void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"stats.json 저장 실패: {ex.Message}");
        }
    }
}
