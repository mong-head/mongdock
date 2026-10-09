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
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public sealed record CalendarEntry(string Name, string Color, bool Enabled, string? Url);

    public sealed record Manifest(string Format, int FormatVersion, string AppVersion, DateTime Created, bool IncludesCalendarUrls,
        int PinCount, int CalendarCount, int IconCount);

    /// <summary>가져오기 전 미리 보기 (파일 안 내용 요약).</summary>
    public sealed record Preview(Manifest Manifest, IReadOnlyList<string> PinNames, IReadOnlyList<CalendarEntry> Calendars);

    public sealed record ImportResult(int Pins, int Icons, int CalendarsAdded, IReadOnlyList<string> CalendarsToReconnect, string BackupSuffix);

    // ───────────────────────── 내보내기 ─────────────────────────

    public static Manifest Export(string path, SettingsService settings, ICalendarFeedService calendars, bool includeCalendarUrls)
    {
        string iconsDir = Path.GetFullPath(settings.IconsDirectory);
        var s = SettingsService.ParseForImport(settings.ExportJson()); // 지금 설정의 사본
        var icons = new List<string>();
        foreach (var pin in s.Pins)
        {
            if (string.IsNullOrWhiteSpace(pin.IconPath)) continue;
            string full;
            try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(pin.IconPath)); }
            catch { continue; }
            if (!full.StartsWith(iconsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
            string name = Path.GetFileName(full);
            pin.IconPath = IconsToken + "\\" + name;
            if (!icons.Contains(full, StringComparer.OrdinalIgnoreCase)) icons.Add(full);
        }
        var cals = calendars.Feeds.Select(f => new CalendarEntry(f.Name, f.Color, f.Enabled, includeCalendarUrls && f.Url.Length > 0 ? f.Url : null)).ToList();
        var manifest = new Manifest(Format, FormatVersion, ReportService.AppVersion(), DateTime.Now, includeCalendarUrls && cals.Any(c => c.Url is not null),
            s.Pins.Count, cals.Count, icons.Count);

        string tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "manifest.json", JsonSerializer.Serialize(manifest, Json));
            WriteEntry(zip, "settings.json", JsonSerializer.Serialize(s, Json));
            WriteEntry(zip, "calendars.json", JsonSerializer.Serialize(cals, Json));
            foreach (string icon in icons) zip.CreateEntryFromFile(icon, "icons/" + Path.GetFileName(icon));
        }
        File.Move(tmp, path, overwrite: true);
        Log.Info($"설정 내보내기: 독 앱 {manifest.PinCount}개, 아이콘 {manifest.IconCount}개, 캘린더 {manifest.CalendarCount}개 (주소 {(manifest.IncludesCalendarUrls ? "포함" : "뺌")})");
        return manifest;
    }

    // ───────────────────────── 가져오기 ─────────────────────────

    /// <summary>몽독 설정 파일이 아니거나 손상이면 예외 (메시지는 사용자에게 보여도 되는 한국어).</summary>
    public static Preview ReadPreview(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifest = ReadJson<Manifest>(zip, "manifest.json") ?? throw new InvalidDataException("몽독 설정 파일이 아니에요.");
        if (manifest.Format != Format) throw new InvalidDataException("몽독 설정 파일이 아니에요.");
        if (manifest.FormatVersion > FormatVersion) throw new InvalidDataException($"더 새 몽독(v{manifest.AppVersion})에서 만든 파일이에요. 몽독을 업데이트한 뒤 가져와 주세요.");
        string settingsJson = ReadText(zip, "settings.json") ?? throw new InvalidDataException("설정이 들어 있지 않아요.");
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
            imported = SettingsService.ParseForImport(ReadText(zip, "settings.json") ?? throw new InvalidDataException("설정이 들어 있지 않아요."));
            cals = ReadJson<List<CalendarEntry>>(zip, "calendars.json") ?? new List<CalendarEntry>();
            Directory.CreateDirectory(iconsDir);
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("icons/", StringComparison.Ordinal) && e.Name.Length > 0))
            {
                string name = Path.GetFileName(entry.Name); // 경로 조각 무시 (zip 경로 탈출 방지)
                entry.ExtractToFile(Path.Combine(iconsDir, name), overwrite: true);
                icons++;
            }
        }
        foreach (var pin in imported.Pins)
        {
            if (pin.IconPath is { } p && p.StartsWith(IconsToken, StringComparison.Ordinal))
                pin.IconPath = Path.Combine(iconsDir, Path.GetFileName(p[IconsToken.Length..].TrimStart('\\', '/')));
        }
        // 다른 PC 의 모니터 이름이면 주 모니터로 (같은 PC 다시 설치면 그대로)
        if (!string.IsNullOrEmpty(imported.Dock.Monitor) && Monitors.Find(imported.Dock.Monitor) is null) imported.Dock.Monitor = "";
        settings.ApplyImported(imported);

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
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("파일이 너무 커요.");
        using var r = new StreamReader(entry.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static T? ReadJson<T>(ZipArchive zip, string name)
    {
        string? text = ReadText(zip, name);
        return text is null ? default : JsonSerializer.Deserialize<T>(text, Json);
    }
}
