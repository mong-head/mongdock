using System.IO;
using Microsoft.Win32;

namespace Mongdock.Services;

/// <summary>
/// 앱 모음 "모든 앱"의 [안 쓰는 앱] 보기 (spec-all-apps-panel.md 마지막 절, 2026-10-13 사용자 승인).
/// 60일 넘게 안 썼거나 기록이 없는 앱 — 윈도우 기본 도구·설치 30일 안·실행 중·시작 프로그램은 뺌.
/// 설치 날짜·용량은 제거 프로그램 레지스트리(InstallDate·EstimatedSize)와 스토어 패키지(설치 날짜·폴더 크기)에서, 모르면 바로 가기 만든 날짜.
/// 무거운 부분(레지스트리·패키지·폴더 크기·시작 프로그램)은 백그라운드에서. PC 안에서만 읽고 밖으로 보내지 않음.
/// </summary>
internal static class UnusedApps
{
    public const int StaleDays = 60, NewInstallDays = 30;

    /// <summary>
    /// 잠자는 앱 한 줄. Bundle = 같은 설치 묶음(제거 프로그램 항목·스토어 패키지 하나)에 앱이 둘 이상이면 그 묶음 키와 이름 —
    /// 크기는 묶음 전체라 "1.8 GB · 한컴오피스 2020 전체"로 보이고, 선택 합계에서는 묶음마다 한 번만 더함 (2026-10-13 사용자: 한글·한셀·한쇼가 모두 1.8 GB).
    /// </summary>
    public sealed record Row(AppEntry App, DateTime? Last, long? Bytes, string? BundleKey = null, string? BundleName = null)
    {
        /// <summary>크기를 한 번만 셀 단위 (묶음이면 묶음, 아니면 앱).</summary>
        public string SizeKey => BundleKey ?? "app:" + App.Key;
    }

    /// <summary>고른 줄들의 크기 합 — 같은 묶음은 한 번만.</summary>
    public static long? TotalBytes(IEnumerable<Row> rows)
    {
        long sum = 0;
        bool any = false;
        foreach (var g in rows.GroupBy(r => r.SizeKey, StringComparer.OrdinalIgnoreCase))
            if (g.First().Bytes is { } b) { sum += b; any = true; }
        return any ? sum : null;
    }

    /// <summary>줄의 크기 글자: 묶음이면 "1.8 GB · 묶음 이름 전체".</summary>
    public static string RowSizeText(Row r)
    {
        string size = SizeText(r.Bytes);
        if (r.BundleKey is null || size.Length == 0) return size;
        return r.BundleName is { Length: > 0 } n ? Loc.F($"{size} · {n} 전체") : Loc.F($"{size} · 묶음 전체");
    }

    // ── 미리 세기 (2026-10-13 사용자: "계산 중 …" 없이 판을 열면 숫자가 바로) ──

    /// <summary>마지막으로 센 잠자는 앱 목록과 그 시각 (몽독 시작 3분 뒤, 그 뒤 하루 한 번, 판에서 다시 센 것도).</summary>
    public static (List<Row> Rows, DateTime At)? Latest { get; private set; }
    /// <summary>새로 세었음 (판이 열려 있으면 숫자를 흐려지며 바꿈).</summary>
    public static event Action? Counted;
    private static System.Windows.Threading.DispatcherTimer? _timer;
    private static bool _counting;

    public static void StartCounting(AppServices services)
    {
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _timer.Tick += (_, _) => { _timer.Interval = TimeSpan.FromHours(24); CountInBackground(services); };
        _timer.Start();
    }

    /// <summary>UI 스레드에서 부름: 실행 중 앱·숨긴 앱·기록 시작일을 잡고 나머지는 백그라운드. 기록이 꺼져 있으면 세지 않음.</summary>
    public static void CountInBackground(AppServices services, Action<List<Row>>? done = null)
    {
        var s = services.Settings.Current;
        if (_counting || !s.AllApps.ShowSuggestions) return;
        var running = services.Windows.Windows.Select(w => Path.GetFileNameWithoutExtension(w.ProcessPath).ToLowerInvariant())
            .Where(e => e.Length > 0).Select(e => "exe:" + e).ToHashSet();
        var hidden = s.AllApps.Hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        DateTime? recordStart = s.AllApps.UsageSeededAt ?? s.FirstRunAt;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _counting = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Task.Run(() =>
        {
            var cutoff = DateTime.Now.AddDays(-StaleDays);
            var candidates = AllAppsCatalog.Apps()
                .Where(a => !hidden.Contains(a.Key))
                .Select(a => (App: a, Last: AppUsage.LastUsed(AllAppsCatalog.Identities(a))))
                .Where(x => (x.Last is not { } l || l < cutoff) && !AllAppsCatalog.Identities(x.App).Any(running.Contains))
                .ToList();
            return Find(candidates, recordStart);
        }).ContinueWith(t => dispatcher.BeginInvoke(() =>
        {
            _counting = false;
            if (t.Status != TaskStatus.RanToCompletion) { Log.Warn($"잠자는 앱 세기 실패: {t.Exception?.InnerException?.GetType().Name}"); return; }
            Latest = (t.Result, DateTime.Now);
            Log.Info($"잠자는 앱 세기: {t.Result.Count}개 ({sw.ElapsedMilliseconds}ms)");
            done?.Invoke(t.Result);
            Counted?.Invoke();
        }), TaskScheduler.Default);
    }

