using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using Mongdock.Models;
using Mongdock.ViewModels;
using Mongdock.Views;

namespace Mongdock.Services;

/// <summary>
/// 쓰는 패턴을 보고 루틴 추천 (spec-routines §15, 2026-10-13 사용자 승인).
/// 기록: 앱이 첫 창을 띄운 시각과 앱 식별자만 PC 안 usage-launches.json 에 (14일·2000건, 같은 앱 30분 안 다시 켬은 한 번).
/// "자주 쓰는 앱 기록"이 꺼져 있으면 기록하지 않고 지움. 밖으로 보내지 않고 설정 옮기기에도 넣지 않음.
/// 찾기: 날마다 30분 안에 함께 켠 서로 다른 앱 3개 이상 묶음 → 14일 안 서로 다른 날 4번 이상, 시작 시각 ±1시간, 2/3 이상 겹치면 같은 묶음.
/// 묻기: 배너 10초 [만들기] [괜찮아요] — 설치 7일 안·지난 추천 7일 안·전체 화면·방해 금지·일시 정지면 안 물음. 3번 연속 거절이면 끔.
/// </summary>
internal static class RoutineSuggest
{
    private static readonly string LogPath = Path.Combine(AppInfo.DataDirectory, "usage-launches.json");
    private static readonly TimeSpan Keep = TimeSpan.FromDays(14), Window = TimeSpan.FromMinutes(30), TimeTolerance = TimeSpan.FromHours(1);
    private const int MaxEntries = 2000, MinApps = 3, MinDays = 4, DeclinesToStop = 3;

    public sealed record Launch(string Id, DateTime At);
    /// <summary>찾은 묶음: 앱 식별자, 나온 날짜들, 시작 시각 중앙값, 요일들.</summary>
    public sealed record Bundle(List<string> Apps, List<DateTime> Days, TimeSpan Time, List<DayOfWeek> Weekdays);

    private static AppServices? _services;
    private static List<Launch> _log = new();
    private static HashSet<string> _present = new();
    private static readonly Dictionary<string, DateTime> _lastSeen = new();
    private static DispatcherTimer? _timer;
    private static bool _asking;

