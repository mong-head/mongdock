using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 설치된 게임 목록 (spec-routines §15-3·15-4): 런처가 아니라 게임 자체를 루틴에서 바로 열게.
/// 스팀(libraryfolders.vdf → appmanifest_*.acf) · 에픽(매니페스트 .item) · GOG(레지스트리)는 라이브러리를 직접 읽고,
/// 배틀넷·EA·유비소프트 등은 시작 메뉴의 게임 바로 가기(.url 런처 링크)를 씀. PC 안에서만 읽고 게임 이름·id 는 통계로 보내지 않음.
/// </summary>
internal static class GameCatalog
{
    /// <summary>게임 하나: 이름, 런처 표시, 여는 링크(또는 exe), 게임 exe(아이콘·창 찾기, 모르면 null).</summary>
    public sealed record Game(string Name, string Launcher, string Launch, string? Exe, bool LaunchIsExe = false);

    private static List<Game>? _cache;
    private static DateTime _cacheAt;

    public static List<Game> All()
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(5)) return _cache;
        var list = new List<Game>();
        try { list.AddRange(Steam()); } catch (Exception ex) { Log.Warn($"스팀 게임 목록 실패: {ex.GetType().Name}"); }
        try { list.AddRange(Epic()); } catch (Exception ex) { Log.Warn($"에픽 게임 목록 실패: {ex.GetType().Name}"); }
        try { list.AddRange(Gog()); } catch (Exception ex) { Log.Warn($"GOG 게임 목록 실패: {ex.GetType().Name}"); }
        try { list.AddRange(StartMenu()); } catch (Exception ex) { Log.Warn($"시작 메뉴 게임 바로 가기 실패: {ex.GetType().Name}"); }
        _cache = list.GroupBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _cacheAt = DateTime.UtcNow;
        return _cache;
    }

    /// <summary>게임 → 루틴 항목: 링크로 열고(스팀 steam://rungameid/…), 아이콘·창 찾기는 게임 exe.</summary>
    public static RoutineItem ToItem(Game g) => g.LaunchIsExe
        ? new RoutineItem { Kind = RoutineItemKind.App, Target = g.Launch, Name = g.Name }
        : new RoutineItem { Kind = RoutineItemKind.Url, Target = g.Launch, Name = g.Name, GameExe = g.Exe };

    /// <summary>실행 중 창의 exe 가 알려진 게임이면 그 게임 ("지금 화면 저장"·루틴 추천이 런처가 아니라 게임으로).</summary>
    public static Game? FromExe(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        string full = exePath.Replace('/', '\\');
        return All().FirstOrDefault(g => g.Exe is { } e && string.Equals(e, full, StringComparison.OrdinalIgnoreCase))
            ?? All().FirstOrDefault(g => g.Exe is { } e && Path.GetDirectoryName(e) is { } dir && full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)
                && !g.LaunchIsExe && full.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>exe 이름(소문자, 확장자 없이)으로.</summary>
    public static Game? FromExeName(string exeName) =>
        All().FirstOrDefault(g => g.Exe is { } e && string.Equals(Path.GetFileNameWithoutExtension(e), exeName, StringComparison.OrdinalIgnoreCase));

    // ── 스팀 ──

    private static IEnumerable<Game> Steam()
    {
        string? root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(root)) yield break;
        root = root.Replace('/', '\\');
        var libraries = new List<string> { root };
        string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(acf);
                string? id = Field(text, "appid"), name = Field(text, "name"), dir = Field(text, "installdir");
                if (id is null || name is null || IsTool(name)) continue;
                string? exe = dir is null ? null : MainExe(Path.Combine(apps, "common", dir), name);
                yield return new Game(name, "Steam", $"steam://rungameid/{id}", exe);
            }
        }
    }

    private static string? Field(string acf, string key) => Regex.Match(acf, "\"" + key + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>스팀 런타임·재배포 패키지 같은 게임 아닌 것.</summary>
    private static bool IsTool(string name) => Regex.IsMatch(name, @"Steamworks|Redistributable|Steam Linux Runtime|Proton|SteamVR|Dedicated Server|\bSDK\b", RegexOptions.IgnoreCase);

    /// <summary>설치 폴더에서 게임 exe 추측: 이름이 비슷한 것 → 가장 큰 exe (런처·크래시 리포터·설치 프로그램 제외). 두 단계까지만.</summary>
    private static string? MainExe(string dir, string name)
    {
        if (!Directory.Exists(dir)) return null;
        var exes = Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
            .Where(f => !Regex.IsMatch(Path.GetFileName(f), @"unins|crash|report|setup|install|redist|vc_redist|dxsetup|launcher|helper|update|easyanticheat|be_service|ue4prereq", RegexOptions.IgnoreCase))
            .ToList();
        if (exes.Count == 0) return null;
        string key = Regex.Replace(name, @"[^\p{L}\p{N}]", "").ToLowerInvariant();
        return exes.FirstOrDefault(f => key.Length > 0 && Regex.Replace(Path.GetFileNameWithoutExtension(f), @"[^\p{L}\p{N}]", "").ToLowerInvariant() is var n && n.Length > 2 && (key.Contains(n) || n.Contains(key)))
            ?? exes.OrderByDescending(f => new FileInfo(f).Length).First();
    }

    // ── 에픽 ──

    private static IEnumerable<Game> Epic()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            Game? g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string? name = S("DisplayName"), ns = S("CatalogNamespace"), item = S("CatalogItemId"), app = S("AppName"), loc = S("InstallLocation"), exe = S("LaunchExecutable");
                if (name is not null && ns is not null && item is not null && app is not null)
                    g = new Game(name, "Epic", $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true",
                        loc is not null && exe is not null ? Path.Combine(loc, exe) : null);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
            if (g is not null) yield return g;
        }
    }

    // ── GOG ──

    private static IEnumerable<Game> Gog()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null) yield break;
        foreach (var id in games.GetSubKeyNames())
        {
            using var k = games.OpenSubKey(id);
            if (k?.GetValue("gameName") is string name && k.GetValue("exe") is string exe && File.Exists(exe))
                yield return new Game(name, "GOG", exe, exe, LaunchIsExe: true);
        }
    }

    // ── 그 밖 런처: 시작 메뉴 .url 게임 링크 ──

    private static readonly Dictionary<string, string> LauncherSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steam"] = "Steam", ["com.epicgames.launcher"] = "Epic", ["battlenet"] = "Battle.net", ["origin"] = "EA", ["origin2"] = "EA",
        ["link2ea"] = "EA", ["uplay"] = "Ubisoft", ["goggalaxy"] = "GOG", ["amazon-games"] = "Amazon", ["riotclient"] = "Riot",
        ["nxl"] = "Nexon", ["ngm"] = "Nexon", ["rockstar"] = "Rockstar",
    };

    private static IEnumerable<Game> StartMenu()
    {
        foreach (var sf in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.DesktopDirectory })
        {
            string root = Environment.GetFolderPath(sf);
            if (!Directory.Exists(root)) continue;
            foreach (var url in Directory.EnumerateFiles(root, "*.url", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                string? link = null, icon = null;
                foreach (var line in File.ReadLines(url))
                {
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) link = line[4..].Trim();
                    else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) icon = line[9..].Trim();
                }
                if (link is null || link.IndexOf(':') is var c && c <= 0) continue;
                if (!LauncherSchemes.TryGetValue(link[..c], out var launcher)) continue;
                string? exe = icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) ? icon : null;
                yield return new Game(Path.GetFileNameWithoutExtension(url), launcher, link, exe);
            }
        }
    }
}
