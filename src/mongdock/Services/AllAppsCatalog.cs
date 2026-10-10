using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>앱 모음 판의 앱 하나. Key = shell:AppsFolder 파싱 이름(실행·아이콘·설정 키). Exe = 실행 파일 이름(소문자, 확장자 없이).</summary>
internal sealed record AppEntry(string Key, string Name, string? Exe, string? Family, string? StartFolder, string? Shortcut, string? TargetPath);

/// <summary>
/// 앱 모음 판 (#24) 앱 목록과 자동 분류.
/// - 목록: shell:AppsFolder (시작 메뉴 "모든 앱") + 시작 메뉴 바로 가기(폴더 이름·대상 exe — 분류와 "독에 고정"·"파일 위치 열기"용).
///   제거 프로그램·도움말·웹 링크 같은 항목은 뺌. 백그라운드에서 만들고 60초 동안 재사용.
/// - 분류: 사용자가 옮긴 것(overrides) → 알려진 앱 표(menus/app-categories.json: exe → 스토어 앱 패키지 → 이름 낱말) → 시작 메뉴 폴더 이름 → "기타".
/// </summary>
internal static class AllAppsCatalog
{
    public const string Other = "other";

    /// <summary>사용 통계 "allAppsOpened": 지난 하루치 신호 뒤로 판을 연 횟수 (이번 실행 안에서만 셈).</summary>
    public static int OpenedSinceSignal;
    public static readonly string[] DefaultGroups = { "work", "chat", "web", "media", "music", "games", "dev", "tools", Other };

    /// <summary>이름 낱말을 볼 때 묶음 순서 (겹치는 낱말: "미디어 플레이어"는 음악, "player"는 사진·영상).</summary>
    private static readonly string[] NameOrder = { "music", "games", "chat", "web", "work", "dev", "media", "tools" };

    public static string DefaultName(string id) => id switch
    {
        "work" => Loc.T("업무"),
        "chat" => Loc.T("소통"),
        "web" => Loc.T("인터넷"),
        "media" => Loc.T("사진·영상"),
        "music" => Loc.T("음악"),
        "games" => Loc.T("게임"),
        "dev" => Loc.T("개발"),
        "tools" => Loc.T("도구"),
        Other => Loc.T("기타"),
        _ => Loc.T("새 묶음"),
    };

    public static string GroupName(AllAppsSettings s, string id) =>
        s.Groups.FirstOrDefault(g => g.Id == id)?.Name is { Length: > 0 } custom ? custom : DefaultName(id);

    /// <summary>화면 순서의 묶음 id (설정 순서 + 빠진 기본 묶음은 뒤에, "기타"는 늘 끝).</summary>
    public static List<string> GroupOrder(AllAppsSettings s)
    {
        var order = s.Groups.Select(g => g.Id).Where(id => id != Other).Distinct().ToList();
        foreach (var id in DefaultGroups) if (id != Other && !order.Contains(id)) order.Add(id);
        order.Add(Other);
        return order;
    }

    // ───────────────────────── 목록 ─────────────────────────

    private static readonly object Gate = new();
    private static List<AppEntry>? _cache;
    private static DateTime _cacheTime;

    /// <summary>앱 목록 (60초 캐시). 처음엔 수백 ms 걸릴 수 있으니 백그라운드에서.</summary>
    public static IReadOnlyList<AppEntry> Apps(bool refresh = false)
    {
        lock (Gate)
        {
            if (!refresh && _cache is not null && DateTime.UtcNow - _cacheTime < TimeSpan.FromSeconds(60)) return _cache;
            _cache = Build();
            _cacheTime = DateTime.UtcNow;
            return _cache;
        }
    }

    /// <summary>캐시된 목록이 있으면 (판을 바로 그리게).</summary>
    public static IReadOnlyList<AppEntry>? Cached
    {
        get { lock (Gate) return _cache; }
    }