    /// <summary>기록을 끄면 센 것도 버림.</summary>
    public static void Forget() => Latest = null;

    private sealed record Uninstall(string Name, string? Location, string? Icon, DateTime? Installed, long? Bytes);

    /// <summary>
    /// 후보(마지막 사용이 60일 넘었거나 없음, 실행 중 아님 — UI 스레드에서 거른 것) → 기본 도구·시작 프로그램·새로 설치한 앱을 빼고 용량을 붙임.
    /// recordStart = 몽독 기록이 쌓이기 시작한 때 (설치 날짜를 모를 때 30일 기준).
    /// </summary>
    public static List<Row> Find(IReadOnlyList<(AppEntry App, DateTime? Last)> candidates, DateTime? recordStart)
    {
        var startup = SafeStartup();
        var uninstall = ReadUninstall();
        var packages = ReadPackages();
        var now = DateTime.Now;
        bool youngRecords = recordStart is not { } rs || now - rs < TimeSpan.FromDays(NewInstallDays);
        var rows = new List<Row>();
        var bundles = new List<(string Key, string? Name)?>(); // rows 와 같은 순서 — 크기를 가져온 설치 묶음
        foreach (var (app, last) in candidates)
        {
            if (IsBuiltIn(app, packages)) continue;
            if (AllAppsCatalog.Identities(app).Any(startup.Contains)) continue;
            DateTime? installed = null;
            long? bytes = null;
            (string Key, string? Name)? bundle = null;
            if (app.Family is { Length: > 0 } fam && packages.TryGetValue(fam, out var pkg))
            {
                installed = pkg.Installed;
                bytes = PackageBytes(pkg.Path);
                bundle = ("pkg:" + fam, pkg.Name);
            }
            else if (Match(app, uninstall) is { } u)
            {
                installed = u.Installed;
                bytes = u.Bytes;
                bundle = ("uninstall:" + uninstall.IndexOf(u), u.Name);
            }
            installed ??= ShortcutDate(app);
            if (installed is { } d ? now - d < TimeSpan.FromDays(NewInstallDays) : youngRecords) continue;
            rows.Add(new Row(app, last, bytes));
            bundles.Add(bundle);
        }
        // 같은 설치 묶음에서 크기를 가져온 앱이 둘 이상이면 묶음 표시 (크기는 묶음 전체라 앱마다 같음)
        var shared = bundles.OfType<(string Key, string? Name)>().GroupBy(b => b.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        for (int i = 0; i < rows.Count; i++)
            if (bundles[i] is { } b && shared.Contains(b.Key)) rows[i] = rows[i] with { BundleKey = b.Key, BundleName = b.Name };
        return rows;
    }

    private static HashSet<string> SafeStartup()
    {
        try { return RoutineSuggest.StartupApps(); }
        catch (Exception ex) { Log.Warn($"안 쓰는 앱: 시작 프로그램 읽기 실패 {ex.GetType().Name}"); return new(); }
    }

    // ── 윈도우 기본 도구 ──

    private static readonly string[] SystemFolders =
    {
        "Windows Tools", "Windows 도구", "Windows Accessories", "보조프로그램", "Windows System", "Windows 시스템", "Accessibility", "접근성",
        "Windows PowerShell", "Administrative Tools", "관리 도구", "System Tools", "시스템 도구",
    };

    /// <summary>
    /// 윈도우가 깔아 둔 앱·도구 (즐겨찾기 추천의 윈도우 기록에서 뺌 — 2026-10-13 사용자: 캡처 도구·Game Bar·Store·돋보기가 "자주 쓴 앱"으로 뜸.
    /// 단축키나 윈도우가 저절로 연 것도 기록에 남아서). 기본 도구 규칙 + 마이크로소프트가 낸 스토어 앱 + 윈도우 폴더 안 프로그램.
    /// </summary>
    internal static bool IsSystemApp(AppEntry app)
    {
        if (IsBuiltIn(app)) return true;
        string fam = app.Family ?? (app.Key.Contains('!') ? app.Key[..app.Key.IndexOf('!')] : "");
        if (fam.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || fam.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
            || fam.StartsWith("windows.", StringComparison.OrdinalIgnoreCase)) return true;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return app.Exe is { Length: > 0 } && app.TargetPath is null && app.Key.StartsWith("{1AC14E77", StringComparison.OrdinalIgnoreCase) // System32 알려진 폴더
               || app.Key.StartsWith(win, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBuiltIn(AppEntry app, IReadOnlyDictionary<string, Pkg>? packages = null)
    {
        if (app.Key.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) || app.Key.StartsWith("windows.", StringComparison.OrdinalIgnoreCase)) return true;
        if (app.Family is { Length: > 0 } fam)
        {
            if (fam.StartsWith("Microsoft.Windows", StringComparison.OrdinalIgnoreCase) || fam.StartsWith("windows.", StringComparison.OrdinalIgnoreCase)) return true;
            if (packages is not null && packages.TryGetValue(fam, out var p) && p.Inbox) return true;
        }
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (app.TargetPath is { Length: > 0 } t && t.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (app.StartFolder is { Length: > 0 } f && SystemFolders.Any(s => f.Contains(s, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    // ── 제거 프로그램 레지스트리 ──

    private static List<Uninstall> ReadUninstall()
    {
        var list = new List<Uninstall>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is not string display || display.Length == 0) continue;
                    DateTime? installed = k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
                    long? bytes = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
                    string? icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim().Trim('"');
                    list.Add(new Uninstall(display, (k.GetValue("InstallLocation") as string)?.Trim().Trim('"').TrimEnd('\\'), icon, installed, bytes));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return list;
    }

    private static Uninstall? Match(AppEntry app, List<Uninstall> list)
    {
        string? exe = app.TargetPath;
        if (exe is { Length: > 0 })
        {
            var byIcon = list.FirstOrDefault(u => u.Icon is { Length: > 0 } i && string.Equals(i, exe, StringComparison.OrdinalIgnoreCase));
            if (byIcon is not null) return byIcon;
            var byDir = list.Where(u => u.Location is { Length: > 3 } l && exe.StartsWith(l + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(u => u.Location!.Length).FirstOrDefault();
            if (byDir is not null) return byDir;
        }
        return list.FirstOrDefault(u => string.Equals(u.Name, app.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    private static DateTime? ShortcutDate(AppEntry app)
    {
        try { return app.Shortcut is { Length: > 0 } s && File.Exists(s) ? File.GetCreationTime(s) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ── 스토어 패키지 ──

    internal sealed record Pkg(DateTime? Installed, string? Path, bool Inbox, string? Name = null);

    private static Dictionary<string, Pkg> ReadPackages()
    {
        var map = new Dictionary<string, Pkg>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var p in pm.FindPackagesForUser(""))
            {
                try
                {
                    if (p.IsFramework || p.IsResourcePackage) continue;
                    string fam = p.Id.FamilyName;
                    if (map.ContainsKey(fam)) continue;
                    bool inbox = p.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System
                                 || p.Id.Publisher.Contains("CN=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) && p.Id.Name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
                    DateTime? installed = null;
                    try { installed = p.InstalledDate.LocalDateTime; } catch { /* 일부 패키지는 날짜 없음 */ }
                    string? path = null;
                    try { path = p.InstalledLocation?.Path; } catch { /* 위치 못 읽음 */ }
                    string? name = null;
                    try { name = p.DisplayName; } catch { /* 이름 못 읽음 */ }
                    map[fam] = new Pkg(installed, path, inbox, name);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or FileNotFoundException) { }
            }
        }
        catch (Exception ex) { Log.Warn($"안 쓰는 앱: 스토어 패키지 목록 실패 {ex.GetType().Name}"); }
        return map;
    }

    private static readonly Dictionary<string, (long Bytes, DateTime At)> _sizeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>패키지 폴더 크기 (하루 캐시). 못 읽으면 null.</summary>
    private static long? PackageBytes(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
        lock (_sizeCache)
            if (_sizeCache.TryGetValue(path, out var c) && DateTime.Now - c.At < TimeSpan.FromDays(1)) return c.Bytes;
        long sum = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                try { sum += new FileInfo(f).Length; } catch (IOException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        lock (_sizeCache) _sizeCache[path] = (sum, DateTime.Now);
        return sum;
    }

    // ── 글 ──

    public static string SizeText(long? bytes) => bytes is not { } b ? "" :
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{Math.Max(1, b >> 10)} KB";
}
