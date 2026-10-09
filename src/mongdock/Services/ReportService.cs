using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Mongdock.Models;
using Mongdock.Native;

namespace Mongdock.Services;

public enum ReportKind { Bug, Question, Idea }

public enum ReportSendResult
{
    /// <summary>받는 쪽이 메일을 만듦 (응답 status 200).</summary>
    Sent,
    /// <summary>하루 한도 초과 (응답 status 429).</summary>
    Limited,
    /// <summary>네트워크·서버 오류, 그 밖의 응답.</summary>
    Failed,
    /// <summary>받는 주소(<see cref="ReportService.Endpoint"/>)가 아직 비어 있음.</summary>
    NotConfigured,
}

/// <summary>
/// "문제 신고하기…": 진단 정보를 모으고(개인정보는 <see cref="ReportRedactor"/> 로 가림) Google Apps Script 웹 앱에 JSON 으로 보낸다.
/// 받는 쪽은 저장소 tools/support-intake/Code.gs — 지원 주소로 메일 한 통을 만든다. 앱에 비밀번호·키는 없다(token 은 비밀 아님).
/// Apps Script 는 HTTP 상태 코드를 못 바꾸므로 결과는 응답 JSON 의 status 로 판단한다.
/// </summary>
public static class ReportService
{
    /// <summary>Apps Script 웹 앱 URL (…/exec). 비어 있으면 창은 열리되 "준비 중" 안내.</summary>
    public const string Endpoint = "https://script.google.com/macros/s/AKfycbypcummOSv5veADZVUuOC6kyFeSfkeoh6rnqABAQiIEMp59O86a57u5PNqbtAicC-qo/exec";

    public const string SupportAddress = "mongdock+help@gmail.com";
    internal const string Token = "mongdock-report-v1";

    /// <summary>받는 쪽 한도 (Code.gs MAX_MESSAGE / MAX_DIAG 와 같게).</summary>
    public const int MaxMessage = 4000;
    public const int MaxDiagnostics = 12000;
    private const int LogLines = 200;

    public static bool IsConfigured => Endpoint.Length > 0;

    internal static readonly HttpClient Http = CreateHttpClient();

    /// <summary>하루에 보낼 수 있는 신고 수 (이 PC 안에서 셈 — 받는 쪽에 PC 를 알아볼 값을 보내지 않으려고).</summary>
    public const int DailyLimit = 5;
    private static readonly string CountPath = Path.Combine(AppInfo.DataDirectory, "cache", "report-count.json");

    /// <summary>오늘 보낸 수 (cache/report-count.json 에 날짜·횟수만).</summary>
    private static int SentToday()
    {
        try
        {
            if (!File.Exists(CountPath)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(CountPath));
            var r = doc.RootElement;
            return r.TryGetProperty("date", out var d) && d.GetString() == DateTime.Today.ToString("yyyy-MM-dd")
                   && r.TryGetProperty("count", out var c) && c.TryGetInt32(out int n) ? n : 0;
        }
        catch { return 0; }
    }

