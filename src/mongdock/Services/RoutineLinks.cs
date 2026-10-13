using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 루틴 "링크" 항목 (spec-routines §15-2, §15-4): 웹 주소 여러 개 = 브라우저 창 하나에 탭으로, 앱 링크(spotify:, discord:// …)는 연결된 앱에 맡김.
/// 크로미움 계열(chrome_elf.dll 이 있는 브라우저)은 --new-window url1 url2, 파이어폭스 계열(mozglue.dll)은 -new-window url1 -new-tab url2,
/// 모르는 브라우저는 하나씩. javascript:·file: 같은 위험한 링크와 열 앱이 없는 링크는 막음.
/// </summary>
internal static class RoutineLinks
{
    public enum Browser { Unknown, Chromium, Firefox }

    /// <summary>항목의 링크들 (첫 번째 = Target, 그다음 탭들). 빈 것은 뺌.</summary>
    public static List<string> Urls(RoutineItem item)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Target)) list.Add(item.Target.Trim());
        if (item.Tabs is { } tabs) list.AddRange(tabs.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
        return list;
    }

    /// <summary>링크 → Uri. "mail.company.com" 처럼 꼴이 없으면 https:// 를 붙여 봄.</summary>
    public static Uri? Parse(string link)
    {
        link = link.Trim();
        if (link.Length == 0) return null;
        if (Uri.TryCreate(link, UriKind.Absolute, out var u) && u.Scheme.Length > 1) return u; // "C:\…" 은 file 이라 아래에서 막힘
        return !link.Contains(' ') && link.Contains('.') && Uri.TryCreate("https://" + link, UriKind.Absolute, out var w) ? w : null;
    }

    public static bool IsWeb(Uri u) => u.Scheme is "http" or "https";

    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "vbscript", "file", "data", "about", "jar", "res", "shell", "ms-msdt", "search-ms", "search", "ms-appinstaller",
        "ms-officecmd", "ms-word", "ms-excel", "ms-powerpoint", "ms-cxh", "ms-cxh-full", "mk", "its", "ms-its", "hcp",
    };

    /// <summary>열어도 되는 링크인지 — 웹은 늘, 앱 링크는 위험 목록이 아니고 이 PC 에 열 앱이 등록된 것만.</summary>
    public static bool Allowed(Uri u) => IsWeb(u) || !Blocked.Contains(u.Scheme) && AppFor(u.Scheme) is not null;

    // ── 연결된 앱 ──

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint size);

    /// <summary>링크 종류(스킴)를 여는 앱 exe (등록 안 됐으면 null).</summary>
    public static string? AppFor(string scheme)
    {
        try
        {
            using (var key = Registry.ClassesRoot.OpenSubKey(scheme))
                if (key?.GetValue("URL Protocol") is null) return null;
            uint size = 1024;
            var sb = new StringBuilder((int)size);
            const int ASSOCF_IS_PROTOCOL = 0x1000, ASSOCSTR_EXECUTABLE = 2;
            if (AssocQueryString(ASSOCF_IS_PROTOCOL, ASSOCSTR_EXECUTABLE, scheme, "open", sb, ref size) != 0) return "";
            return sb.ToString();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>이 항목이 띄울 창의 exe (첫 링크가 웹 = 기본 브라우저, 앱 링크 = 그 앱). 아이콘·창 찾기용.</summary>
    public static string? HandlerExe(RoutineItem item)
    {
        var first = Urls(item).Select(Parse).FirstOrDefault(u => u is not null);
        if (first is null || IsWeb(first)) return RoutineService.DefaultBrowser();
        return AppFor(first.Scheme) is { Length: > 0 } exe ? exe : null;
    }

    // ── 브라우저 판별 (15-4) ──

    public static Browser Kind(string browserExe)
    {
        try
        {
            string dir = Path.GetDirectoryName(browserExe) ?? "";
            string exe = Path.GetFileNameWithoutExtension(browserExe).ToLowerInvariant();
            if (exe is "firefox" or "librewolf" or "waterfox" or "floorp" or "zen" || File.Exists(Path.Combine(dir, "mozglue.dll"))) return Browser.Firefox;
            if (exe is "chrome" or "msedge" or "whale" or "brave" or "vivaldi" or "opera" or "chromium" || File.Exists(Path.Combine(dir, "chrome_elf.dll"))
                || Directory.EnumerateDirectories(dir).Any(sub => File.Exists(Path.Combine(sub, "chrome_elf.dll")))) return Browser.Chromium; // 크롬은 버전 폴더 안
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return Browser.Unknown;
    }

    /// <summary>창 하나에 탭으로 여는 실행 인자.</summary>
    public static string TabArgs(Browser kind, IReadOnlyList<Uri> urls) => kind == Browser.Firefox
        ? string.Join(" ", urls.Select((u, i) => (i == 0 ? "-new-window" : "-new-tab") + $" \"{u.AbsoluteUri}\""))
        : "--new-window " + string.Join(" ", urls.Select(u => $"\"{u.AbsoluteUri}\""));

    // ── 이름 자동으로 (15-4 직관성) ──

    /// <summary>붙여 넣은 링크로 이름: 알려진 앱 링크는 "Spotify 재생 목록" 같이, 웹 주소는 사이트 이름(www. 뺀 호스트).</summary>
    public static string? AutoName(string link)
    {
        var u = Parse(link);
        if (u is null) return null;
        string host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        string path = u.AbsolutePath.ToLowerInvariant();
        return u.Scheme.ToLowerInvariant() switch
        {
            "spotify" => link.Contains("playlist", StringComparison.OrdinalIgnoreCase) ? Loc.T("Spotify 재생 목록") : "Spotify",
            "discord" => Loc.T("Discord 채널"),
            "notion" => Loc.T("Notion 페이지"),
            "zoommtg" or "zoomus" => Loc.T("Zoom 회의"),
            "msteams" => "Microsoft Teams",
            "slack" => "Slack",
            "obsidian" => "Obsidian",
            "http" or "https" when host.EndsWith("spotify.com") => path.Contains("playlist") ? Loc.T("Spotify 재생 목록") : "Spotify",
            "http" or "https" when host.EndsWith("discord.com") && path.StartsWith("/channels") => Loc.T("Discord 채널"),
            "http" or "https" when host.EndsWith("notion.so") || host.EndsWith("notion.site") => Loc.T("Notion 페이지"),
            "http" or "https" when host.EndsWith("zoom.us") && path.StartsWith("/j") => Loc.T("Zoom 회의"),
            "http" or "https" => host,
            _ => char.ToUpperInvariant(u.Scheme[0]) + u.Scheme[1..],
        };
    }

    /// <summary>앱마다 링크를 얻는 방법 (?) 안내.</summary>
    public static string HowToGetLinks() => string.Join("\n", new[]
    {
        Loc.T("스포티파이: 재생 목록 ⋯ → 공유 → 링크 복사"),
        Loc.T("디스코드: 채널 오른쪽 클릭 → 링크 복사"),
        Loc.T("노션: 페이지 오른쪽 위 공유 → 링크 복사"),
        Loc.T("줌: 회의 초대 링크 복사"),
        Loc.T("웹사이트: 주소창의 주소 복사"),
    });

    /// <summary>목록 요약: "Chrome — mail.company.com 외 2개 탭" (웹), 앱 링크는 이름만.</summary>
    public static string? Summary(RoutineItem item)
    {
        var urls = Urls(item).Select(Parse).OfType<Uri>().ToList();
        if (urls.Count == 0) return null;
        if (!IsWeb(urls[0])) return null;
        string host = urls[0].Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? urls[0].Host[4..] : urls[0].Host;
        string browser = RoutineService.DefaultBrowser() is { } b ? BrowserName(b) : Loc.T("브라우저");
        return urls.Count > 1 ? Loc.F($"{browser} — {host} 외 {urls.Count - 1}개 탭") : $"{browser} — {host}";
    }

    private static string BrowserName(string exe) => Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "whale" => "Whale",
        "brave" => "Brave",
        "firefox" => "Firefox",
        "opera" => "Opera",
        "vivaldi" => "Vivaldi",
        var other => other,
    };
}