    private static readonly Regex DesktopAumidExe = new(@"(?:^|\.)([A-Za-z0-9_\-]+)\.exe(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Junk = new(@"(^|\b)(uninstall|uninstaller|제거|삭제|readme|read me|도움말|manual|설명서|license|라이선스|release notes|website|web site|홈페이지|documentation|changelog)(\b|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] JunkExtensions = { ".url", ".chm", ".txt", ".pdf", ".htm", ".html", ".rtf", ".hlp", ".ini", ".log" };

    private static List<AppEntry> Build()
    {
        var shortcuts = StartMenuShortcuts();
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parsing, display) in AppsFolder.Enumerate())
        {
            string name = display.Trim();
            if (name.Length == 0 || !seen.Add(parsing)) continue;
            if (JunkExtensions.Any(e => parsing.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || Junk.IsMatch(name)) continue;
            if (parsing.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || parsing.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) continue;

            shortcuts.TryGetValue(name, out var sc);
            string? exe = null;
            if (parsing.Contains('\\') || parsing.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exe = Path.GetFileNameWithoutExtension(parsing);
            else if (!parsing.Contains('!') && DesktopAumidExe.Match(parsing) is { Success: true } m)
                exe = m.Groups[1].Value;
            if (exe is null && sc.Target is { Length: > 0 } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exe = Path.GetFileNameWithoutExtension(t);
            if (exe is not null && (exe.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || exe.Equals("uninstall", StringComparison.OrdinalIgnoreCase))) continue;
            string? family = parsing.Contains('!') ? AppsFolder.FamilyOf(parsing) : null;
            list.Add(new AppEntry(parsing, name, exe?.ToLowerInvariant(), family, sc.Folder, sc.Lnk, sc.Target));
        }
        return list;
    }

    /// <summary>모든 사용자·내 시작 메뉴 바로 가기: 이름 → (상대 폴더, .lnk, 대상). 같은 이름이면 처음 것.</summary>
    private static Dictionary<string, (string? Folder, string? Lnk, string? Target)> StartMenuShortcuts()
    {
        var map = new Dictionary<string, (string?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sf in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            string root = Environment.GetFolderPath(sf);
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string lnk in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    string name = Path.GetFileNameWithoutExtension(lnk);
                    if (map.ContainsKey(name)) continue;
                    string rel = Path.GetRelativePath(root, Path.GetDirectoryName(lnk) ?? root);
                    map[name] = (rel == "." ? null : rel, lnk, PinFactory.ShortcutTarget(lnk));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return map;
    }

    // ───────────────────────── 분류 ─────────────────────────

    private sealed class Rule
    {
        public HashSet<string> Exe { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Aumid { get; } = new();
        public List<string> Name { get; } = new();
    }

    private static Dictionary<string, Rule>? _rules;
    private static Dictionary<string, List<string>>? _folders;

    private static void LoadRules()
    {
        if (_rules is not null) return;
        var rules = new Dictionary<string, Rule>();
        var folders = new Dictionary<string, List<string>>();
        try
        {
            using var stream = typeof(AllAppsCatalog).Assembly.GetManifestResourceStream("mongdock.app-categories.json");
            if (stream is not null)
            {
                using var doc = JsonDocument.Parse(stream);
                foreach (var g in doc.RootElement.GetProperty("groups").EnumerateObject())
                {
                    var rule = new Rule();
                    if (g.Value.TryGetProperty("exe", out var e)) foreach (var x in e.EnumerateArray()) rule.Exe.Add(x.GetString() ?? "");
                    if (g.Value.TryGetProperty("aumid", out var a)) foreach (var x in a.EnumerateArray()) rule.Aumid.Add(x.GetString() ?? "");
                    if (g.Value.TryGetProperty("name", out var n)) foreach (var x in n.EnumerateArray()) rule.Name.Add((x.GetString() ?? "").ToLowerInvariant());
                    rules[g.Name] = rule;
                }
                if (doc.RootElement.TryGetProperty("folders", out var f))
                    foreach (var g in f.EnumerateObject())
                        folders[g.Name] = g.Value.EnumerateArray().Select(x => (x.GetString() ?? "").ToLowerInvariant()).Where(x => x.Length > 0).ToList();
            }
        }
        catch (Exception ex)
        {
            Log.Error("앱 분류 표 읽기 실패", ex);
        }
        _folders = folders;
        _rules = rules;
    }

    private static readonly Dictionary<string, string> ClassifyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>자동 분류 (사용자가 옮긴 것은 부르는 쪽에서 먼저). 앱마다 한 번만 계산 (판을 다시 그릴 때마다 규칙을 다시 돌지 않게).</summary>
    public static string Classify(AppEntry app)
    {
        string cacheKey = app.Key + "|" + app.Name;
        lock (ClassifyCache)
        {
            if (ClassifyCache.TryGetValue(cacheKey, out var hit)) return hit;
        }
        string id = ClassifyUncached(app);
        lock (ClassifyCache) ClassifyCache[cacheKey] = id;
        return id;
    }

    private static string ClassifyUncached(AppEntry app)
    {
        LoadRules();
        var rules = _rules!;
        // 1) 실행 파일 이름
        if (app.Exe is { } exe)
            foreach (var (id, r) in rules)
                if (r.Exe.Contains(exe)) return id;
        // 2) 스토어 앱 패키지 / AUMID 앞부분
        foreach (var (id, r) in rules)
            foreach (var prefix in r.Aumid)
                if (prefix.Length > 0 && (app.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (app.Family?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false)))
                    return id;
        // 3) 이름 낱말 (여러 낱말이면 들어 있는지, 한 낱말이면 낱말 단위로)
        string name = app.Name.ToLowerInvariant();
        var words = Words(name);
        foreach (var id in NameOrder)
            if (rules.TryGetValue(id, out var r) && r.Name.Any(w => w.Contains(' ') || !IsAsciiWord(w) ? name.Contains(w) : words.Contains(w)))
                return id;
        // 4) 시작 메뉴 폴더 이름
        if (app.StartFolder is { Length: > 0 } folder)
        {
            string fl = folder.ToLowerInvariant();
            var fw = Words(fl);
            foreach (var (id, keys) in _folders!)
                if (keys.Any(k => k.Contains(' ') || !IsAsciiWord(k) ? fl.Contains(k) : fw.Contains(k)))
                    return id;
        }
        return Other;
    }

    private static HashSet<string> Words(string s) =>
        Regex.Split(s, @"[^\p{L}\p{Nd}\.\-+]+").Where(w => w.Length > 0).ToHashSet();

    private static bool IsAsciiWord(string w) => w.All(c => c < 128);

    /// <summary>그 앱의 묶음: 사용자가 옮긴 것 → 자동 분류. 지워진 사용자 묶음을 가리키면 자동 분류.</summary>
    public static string GroupOf(AllAppsSettings s, AppEntry app)
    {
        if (s.Overrides.TryGetValue(app.Key, out var id) && GroupOrder(s).Contains(id)) return id;
        return Classify(app);
    }

    // ───────────────────────── 실행 기록 연결 ─────────────────────────

    /// <summary>실행 횟수를 세는 이름 — 같은 앱을 독(경로 핀)·판(AppsFolder)에서 실행해도 하나로.</summary>
    public static string Identity(AppEntry app) => app.Exe is { Length: > 0 } exe ? "exe:" + exe : "app:" + app.Key.ToLowerInvariant();

    /// <summary>독 핀의 실행 기록 이름 (경로 핀 = exe 이름, AUMID 핀 = 그 앱).</summary>
    public static string? Identity(PinItem pin) => pin.Kind switch
    {
        PinKind.Exe when pin.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) => "exe:" + Path.GetFileNameWithoutExtension(pin.Target).ToLowerInvariant(),
        PinKind.Aumid => Cached?.FirstOrDefault(a => a.Key.Equals(pin.Target, StringComparison.OrdinalIgnoreCase)) is { } app ? Identity(app) : "app:" + pin.Target.ToLowerInvariant(),
        _ => null,
    };
}

/// <summary>
/// 이 PC 안에서만 센 앱 실행 횟수 (★ 줄 "자주 쓰는 앱"): %APPDATA%\mongdock\usage-local.json = { 이름: [실행 날짜 yyyy-MM-dd …] } 최근 30일.
/// 밖으로 보내지 않고 설정 옮기기에도 넣지 않음. "자주 쓰는 앱으로 채우기"를 끄면 기록을 멈추고 파일을 지움.
/// </summary>
internal static class AppUsage
{
    private const int Days = 30;
    private static readonly object Gate = new();
    private static Dictionary<string, List<string>>? _data;
    private static string FilePath => Path.Combine(AppInfo.DataDirectory, "usage-local.json");

    private static Dictionary<string, List<string>> Data()
    {
        if (_data is not null) return _data;
        try
        {
            if (File.Exists(FilePath))
                _data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"실행 기록 읽기 실패: {ex.GetType().Name}");
        }
        return _data ??= new Dictionary<string, List<string>>();
    }

    public static void Record(string? identity, AllAppsSettings settings)
    {
        if (identity is null || !settings.FillFrequent) return;
        lock (Gate)
        {
            var data = Data();
            string today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (!data.TryGetValue(identity, out var days)) data[identity] = days = new List<string>();
            days.Add(today);
            Prune(data);
            Save(data);
        }
    }

    /// <summary>최근 30일 실행 횟수 많은 순.</summary>
    public static List<string> Top(int max)
    {
        lock (Gate)
        {
            var data = Data();
            Prune(data);
            return data.Where(kv => kv.Value.Count > 0).OrderByDescending(kv => kv.Value.Count).ThenByDescending(kv => kv.Value.Max())
                .Take(max).Select(kv => kv.Key).ToList();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _data = new Dictionary<string, List<string>>();
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"실행 기록 지우기 실패: {ex.GetType().Name}"); }
        }
    }

    private static void Prune(Dictionary<string, List<string>> data)
    {
        string cutoff = DateTime.Now.AddDays(-Days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var key in data.Keys.ToList())
        {
            if (data[key] is not { } days) { data.Remove(key); continue; } // 손으로 고친 파일의 null
            days.RemoveAll(d => d is null || string.CompareOrdinal(d, cutoff) < 0);
            if (days.Count == 0) data.Remove(key);
        }
    }

    private static void Save(Dictionary<string, List<string>> data)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDirectory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, FilePath, overwrite: true); // 쓰다 꺼져도 깨진 파일이 남지 않게
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"실행 기록 저장 실패: {ex.GetType().Name}");
        }
    }
}