    private static void CountSent()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CountPath)!);
            AtomicFile.WriteAllText(CountPath, JsonSerializer.Serialize(new { date = DateTime.Today.ToString("yyyy-MM-dd"), count = SentToday() + 1 }));
        }
        catch (Exception ex) { Log.Warn($"신고 횟수 기록 실패: {ex.Message}"); }
    }

    /// <summary>어셈블리 정보 버전 ("+커밋" 꼬리 제거).</summary>
    public static string AppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(ReportService).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "?";
    }

    // ───────────────────────── 진단 정보 ─────────────────────────

    /// <summary>
    /// 진단 정보 앞부분 (가린 뒤): 앱 버전·윈도우·모니터·배터리·터치·설정 요약(경로·주소 없이).
    /// 설정을 읽으므로 UI 스레드에서 (설정 객체는 UI 스레드에서 바뀜). 로그는 <see cref="AppendLog"/> 로 따로.
    /// </summary>
    public static string BuildSystemInfo(Settings s, IEnumerable<MonitorInfo> monitors, bool hasTouch, ReportRedactor.Context ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"앱: mongdock {AppVersion()} ({AppInfo.InstallKind})");
        sb.AppendLine($"윈도우: {WindowsVersion()}");
        sb.AppendLine($"언어: {CultureInfo.CurrentUICulture.Name}");

        int i = 0;
        foreach (var m in monitors)
        {
            var b = m.Bounds;
            sb.AppendLine($"모니터 {++i}: {Math.Round(b.Width * m.Scale)}×{Math.Round(b.Height * m.Scale)}, 배율 {Math.Round(m.Scale * 100)}%{(m.IsPrimary ? ", 주 모니터" : "")}");
        }
        sb.AppendLine($"배터리: {BatteryText()}");
        sb.AppendLine($"터치: {(hasTouch ? "있음" : "없음")}");

        sb.AppendLine();
        sb.AppendLine("[설정 요약]");
        var d = s.Dock;
        sb.AppendLine($"독: {(d.Enabled ? "켜짐" : "꺼짐")}, 위치 {d.Edge}, 모드 {d.Mode}, 테마 {d.Theme}, 아이콘 {d.IconSize:0}, 확대 {d.HoverScale:0.##}, 흐림 {OnOff(d.Blur)}, 실행 중 앱 {OnOff(d.ShowRunningApps)}, 고정 앱 {s.Pins.Count}개");
        var t = s.TopBar;
        sb.AppendLine($"상단바: {(t.Enabled ? "켜짐" : "꺼짐")}, 높이 {t.Height:0}, 색 {t.ColorMode}, 모든 모니터 {OnOff(t.ShowOnAllMonitors)}, 앱 메뉴 {OnOff(t.ShowAppMenus)}, 원래 메뉴 막대 숨김 {OnOff(t.HideNativeMenuBars)}, 트레이 {OnOff(t.ShowTrayIcons)}, 검색 {t.SearchMode}, 자리 확보 {OnOff(t.ReserveSpace)}");
        var n = s.Notifications;
        sb.AppendLine($"알림: 배너 {OnOff(n.ShowNotificationBanners)}, 윈도우 알림 숨기기 {OnOff(n.HideWindowsToastPopups)}, 배터리 부족 알림 {OnOff(n.LowBatteryAlerts)}, 알림 소리 {(string.IsNullOrEmpty(n.Sound) ? "기본" : "바꿈")}");
        sb.AppendLine($"백업: {(UpdateBackup.LatestLabel(AppInfo.DataDirectory) is { } bak ? $"있음({bak})" : "없음")}");
        sb.AppendLine($"기타: 작업 표시줄 숨기기 {OnOff(s.HideWindowsTaskbar)}, 성능 모드 {OnOff(s.PerformanceMode)}, 시작 시 실행 {OnOff(s.StartWithWindows)}, 업데이트 확인 {OnOff(s.CheckForUpdates)}, 글꼴 {(s.FontFamily == "Pretendard" ? "기본" : "바꿈")}");

        return ReportRedactor.Redact(sb.ToString(), ctx);
    }

    /// <summary>
    /// 앞부분 + 최근 로그 (가린 뒤). 길면 오래된 로그 줄부터 빼서 <see cref="MaxDiagnostics"/> 안으로. 파일을 읽으므로 UI 스레드 밖에서.
    /// </summary>
    public static string AppendLog(string head, ReportRedactor.Context ctx)
    {
        // 최근 로그: 가린 뒤, 넘치면 오래된 줄부터 뺌
        var lines = ReadLogTail(LogLines).Select(l => ReportRedactor.RedactLog(l, ctx)).ToList();
        const string logTitle = "\n[최근 로그]\n";
        int budget = MaxDiagnostics - head.Length - logTitle.Length;
        int total = lines.Sum(l => l.Length + 1);
        int skip = 0;
        while (skip < lines.Count && total > budget) total -= lines[skip++].Length + 1;
        return head + logTitle + string.Join("\n", lines.Skip(skip));
    }

    private static string OnOff(bool on) => on ? "켬" : "끔";

    /// <summary>예 "Windows 11 Home 24H2 (26200.6584)". ProductName 은 11 에서도 "Windows 10" 이라 빌드 번호로 고침.</summary>
    private static string WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            string display = key?.GetValue("DisplayVersion") as string ?? "";
            string build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
            string ubr = key?.GetValue("UBR") is int u ? "." + u.ToString(CultureInfo.InvariantCulture) : "";
            if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            return $"{product} {display} ({build}{ubr})".Replace("  ", " ");
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }

    private static string BatteryText()
    {
        try
        {
            if (!PowerApi.GetSystemPowerStatus(out var st)) return "알 수 없음";
            if (st.BatteryFlag == PowerApi.BATTERY_FLAG_NO_BATTERY) return "없음";
            if (st.BatteryFlag == PowerApi.BATTERY_FLAG_UNKNOWN) return "알 수 없음";
            return $"있음 ({st.BatteryLifePercent}%, {(st.ACLineStatus == 1 ? "전원 연결" : "배터리 사용")})";
        }
        catch
        {
            return "알 수 없음";
        }
    }

    /// <summary>mongdock.log 마지막 n 줄 (로그가 쓰는 중이어도 읽음). 예외 줄(스택)도 한 줄씩 셈.</summary>
    private static List<string> ReadLogTail(int n)
    {
        var tail = new Queue<string>(n + 1);
        try
        {
            if (!File.Exists(Log.LogPath)) return new List<string>();
            using var fs = new FileStream(Log.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                tail.Enqueue(line);
                if (tail.Count > n) tail.Dequeue();
            }
        }
        catch (Exception ex)
        {
            tail.Enqueue($"(로그를 읽지 못함: {ex.GetType().Name})");
        }
        return tail.ToList();
    }

    // ───────────────────────── 보내기 ─────────────────────────

    public static string KindValue(ReportKind kind) => kind switch
    {
        ReportKind.Question => "question",
        ReportKind.Idea => "idea",
        _ => "bug",
    };

    public static async Task<ReportSendResult> SendAsync(
        ReportKind kind, string message, string contact, string diagnostics, CancellationToken ct = default)
    {
        if (!IsConfigured) return ReportSendResult.NotConfigured;
        if (SentToday() >= DailyLimit)
        {
            Log.Info($"문제 신고: 오늘 {DailyLimit}건을 넘어 보내지 않음");
            return ReportSendResult.Limited;
        }
        try
        {
            var body = new Dictionary<string, string>
            {
                ["token"] = Token,
                // 받는 쪽(Code.gs)이 요구하는 칸이라 남기되 보낼 때마다 새 무작위 값 — 신고끼리·PC 를 잇지 못하게
                ["clientId"] = Guid.NewGuid().ToString("D"),
                ["kind"] = KindValue(kind),
                ["message"] = Truncate(message.Trim(), MaxMessage),
                ["contact"] = contact.Trim(),
                ["appVersion"] = AppVersion(),
                ["lang"] = Loc.Code,
                ["diagnostics"] = Truncate(diagnostics, MaxDiagnostics),
            };
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"문제 신고 보내기 실패: HTTP {(int)response.StatusCode}");
                return ReportSendResult.Failed;
            }
            int status = ParseStatus(text);
            Log.Info($"문제 신고 보냄: 종류 {KindValue(kind)}, 응답 {status}");
            if (status == 200) CountSent();
            return status switch
            {
                200 => ReportSendResult.Sent,
                429 => ReportSendResult.Limited,
                _ => ReportSendResult.Failed,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"문제 신고 보내기 실패: {ex.GetType().Name} {ex.Message}");
            return ReportSendResult.Failed;
        }
    }

    /// <summary>응답 JSON {"status":200,...} 의 status. 형식이 다르면 0.</summary>
    internal static int ParseStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("status", out var st) && st.TryGetInt32(out int v) ? v : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>보내기 실패 때 클립보드에 넣을 글 (메일로 대신 보낼 수 있게).</summary>
    public static string ClipboardText(ReportKind kind, string message, string contact, string diagnostics)
    {
        string label = kind switch { ReportKind.Question => "질문", ReportKind.Idea => "제안", _ => "버그" };
        var sb = new StringBuilder();
        sb.AppendLine($"[몽독 신고][{label}] (v{AppVersion()})");
        if (!string.IsNullOrWhiteSpace(contact)) sb.AppendLine($"답장 받을 주소: {contact.Trim()}");
        sb.AppendLine();
        sb.AppendLine(message.Trim());
        sb.AppendLine();
        sb.AppendLine("──── 함께 보낼 정보 ────");
        sb.Append(diagnostics);
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>MenuRulesService 와 같은 설정: 시스템 프록시(App 시작 때 기본 자격 증명 붙임), Apps Script 의 302 리디렉션 따라감.</summary>
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppVersion()));
        return client;
    }
}
