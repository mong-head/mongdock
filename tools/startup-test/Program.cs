using System.IO;
using Microsoft.Win32;
using Mongdock;
using Mongdock.Services;

namespace StartupTest;

/// <summary>
/// 시작 시 실행·설치 방식 판별 시험. 패키지가 아닌 프로세스에서 돈다 (스토어판 실제 동작은 #9 MSIX 설치 시험에서).
/// - 일반판 Run 키(StartupService): 시험용 값 이름 "mongdock-selftest" 로 켜기/끄기 → 실제 "mongdock" 값은 건드리지 않음.
/// - 스토어판 서비스(PackagedStartupService)가 패키지 밖에서 만들어져도 예외 없이 "꺼짐·바꿀 수 없음".
/// 실패하면 종료 코드 1.
/// </summary>
internal static class Program
{
    private const string TestValue = "mongdock-selftest";
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        // 설정 옮기기 시험은 별도 실행: 데이터 폴더를 임시 폴더로 (AppInfo 를 건드리기 전에)
        if (args.Contains("--transfer")) return TransferTests();
        if (args.Contains("--pins")) return PinsPreview();

        Check("패키지 아님", AppInfo.IsPackaged, false);
        Check("설치 방식", AppInfo.InstallKind, "zip·개발 빌드");

        // ── 일반판 Run 키 ──
        var run = new StartupService(TestValue);
        int changed = 0;
        run.Changed += (_, _) => changed++;
        run.SetEnabled(true);
        Check("켜면 등록됨", run.IsEnabled, true);
        Check("켜면 상태 Enabled", run.State, StartupState.Enabled);
        Check("Run 값 = 따옴표 친 exe 경로", ReadValue(), $"\"{Environment.ProcessPath}\"");
        Check("바꿀 수 있음", run.CanChange, true);
        run.SetEnabled(false);
        Check("끄면 해제됨", run.IsEnabled, false);
        Check("끄면 상태 Disabled", run.State, StartupState.Disabled);
        Check("Run 값 없음", ReadValue(), null);
        Check("Changed 2번", changed, 2);

        // ── 패키지 밖의 스토어판 서비스 ──
        var packaged = new PackagedStartupService();
        Check("스토어판(패키지 밖): 꺼짐", packaged.State, StartupState.Disabled);
        Check("스토어판(패키지 밖): 바꿀 수 없음", packaged.CanChange, false);
        packaged.SetEnabled(true); // 예외 없이 무시
        Check("스토어판(패키지 밖): 켜도 그대로", packaged.IsEnabled, false);

        BackupTests();

