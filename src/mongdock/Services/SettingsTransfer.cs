using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 설정 옮기기 (#22): 설정 → 일반 → "설정 내보내기…/가져오기…". 파일 하나(.mongdock = zip):
/// - manifest.json: 형식·앱 버전·만든 시각·개수·캘린더 주소 포함 여부
/// - settings.json: 지금 설정. 독 아이콘 경로(%APPDATA%\mongdock\icons\…)는 "{icons}\파일" 로 바꿔 넣음 (PC 마다 사용자 폴더가 다름)
/// - icons/…: 독 핀이 쓰는 아이콘 파일만
/// - calendars.json: 이름·색·켜기 (+ 사용자가 고르면 주소). calendars.json 원본은 DPAPI 암호화라 다른 PC 에선 못 풀어서 그대로 넣지 않음.
/// 가져오기: 미리 보기(<see cref="ReadPreview"/>) → 확인 → 지금 설정 백업(*.bak-import-날짜) → 적용. 이 PC 전용 값은 SettingsService.ApplyImported 가 유지.
/// 스토어판·일반판 공통 (%APPDATA%\mongdock).
/// </summary>
public static class SettingsTransfer
{
    public const string Extension = ".mongdock";
    private const string Format = "mongdock-settings";
    private const int FormatVersion = 1;
    private const string IconsToken = "{icons}";

    /// <summary>icons 아래에서 하위 폴더째 옮기는 곳 (루틴·독 폴더 그림). 그 밖은 파일 이름만.</summary>
    private static readonly HashSet<string> IconSubfolders = new(StringComparer.OrdinalIgnoreCase) { IconFiles.Routines, IconFiles.Folders };

    private static string IconRelative(string iconsDir, string full)
    {
        string rel = Path.GetRelativePath(iconsDir, full);
        var parts = rel.Split('\\', '/');
        return parts.Length == 2 && IconSubfolders.Contains(parts[0]) ? parts[0] + "\\" + parts[1] : Path.GetFileName(full);
    }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public sealed record CalendarEntry(string Name, string Color, bool Enabled, string? Url);

    public sealed record Manifest(string Format, int FormatVersion, string AppVersion, DateTime Created, bool IncludesCalendarUrls,
        int PinCount, int CalendarCount, int IconCount);

    /// <summary>가져오기 전 미리 보기 (파일 안 내용 요약).</summary>
    public sealed record Preview(Manifest Manifest, IReadOnlyList<string> PinNames, IReadOnlyList<CalendarEntry> Calendars);

    public sealed record ImportResult(int Pins, int Icons, int CalendarsAdded, IReadOnlyList<string> CalendarsToReconnect, string BackupSuffix);

    // ───────────────────────── 내보내기 ─────────────────────────

    /// <summary>
    /// 내보내기 준비: 지금 설정·캘린더 목록의 사본을 뜸 (UI 스레드 — 설정·구독 목록을 바꾸는 쪽과 같은 스레드).
    /// 돌려준 함수가 파일을 씀 (백그라운드에서 불러도 됨).
    /// </summary>
    public static Func<Manifest> PrepareExport(string path, SettingsService settings, ICalendarFeedService calendars, bool includeCalendarUrls)
    {
        string iconsDir = Path.GetFullPath(settings.IconsDirectory);
        var s = SettingsService.ParseForImport(settings.ExportJson()); // 지금 설정의 사본
        var icons = new List<string>();
        s.NewSince.Clear(); // NEW 배지 기록은 이 PC 것 — 옮기지 않음
        s.NewSeen.Clear();
        s.AllApps.DismissedSuggestions.Clear(); // 추천 거절도
        // 실행 기록에서 나온 자동 폴더 내용과 기록 날짜도 — 사용 기록은 이 PC 밖으로 안 나감
        s.AllApps.UsageSeededAt = null;
        s.AllApps.AutoFoldersDay = null;
        s.AllApps.CleanupPromptMonth = null;
        foreach (var g in s.AllApps.Groups) g.AutoApps = null;
        // 루틴의 웹 주소·파일 경로는 캘린더 주소와 같은 선택을 따름 — 빼면 앱 항목은 앱만, 웹·파일 항목은 빠짐
        // (실행 옵션은 경로·주소가 든 것만 뺌 — Update.exe --processStart 같은 실행 인자는 있어야 앱이 켜짐)
        if (!includeCalendarUrls)
            foreach (var r in s.Routines)
            {
                r.Items.RemoveAll(i => i.Kind != RoutineItemKind.App);
                foreach (var i in r.Items)
                {
                    i.Open = null;
                    if (i.Args is { } a && (a.Contains("://") || a.Contains(":\\") || a.Contains("\\\\"))) i.Args = null;
                }
            }
        // 핀 아이콘(IconPath)과 아이콘 바꾸기로 고른 그림(Icon.File) — 몽독 아이콘 폴더 안 것만 묶어 넣고 경로는 토큰으로
        string? Pack(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            string full;
            try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
            catch { return path; }
            if (!full.StartsWith(iconsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return path;
            if (!icons.Contains(full, StringComparer.OrdinalIgnoreCase)) icons.Add(full);
            return IconsToken + "\\" + IconRelative(iconsDir, full);
        }
        foreach (var pin in s.Pins)
        {
            pin.IconPath = Pack(pin.IconPath);
            if (pin.Icon is { File: not null } icon) icon.File = Pack(icon.File);
        }
        foreach (var r in s.Routines)
            if (r.Icon is { File: not null } icon) icon.File = Pack(icon.File); // 루틴 아이콘 그림도 같이
        var cals = calendars.Feeds.Select(f => new CalendarEntry(f.Name, f.Color, f.Enabled, includeCalendarUrls && f.Url.Length > 0 ? f.Url : null)).ToList();
        var manifest = new Manifest(Format, FormatVersion, ReportService.AppVersion(), DateTime.Now, includeCalendarUrls && cals.Any(c => c.Url is not null),
            s.Pins.Count, cals.Count, icons.Count);
        string settingsJson = JsonSerializer.Serialize(s, Json), calsJson = JsonSerializer.Serialize(cals, Json);

        return () =>
        {
            string tmp = path + ".tmp";
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    WriteEntry(zip, "manifest.json", JsonSerializer.Serialize(manifest, Json));
                    WriteEntry(zip, "settings.json", settingsJson);
                    WriteEntry(zip, "calendars.json", calsJson);
                    foreach (string icon in icons) zip.CreateEntryFromFile(icon, "icons/" + IconRelative(iconsDir, icon).Replace('\\', '/'));
                }
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { /* 지우기 실패는 무시 */ }
                throw;
            }
            Log.Info($"설정 내보내기: 독 앱 {manifest.PinCount}개, 아이콘 {manifest.IconCount}개, 캘린더 {manifest.CalendarCount}개 (주소 {(manifest.IncludesCalendarUrls ? "포함" : "뺌")})");
            return manifest;
        };
    }

    /// <summary>가져올 수 있는 아이콘 파일 (그림만, 하나에 5MB 까지).</summary>
    private static readonly HashSet<string> IconExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
    private const long MaxIconBytes = 5 * 1024 * 1024;

    // ───────────────────────── 가져오기 ─────────────────────────

    /// <summary>몽독 설정 파일이 아니거나 손상이면 예외 (메시지는 사용자에게 보여도 되는 한국어).</summary>
    public static Preview ReadPreview(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifest = ReadJson<Manifest>(zip, "manifest.json") ?? throw new InvalidDataException(Loc.T("몽독 설정 파일이 아니에요."));
        if (manifest.Format != Format) throw new InvalidDataException(Loc.T("몽독 설정 파일이 아니에요."));
        if (manifest.FormatVersion > FormatVersion) throw new InvalidDataException(Loc.F($"더 새 몽독(v{manifest.AppVersion})에서 만든 파일이에요. 몽독을 업데이트한 뒤 가져와 주세요."));
        string settingsJson = ReadText(zip, "settings.json") ?? throw new InvalidDataException(Loc.T("설정이 들어 있지 않아요."));
        var s = SettingsService.ParseForImport(settingsJson);
        var cals = ReadJson<List<CalendarEntry>>(zip, "calendars.json") ?? new List<CalendarEntry>();
        return new Preview(manifest, s.Pins.Select(p => p.Name).ToList(), cals);
    }

    /// <summary>
    /// 지금 설정을 백업한 뒤 파일의 설정을 적용. 아이콘은 icons 폴더에 풀고, 주소가 든 캘린더는 추가(이미 있으면 건너뜀),
    /// 주소 없는 캘린더는 이름만 돌려줌(다시 연결 안내). UI 스레드에서 부름.
    /// </summary>
    public static async Task<ImportResult> ImportAsync(string path, SettingsService settings, ICalendarFeedService calendars)
    {
        string suffix = ".bak-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string dataDir = Path.GetDirectoryName(settings.SettingsPath)!;
        foreach (string name in new[] { "settings.json", "calendars.json", "notifications-hidden.json" })
        {
            string src = Path.Combine(dataDir, name);
            if (File.Exists(src)) File.Copy(src, src + suffix, overwrite: true);
        }

        string iconsDir = Path.GetFullPath(settings.IconsDirectory);
        Settings imported;
        List<CalendarEntry> cals;
        int icons = 0;
        using (var zip = ZipFile.OpenRead(path))
        {
            imported = SettingsService.ParseForImport(ReadText(zip, "settings.json") ?? throw new InvalidDataException(Loc.T("설정이 들어 있지 않아요.")));
            cals = ReadJson<List<CalendarEntry>>(zip, "calendars.json") ?? new List<CalendarEntry>();
            Directory.CreateDirectory(iconsDir);
            string iconsBackup = iconsDir + suffix;
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("icons/", StringComparison.Ordinal) && e.Name.Length > 0))
            {
                string name = Path.GetFileName(entry.Name); // 경로 조각 무시 (zip 경로 탈출 방지) — 정해 둔 하위 폴더(routines·folders)만 살림
                var parts = entry.FullName.Split('/');
                string dir = parts.Length == 3 && IconSubfolders.Contains(parts[1]) ? Path.Combine(iconsDir, parts[1]) : iconsDir;
                if (!IconExtensions.Contains(Path.GetExtension(name)) || entry.Length > MaxIconBytes)
                {
                    Log.Warn($"설정 가져오기: 그림이 아니거나 너무 큰 아이콘 건너뜀 ({entry.Length} 바이트)");
                    continue;
                }
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, name);
                if (File.Exists(dest))
                {
                    // 같은 이름 아이콘은 덮기 전에 icons.bak-import-… 폴더로 (settings.json 백업으로 되돌릴 때 같이 쓰게)
                    string backupDir = Path.Combine(iconsBackup, Path.GetRelativePath(iconsDir, dir));
                    Directory.CreateDirectory(backupDir);
                    File.Copy(dest, Path.Combine(backupDir, name), overwrite: true);
                }
                entry.ExtractToFile(dest, overwrite: true);
                icons++;
            }
        }
        string? Unpack(string? p)
        {
            if (p is null || !p.StartsWith(IconsToken, StringComparison.Ordinal)) return p;
            var parts = p[IconsToken.Length..].TrimStart('\\', '/').Split('\\', '/');
            return parts.Length == 2 && IconSubfolders.Contains(parts[0])
                ? Path.Combine(iconsDir, parts[0], Path.GetFileName(parts[1]))
                : Path.Combine(iconsDir, Path.GetFileName(parts[^1]));
        }
        foreach (var pin in imported.Pins)
        {
            pin.IconPath = Unpack(pin.IconPath);
            if (pin.Icon is { } icon) icon.File = Unpack(icon.File);
        }
        foreach (var r in imported.Routines)
            if (r.Icon is { } icon) icon.File = Unpack(icon.File);
        // 다른 PC 의 모니터 이름이면 주 모니터로 (같은 PC 다시 설치면 그대로)
        if (!string.IsNullOrEmpty(imported.Dock.Monitor) && Monitors.Find(imported.Dock.Monitor) is null) imported.Dock.Monitor = "";
        settings.ApplyImported(imported);
        if (!imported.AllApps.ShowSuggestions) AppUsage.Clear(); // 끈 설정을 가져오면 이 PC 실행 기록도 지움

        int added = 0;
        var reconnect = new List<string>();
        foreach (var c in cals)
        {
            if (string.IsNullOrEmpty(c.Url))
            {
                if (!calendars.Feeds.Any(f => string.Equals(f.Name, c.Name, StringComparison.Ordinal))) reconnect.Add(c.Name);
                continue;
            }
            try
            {
                var r = await calendars.AddAsync(c.Url);
                if (!r.Ok || r.Feed is not { } feed) continue;
                added++;
                calendars.Update(feed.Id, f => { f.Name = c.Name; f.Color = c.Color; f.Enabled = c.Enabled; });
            }
            catch (Exception ex)
            {
                Log.Warn($"가져오기: 캘린더 추가 실패 ({ex.GetType().Name})");
            }
        }
        Log.Info($"설정 가져오기: 독 앱 {imported.Pins.Count}개, 아이콘 {icons}개, 캘린더 추가 {added}개, 다시 연결 필요 {reconnect.Count}개 (지금 설정 백업 *{suffix})");
        return new ImportResult(imported.Pins.Count, icons, added, reconnect, suffix);
    }

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string? ReadText(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException(Loc.T("파일이 너무 커요."));
        using var r = new StreamReader(entry.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static T? ReadJson<T>(ZipArchive zip, string name)
    {
        string? text = ReadText(zip, name);
        return text is null ? default : JsonSerializer.Deserialize<T>(text, Json);
    }
}
