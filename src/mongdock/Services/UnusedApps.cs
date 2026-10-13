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

    public sealed record Row(AppEntry App, DateTime? Last, long? Bytes);

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
        foreach (var (app, last) in candidates)
        {
            if (IsBuiltIn(app, packages)) continue;
            if (AllAppsCatalog.Identities(app).Any(startup.Contains)) continue;
            DateTime? installed = null;
            long? bytes = null;
            if (app.Family is { Length: > 0 } fam && packages.TryGetValue(fam, out var pkg))
            {
                installed = pkg.Installed;
                bytes = PackageBytes(pkg.Path);
            }
            else if (Match(app, uninstall) is { } u)
            {
                installed = u.Installed;
                bytes = u.Bytes;
            }
            installed ??= ShortcutDate(app);
            if (installed is { } d ? now - d < TimeSpan.FromDays(NewInstallDays) : youngRecords) continue;
            rows.Add(new Row(app, last, bytes));
        }
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

    internal sealed record Pkg(DateTime? Installed, string? Path, bool Inbox);

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
                    map[fam] = new Pkg(installed, path, inbox);
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