        Console.WriteLine(_failed == 0 ? "시작 시 실행·백업 시험: 모두 통과" : $"시작 시 실행·백업 시험: {_failed}개 실패");
        return _failed == 0 ? 0 : 1;
    }

    // ── 업데이트 전 설정 백업 (UpdateBackup) — 임시 폴더에서만 ──
    private static void BackupTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mongdock-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string cur = Mongdock.Services.WhatsNew.CurrentText;
            File.WriteAllText(Path.Combine(dir, "calendars.json"), "{}");
            // 1) 옛 버전에서 올라옴 → .bak-v0.4.1 (settings·calendars, 없는 파일은 건너뜀)
            string old = "{\"settingsVersion\":3,\"lastSeenVersion\":\"0.4.1\"}";
            File.WriteAllText(Path.Combine(dir, "settings.json"), old);
            UpdateBackup.BeforeLoad(dir, old);
            Check("이전 버전 백업 settings", File.Exists(Path.Combine(dir, "settings.json.bak-v0.4.1")), true);
            Check("이전 버전 백업 calendars", File.Exists(Path.Combine(dir, "calendars.json.bak-v0.4.1")), true);
            Check("없는 파일은 백업 안 함", File.Exists(Path.Combine(dir, "notifications-hidden.json.bak-v0.4.1")), false);
            Check("최근 백업 표시", UpdateBackup.LatestLabel(dir), "v0.4.1");
            // 2) 같은 버전·이관 필요 없음 → 백업 안 함
            string same = $"{{\"settingsVersion\":{Mongdock.Models.Settings.CurrentVersion},\"lastRunVersion\":\"{cur}\"}}";
            int before = Directory.GetFiles(dir).Length;
            UpdateBackup.BeforeLoad(dir, same);
            Check("같은 버전이면 백업 없음", Directory.GetFiles(dir).Length, before);
            // 3) 버전 정보 없는 옛 파일 → .bak-pre-v<지금>
            UpdateBackup.BeforeLoad(dir, "{}");
            Check("버전 모름 → pre", File.Exists(Path.Combine(dir, $"settings.json.bak-pre-v{cur}")), true);
            // 4) 정리: 최근 3개만 (사람이 만든 .bak-notif 는 남김)
            File.WriteAllText(Path.Combine(dir, "settings.json.bak-notif"), "x");
            foreach (var v in new[] { "0.1.0", "0.2.0", "0.3.0" })
            {
                Thread.Sleep(20);
                UpdateBackup.BeforeLoad(dir, $"{{\"lastRunVersion\":\"{v}\"}}");
            }
            var ours = Directory.GetFiles(dir, "settings.json.bak-*").Select(Path.GetFileName).Where(n => n!.Contains(".bak-v") || n.Contains(".bak-pre-v")).ToList();
            Check("최근 3개만 남김", ours.Count, 3);
            Check("가장 오래된 것(0.4.1) 지워짐", File.Exists(Path.Combine(dir, "settings.json.bak-v0.4.1")), false);
            Check("사람이 만든 백업은 그대로", File.Exists(Path.Combine(dir, "settings.json.bak-notif")), true);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── 설정 옮기기 (SettingsTransfer) — 임시 데이터 폴더에서만 ──
    private sealed class FakeCalendars : ICalendarFeedService
    {
        public List<Mongdock.Models.CalendarFeed> List = new();
        public IReadOnlyList<Mongdock.Models.CalendarFeed> Feeds => List;
        public CalendarFeedStatus GetStatus(string feedId) => new(null, null, false, 0);
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<CalendarOccurrence> GetOccurrences(DateTime from, DateTime to) => Array.Empty<CalendarOccurrence>();
        public Task<CalendarAddResult> AddAsync(string url)
        {
            var f = new Mongdock.Models.CalendarFeed { Name = "added", Url = url };
            List.Add(f);
            return Task.FromResult(new CalendarAddResult(true, "", f));
        }
        public void Remove(string feedId) { }
        public void Update(string feedId, Action<Mongdock.Models.CalendarFeed> change) { foreach (var f in List.Where(f => f.Id == feedId)) change(f); }
        public void Refresh(string feedId) { }
        public void RefreshAll() { }
        public void Start() { }
        public void Stop() { }
    }

    private static int TransferTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "mongdock-transfer-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MONGDOCK_DATA_DIR", Path.Combine(root, "data"));
        try
        {
            using var settings = new SettingsService();
            Directory.CreateDirectory(settings.IconsDirectory);
            string icon = Path.Combine(settings.IconsDirectory, "test-icon.png");
            File.WriteAllBytes(icon, new byte[] { 1, 2, 3 });
            var s = settings.Current;
            s.Pins.Clear();
            s.Pins.Add(new Mongdock.Models.PinItem { Name = "메모장", Kind = Mongdock.Models.PinKind.Exe, Target = "notepad.exe", IconPath = icon });
            s.Pins.Add(new Mongdock.Models.PinItem { Name = "계산기", Kind = Mongdock.Models.PinKind.Exe, Target = "calc.exe" });
            s.Dock.IconSize = 60;
            s.StartWithWindows = true;
            settings.Save();
            var cals = new FakeCalendars();
            cals.List.Add(new Mongdock.Models.CalendarFeed { Name = "회사", Url = "https://example.com/private/secret.ics", Color = "#FF3B30" });

            string noUrl = Path.Combine(root, "a.mongdock"), withUrl = Path.Combine(root, "b.mongdock");
            SettingsTransfer.Export(noUrl, settings, cals, includeCalendarUrls: false);
            SettingsTransfer.Export(withUrl, settings, cals, includeCalendarUrls: true);
            var p1 = SettingsTransfer.ReadPreview(noUrl);
            Check("미리 보기 독 앱 수", p1.PinNames.Count, 2);
            Check("미리 보기 아이콘 수", p1.Manifest.IconCount, 1);
            Check("주소 뺀 캘린더", p1.Calendars.Single().Url, null);
            Check("주소 뺀 파일에 비밀 주소 없음", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(noUrl)).Contains("secret"), false);
            Check("주소 넣은 캘린더", SettingsTransfer.ReadPreview(withUrl).Calendars.Single().Url, "https://example.com/private/secret.ics");

            // 이 PC 에서 바꾼 뒤 가져오기
            s.Dock.IconSize = 40;
            s.StartWithWindows = false;
            File.Delete(icon);
            var empty = new FakeCalendars();
            var r = SettingsTransfer.ImportAsync(noUrl, settings, empty).GetAwaiter().GetResult();
            Check("가져온 독 아이콘 크기", settings.Current.Dock.IconSize, 60.0);
            Check("자동 실행은 이 PC 값 유지", settings.Current.StartWithWindows, false);
            Check("아이콘 파일 복원", File.Exists(icon), true);
            Check("아이콘 경로 이 PC 로", settings.Current.Pins[0].IconPath, icon);
            Check("다시 연결할 캘린더", string.Join(",", r.CalendarsToReconnect), "회사");
            Check("이전 설정 백업", File.Exists(settings.SettingsPath + r.BackupSuffix), true);
            var r2 = SettingsTransfer.ImportAsync(withUrl, settings, empty).GetAwaiter().GetResult();
            Check("주소 있는 캘린더 추가", r2.CalendarsAdded, 1);
            Check("추가된 캘린더 이름·색", $"{empty.List[0].Name} {empty.List[0].Color}", "회사 #FF3B30");

            File.WriteAllText(Path.Combine(root, "junk.mongdock"), "not a zip");
            bool rejected = false;
            try { SettingsTransfer.ReadPreview(Path.Combine(root, "junk.mongdock")); } catch (Exception ex) when (ex is InvalidDataException or IOException) { rejected = true; }
            Check("엉뚱한 파일 거절", rejected, true);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  실패  예외 {ex}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        Console.WriteLine(_failed == 0 ? "설정 옮기기 시험: 모두 통과" : $"설정 옮기기 시험: {_failed}개 실패");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>이 PC 에서 새 설치라면 독에 들어갈 앱 (임시 데이터 폴더, 실제 설정 안 건드림).</summary>
    private static int PinsPreview()
    {
        string root = Path.Combine(Path.GetTempPath(), "mongdock-pins-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MONGDOCK_DATA_DIR", root);
        try
        {
            using var settings = new SettingsService();
            var pins = DefaultPins.CreateInitial(settings);
            foreach (var p in pins) Console.WriteLine($"  {p.Kind,-7} {p.Name}");
            Console.WriteLine($"앱 {pins.Count - 2}개 (Finder·Launchpad 제외)");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return 0;
    }

    private static string? ReadValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRunKey);
        return key?.GetValue(TestValue) as string;
    }

    private const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static void Check<T>(string name, T actual, T expected)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected)) { Console.WriteLine($"  통과  {name}"); return; }
        _failed++;
        Console.WriteLine($"  실패  {name}\n        기대: {expected}\n        실제: {actual}");
    }
}