    public static void Init(AppServices services)
    {
        _services = services;
        var s = services.Settings.Current;
        if (s.FirstRunAt is null) { s.FirstRunAt = DateTime.Now; services.Settings.Save(); } // 설치 날짜가 없던 기존 사용자는 오늘부터 7일
        _log = Load();
        _present = Current(services).ToHashSet(); // 몽독을 켜기 전에 이미 떠 있던 앱은 세지 않음
        services.Windows.WindowsChanged += (_, _) => OnWindows();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) }; // 로그인 10분 뒤, 그 뒤 2시간마다
        _timer.Tick += (_, _) => { _timer.Interval = TimeSpan.FromHours(2); Check(); };
        _timer.Start();
    }

    private static bool Recording => _services?.Settings.Current.AllApps.ShowSuggestions == true;

    /// <summary>지금 떠 있는 앱 식별자들 (독과 같은 창 목록).</summary>
    private static IEnumerable<string> Current(AppServices services) =>
        services.Windows.Windows.Select(IdOf).OfType<string>().Distinct();

    /// <summary>창 → 실행 기록과 같은 식별자 ("exe:이름" / 스토어 앱 "app:aumid").</summary>
    internal static string? IdOf(AppWindowInfo w)
    {
        string exe = Path.GetFileNameWithoutExtension(w.ProcessPath).ToLowerInvariant();
        if (exe is "applicationframehost" or "" && !string.IsNullOrEmpty(w.Aumid)) return "app:" + w.Aumid!.ToLowerInvariant();
        return exe.Length == 0 || exe == "mongdock" ? null : "exe:" + exe;
    }

    // ── 브라우저 페이지 힌트 (§15-1): 브라우저 창 제목(페이지 제목)을 메모리에서만 셈 — 파일에 저장하지 않음, 방문 기록·주소창은 읽지 않음 ──

    private static readonly HashSet<string> BrowserExes = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "whale", "brave", "firefox", "opera", "vivaldi" };
    /// <summary>브라우저 exe → (깨끗한 제목 → 본 횟수). 같은 창의 같은 제목은 한 번만.</summary>
    private static readonly Dictionary<string, Dictionary<string, int>> _titles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<(IntPtr, string)> _titleSeen = new();

    private static void CountBrowserTitles(AppServices services)
    {
        if (!Recording) return;
        foreach (var w in services.Windows.Windows)
        {
            string exe = Path.GetFileNameWithoutExtension(w.ProcessPath);
            if (!BrowserExes.Contains(exe) || CleanTitle(w.Title) is not { } title) continue;
            if (!_titleSeen.Add((w.Hwnd, title))) continue;
            if (_titleSeen.Count > 5000) _titleSeen.Clear();
            if (!_titles.TryGetValue(exe, out var counts)) _titles[exe] = counts = new(StringComparer.Ordinal);
            counts[title] = counts.GetValueOrDefault(title) + 1;
            if (counts.Count > 300) foreach (var k in counts.OrderBy(c => c.Value).Take(100).Select(c => c.Key).ToList()) counts.Remove(k);
        }
    }

    /// <summary>창 제목 → 페이지 이름: 브라우저 꼬리("- Google Chrome" 등)와 알림 수("(3)")를 지우고 40자.</summary>
    internal static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string t = System.Text.RegularExpressions.Regex.Replace(title.Trim(), @"^\(\d+\+?\)\s*", "");
        foreach (var tail in new[] { "Google Chrome", "Microsoft Edge", "Microsoft​ Edge", "Whale", "Naver Whale", "Brave", "Mozilla Firefox", "Firefox", "Opera", "Vivaldi" })
        {
            int at = Math.Max(t.LastIndexOf(" - " + tail, StringComparison.OrdinalIgnoreCase), t.LastIndexOf(" — " + tail, StringComparison.OrdinalIgnoreCase));
            if (at > 0) { t = t[..at]; break; }
        }
        // 엣지 프로필 꼬리 "- 개인" "- 회사" 같은 마지막 한 토막은 그대로 둠 (페이지 이름일 수도 있어서)
        t = t.Trim();
        if (t.Length == 0 || t is "새 탭" or "New Tab" or "새 탭 - 개인") return null;
        return t.Length > 40 ? t[..40] + "…" : t;
    }

    /// <summary>그 브라우저에서 자주 보인 페이지 이름 최대 3개 (없으면 빈 목록 — 몽독을 다시 켜면 그 뒤 본 것만).</summary>
    internal static List<string> TopTitles(string browserExe) =>
        _titles.TryGetValue(browserExe, out var counts) ? counts.Where(c => c.Value >= 2).OrderByDescending(c => c.Value).Take(3).Select(c => c.Key).ToList() : new();

    private static void OnWindows()
    {
        var services = _services;
        if (services is null) return;
        CountBrowserTitles(services);
        var now = Current(services).ToHashSet();
        var added = now.Where(id => !_present.Contains(id)).ToList();
        _present = now;
        if (added.Count == 0 || !Recording) return;
        if (Environment.TickCount64 < TimeSpan.FromMinutes(3).TotalMilliseconds) return; // 로그인 직후 3분 = 시작 프로그램
        var t = DateTime.Now;
        bool changed = false;
        foreach (var id in added)
        {
            if (_lastSeen.TryGetValue(id, out var last) && t - last < Window) continue; // 30분 안 다시 켬은 한 번
            _lastSeen[id] = t;
            _log.Add(new Launch(id, t));
            changed = true;
        }
        if (changed) Save();
    }

    private static List<Launch> Load()
    {
        try
        {
            if (!File.Exists(LogPath)) return new();
            var list = JsonSerializer.Deserialize<List<Launch>>(File.ReadAllText(LogPath)) ?? new();
            var since = DateTime.Now - Keep;
            return list.Where(l => l.At >= since).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private static void Save()
    {
        var since = DateTime.Now - Keep;
        _log = _log.Where(l => l.At >= since).TakeLast(MaxEntries).ToList();
        try { AtomicFile.WriteAllText(LogPath, JsonSerializer.Serialize(_log)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>"자주 쓰는 앱 기록"을 끄면: 실행 시각 기록도 지움.</summary>
    public static void Clear()
    {
        _log.Clear();
        _lastSeen.Clear();
        try { if (File.Exists(LogPath)) File.Delete(LogPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ───────────────────────── 찾기 (순수 계산 — report-test 판정표) ─────────────────────────

    /// <summary>2/3 이상 겹침 (작은 쪽이 아니라 큰 쪽 기준 — 더 엄격).</summary>
    internal static bool Same(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b) =>
        a.Count > 0 && b.Count > 0 && a.Intersect(b).Count() * 3 >= Math.Max(a.Count, b.Count) * 2;

    /// <summary>
    /// 가장 뚜렷한 묶음 하나 (없으면 null). startup = 시작 프로그램 앱 (뺌), routines = 기존 루틴 앱들, declined = 거절한 묶음들.
    /// </summary>
    internal static Bundle? Find(IReadOnlyList<Launch> launches, DateTime now, ISet<string> startup,
        IEnumerable<IReadOnlyCollection<string>> routines, IEnumerable<IReadOnlyCollection<string>> declined)
    {
        var recent = launches.Where(l => l.At >= now - Keep && l.At <= now && !startup.Contains(l.Id)).OrderBy(l => l.At).ToList();
        // 날마다 30분 창 묶음 (서로 다른 앱 3개 이상)
        var occ = new List<(DateTime Day, DateTime Start, HashSet<string> Apps)>();
        foreach (var day in recent.GroupBy(l => l.At.Date))
        {
            var list = day.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var set = list.Skip(i).TakeWhile(l => l.At - list[i].At <= Window).Select(l => l.Id).ToHashSet();
                if (set.Count >= MinApps) occ.Add((day.Key, list[i].At, set));
            }
        }
        var routineSets = routines.ToList();
        var declinedSets = declined.ToList();
        Bundle? best = null;
        foreach (var seed in occ)
        {
            // 날마다 seed 와 같은 묶음·비슷한 시각 중 가장 잘 맞는 것 하나
            var matches = occ.Where(o => Same(o.Apps, seed.Apps) && Math.Abs((o.Start.TimeOfDay - seed.Start.TimeOfDay).TotalMinutes) <= TimeTolerance.TotalMinutes)
                .GroupBy(o => o.Day)
                .Select(g => g.OrderByDescending(o => o.Apps.Intersect(seed.Apps).Count()).First())
                .ToList();
            if (matches.Count < MinDays) continue;
            var core = matches.SelectMany(m => m.Apps).GroupBy(id => id)
                .Where(g => g.Count() * 4 >= matches.Count * 3).Select(g => g.Key).OrderBy(id => id).ToList();
            if (core.Count < MinApps) continue;
            if (routineSets.Any(r => Same(r, core)) || declinedSets.Any(d => Same(d, core))) continue;
            var times = matches.Select(m => m.Start.TimeOfDay).OrderBy(t => t).ToList();
            var bundle = new Bundle(core, matches.Select(m => m.Day).OrderBy(d => d).ToList(), times[times.Count / 2],
                matches.Select(m => m.Day.DayOfWeek).Distinct().OrderBy(d => d).ToList());
            if (best is null || bundle.Days.Count > best.Days.Count || bundle.Days.Count == best.Days.Count && bundle.Apps.Count > best.Apps.Count)
                best = bundle;
        }
        return best;
    }

    /// <summary>요일 묶음 → 시작 조건 요일 (평일·주말·매일=null·그 밖은 본 요일).</summary>
    internal static List<int>? StartDays(Bundle b)
    {
        var d = b.Weekdays.Select(x => (int)x).ToHashSet();
        if (d.All(x => x is >= 1 and <= 5)) return d.Count >= 3 ? new List<int> { 1, 2, 3, 4, 5 } : d.OrderBy(x => x).ToList();
        if (d.All(x => x is 0 or 6)) return new List<int> { 0, 6 };
        return d.Count >= 5 ? null : d.OrderBy(x => x).ToList();
    }

    internal static string WhenText(Bundle b)
    {
        var days = StartDays(b);
        string day = days is null ? Loc.T("매일")
            : days.SequenceEqual(new[] { 1, 2, 3, 4, 5 }) ? Loc.T("평일")
            : days.SequenceEqual(new[] { 0, 6 }) ? Loc.T("주말")
            : string.Join("·", days.Select(x => Loc.Culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)x)));
        int h = b.Time.Hours;
        string part = h < 11 ? Loc.T("아침") : h < 14 ? Loc.T("점심") : h < 18 ? Loc.T("오후") : Loc.T("저녁");
        return Loc.F($"{day} {part}");
    }

    // ───────────────────────── 시작 프로그램 ─────────────────────────

    /// <summary>윈도우 시작 프로그램(Run 키·시작 폴더)의 exe 식별자 — 늘 켜지는 앱이 묶음에 끼지 않게.</summary>
    internal static HashSet<string> StartupApps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void FromRun(RegistryKey root, string path)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) return;
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name) is string cmd && ExeFromCommand(cmd) is { } exe) set.Add("exe:" + exe);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        FromRun(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run");
        FromRun(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run");
        FromRun(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            try
            {
                string dir = Environment.GetFolderPath(folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
                    if (PinFactory.ShortcutTarget(lnk) is { Length: > 0 } t) set.Add("exe:" + Path.GetFileNameWithoutExtension(t).ToLowerInvariant());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return set;
    }

    private static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        string path = cmd.StartsWith('"') ? cmd[1..].Split('"')[0] : cmd.Split(' ')[0];
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(path).ToLowerInvariant() : null;
    }

    // ───────────────────────── 묻기 ─────────────────────────

    private static void Check()
    {
        var services = _services;
        if (services is null || _asking) return;
        var s = services.Settings.Current;
        if (!s.RoutineSuggest || !s.AllApps.ShowSuggestions || AppState.Paused) return;
        if (s.FirstRunAt is { } first && DateTime.Now - first < TimeSpan.FromDays(7)) return;
        if (s.RoutineSuggestLastAt is { } lastAt && DateTime.Now - lastAt < TimeSpan.FromDays(7)) return;
        if (!RoutineUi.CanAdd(services) || RoutineTriggers.IsBusy()) return;
        var apps = AllAppsCatalog.Cached;
        if (apps is null) return;
        var routines = s.Routines.Select(r => (IReadOnlyCollection<string>)r.Items.Where(i => i.Kind == RoutineItemKind.App)
            .Select(i => !string.IsNullOrEmpty(i.Aumid) ? "app:" + i.Aumid!.ToLowerInvariant() : "exe:" + Path.GetFileNameWithoutExtension(i.Target).ToLowerInvariant()).ToList()).ToList();
        var bundle = Find(_log, DateTime.Now, StartupApps(), routines, s.RoutineSuggestDeclined.Select(d => (IReadOnlyCollection<string>)d));
        if (bundle is null) return;
        // 식별자 → 앱 모음 항목 (못 찾는 앱은 뺌 — 그래도 3개 이상일 때만)
        var entries = bundle.Apps.Select(id => apps.FirstOrDefault(a => AllAppsCatalog.Identities(a).Contains(id))).OfType<AppEntry>().ToList();
        if (entries.Count < MinApps) return;
        Ask(services, bundle, entries);
    }

    private static void Ask(AppServices services, Bundle bundle, List<AppEntry> entries)
    {
        var s = services.Settings.Current;
        string names = string.Join("·", entries.Take(4).Select(e => e.Name)) + (entries.Count > 4 ? Loc.F($" 외 {entries.Count - 4}개") : "");
        string when = WhenText(bundle);
        var item = new NotificationItem(0, "", AppInfo.Name, Loc.T("루틴으로 만들어 둘까요?"),
            new[] { Loc.F($"{when}마다 {names}을(를) 같이 여시네요.") }, DateTime.Now, null, null, false, null);
        System.Windows.Media.ImageSource? icon = null;
        try { icon = MacIconRenderer.AllApps(); } catch { /* 아이콘 없이 */ }
        _asking = true;
        var buttons = new List<NotificationBannerWindow.BannerButton>
        {
            new(Loc.T("만들기"), () => { _asking = false; Create(services, bundle, entries); }, Primary: true),
            new(Loc.T("괜찮아요"), () => { _asking = false; Decline(services, bundle); }),
        };
        if (!NotificationBannerWindow.ShowQuestion(item, icon, buttons, TimeSpan.FromSeconds(10), () => _asking = false))
        {
            _asking = false;
            return;
        }
        s.RoutineSuggestLastAt = DateTime.Now;
        s.RoutineSuggestShownSinceSignal++;
        services.Settings.Save();
        Log.Info($"루틴 추천: 앱 {entries.Count}개, {bundle.Days.Count}일, {bundle.Time:hh\\:mm}");
    }

    private static void Create(AppServices services, Bundle bundle, List<AppEntry> entries)
    {
        var s = services.Settings.Current;
        s.RoutineSuggestDeclines = 0;
        s.RoutineSuggestCreatedSinceSignal++;
        services.Settings.Save();
        var t = new TimeSpan(bundle.Time.Hours, bundle.Time.Minutes / 5 * 5, 0);
        var r = new RoutineDef
        {
            Name = Loc.F($"{WhenText(bundle)} 루틴"), // "평일 아침 루틴"
            Items = entries.Select(e => RoutineUi.ItemFromApp(services, e)).ToList(),
        };
        (r.More ??= new RoutineMore()).Start.Add(new RoutineStart { Kind = RoutineStartKind.Time, Time = t.ToString(@"hh\:mm"), Days = StartDays(bundle) }); // 묻기만 (AutoOpen 끔)
        // 브라우저 페이지 힌트 (§15-1): 묶음에 브라우저가 있으면 자주 보인 페이지 이름 최대 3개
        string? browserExe = bundle.Apps.Select(id => id.StartsWith("exe:") ? id[4..] : "").FirstOrDefault(BrowserExes.Contains);
        var pages = browserExe is null ? new List<string>() : TopTitles(browserExe);
        string? browserName = browserExe is null ? null : entries.FirstOrDefault(e => string.Equals(e.Exe, browserExe, StringComparison.OrdinalIgnoreCase))?.Name ?? browserExe;
        Log.Info($"루틴 추천 → 만들기 (편집 창, 페이지 힌트 {pages.Count}개)");
        RoutineEditorWindow.OpenPrefilled(services, r, pages.Count > 0 ? (browserName!, pages) : null);
    }

    private static void Decline(AppServices services, Bundle bundle)
    {
        var s = services.Settings.Current;
        s.RoutineSuggestDeclined.Add(bundle.Apps.ToList());
        s.RoutineSuggestDeclines++;
        if (s.RoutineSuggestDeclines >= DeclinesToStop)
        {
            s.RoutineSuggest = false;
            Log.Info("루틴 추천: 3번 연속 거절 — 끔");
            try
            {
                NotificationBannerWindow.ShowCustom(new NotificationItem(0, "", AppInfo.Name, Loc.T("루틴 추천을 껐어요"),
                    new[] { Loc.T("설정 → 독 → 루틴에서 다시 켤 수 있어요.") }, DateTime.Now, null, null, false, null), null, () => { });
            }
            catch (Exception ex) { Log.Warn($"루틴 추천 끔 안내 실패: {ex.GetType().Name}"); }
        }
        else Log.Info("루틴 추천: 괜찮아요 (이 묶음은 다시 묻지 않음)");
        services.Settings.Save();
    }
}
