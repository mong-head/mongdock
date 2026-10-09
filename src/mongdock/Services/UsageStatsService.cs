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
/// - 못 보내면(오프라인·받는 쪽 오류) 날짜를 붙여 두었다가 다음 확인(1시간마다) 때 한꺼번에. 7일치까지만 (오래된 것부터 버림).
/// - 오류 수 = 지난 신호 이후 로그의 ERROR 줄 수 (로그 파일 시각으로 셈 — 다시 시작·크래시에도 이어짐).
/// - 설정 "사용 통계 보내기"(SendUsageStats)를 끄면 아무것도 만들지 않고 모아 둔 것도 지움.
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
        return svc;
    }

    public void Dispose() => _timer.Stop();

    private void Tick()
    {
        try
        {
            var state = Load();
            if (!_services.Settings.Current.SendUsageStats)
            {
                if (state.Pending.Count > 0) { state.Pending.Clear(); Save(state); Log.Info("사용 통계 꺼짐: 모아 둔 것 지움"); }
                return;
            }
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (state.LastDay != today)
            {
                var now = DateTime.Now;
                state.Pending.Add(Snapshot(today, CountErrorsSince(state.LastAt ?? now.AddDays(-1))));
                while (state.Pending.Count > MaxPendingDays) state.Pending.RemoveAt(0);
                state.LastDay = today;
                state.LastAt = now;
                Save(state);
            }
            if (state.Pending.Count > 0 && !_sending) _ = SendAsync();
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
            ["errors"] = errors,
        };
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
            if (_logOnly)
            {
                Log.Info($"사용 통계 (MONGDOCK_STATS=log — 보내지 않음): {days.ToJsonString()}");
                Clear(count);
                return;
            }
            var body = new JsonObject { ["token"] = ReportService.Token, ["type"] = "stats", ["days"] = days };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await ReportService.Http.PostAsync(ReportService.Endpoint, content).ConfigureAwait(true);
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            int status = response.IsSuccessStatusCode ? ReportService.ParseStatus(text) : (int)response.StatusCode;
            if (status == 200)
            {
                Clear(count);
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

    /// <summary>보낸 앞쪽 count 개만 지움 (보내는 동안 새로 붙은 것은 남김).</summary>
    private void Clear(int count)
    {
        var state = Load();
        state.Pending.RemoveRange(0, Math.Min(count, state.Pending.Count));
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
