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
        if (args.Contains("--repair")) return RepairTests();
        if (args.Contains("--recycle")) return RecycleTests();

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

    /// <summary>
    /// settings.json 너그러운 읽기: 값 하나가 틀리면 그 항목만 기본값, 나머지(언어·독·핀)는 그대로 + .bad- 원본 보관.
    /// 파일이 잘렸으면 지금처럼 .corrupt- 백업 후 기본값. 임시 폴더에서만.
    /// </summary>
    /// <summary>독 폴더 내용 읽기: 숨김·desktop.ini 제외, 이름순/추가된 날짜순, 최대 개수와 전체 수. 없는 폴더는 null.</summary>
    private static void FolderListTests()
    {
        var type = typeof(SettingsService).Assembly.GetType("Mongdock.Services.DockFolderService")!;
        var list = type.GetMethod("List")!;
        string dir = Path.Combine(Path.GetTempPath(), "mongdock-foldertest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var t0 = DateTime.Now.AddHours(-3);
            foreach (var (name, hours) in new[] { ("b.txt", 1), ("c.txt", 2), ("a.txt", 3) })
            {
                string f = Path.Combine(dir, name);
                File.WriteAllText(f, name);
                File.SetCreationTime(f, t0.AddHours(hours));
                File.SetLastWriteTime(f, t0.AddHours(hours));
            }
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            Directory.SetCreationTime(Path.Combine(dir, "sub"), t0.AddHours(-1));
            Directory.SetLastWriteTime(Path.Combine(dir, "sub"), t0.AddHours(-1));
            File.WriteAllText(Path.Combine(dir, "desktop.ini"), "");
            string hidden = Path.Combine(dir, "hidden.txt");
            File.WriteAllText(hidden, "");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            string Names(object? r)
            {
                if (r is null) return "null";
                var items = (System.Collections.IEnumerable)r.GetType().GetField("Item1")!.GetValue(r)!;
                int total = (int)r.GetType().GetField("Item2")!.GetValue(r)!;
                return string.Join(",", items.Cast<FileSystemInfo>().Select(f => f.Name)) + "/" + total;
            }
            Check("폴더 내용: 추가된 날짜순(최근 먼저)", Names(list.Invoke(null, new object[] { dir, Mongdock.Models.FolderSort.Added, 20 })), "a.txt,c.txt,b.txt,sub/4");
            Check("폴더 내용: 이름순·최대 2개", Names(list.Invoke(null, new object[] { dir, Mongdock.Models.FolderSort.Name, 2 })), "a.txt,b.txt/4");
            Check("없는 폴더 → null", Names(list.Invoke(null, new object[] { Path.Combine(dir, "없음"), Mongdock.Models.FolderSort.Added, 20 })), "null");
        }
        finally
        {
            try { File.SetAttributes(Path.Combine(dir, "hidden.txt"), FileAttributes.Normal); Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>휴지통 정보 파일($I) 읽기 — 가짜 파일로만 (실제 휴지통은 건드리지 않음). 복원 경로에 확장자가 그대로여야 함.</summary>
    private static void RecycleInfoTests(Type rb)
    {
        var read = rb.GetMethod("ReadOriginalPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        string dir = Path.Combine(Path.GetTempPath(), "mongdock-rbtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string original = @"C:\Users\u\Documents\보고서 최종.docx";
            var v2 = new List<byte>();
            v2.AddRange(BitConverter.GetBytes(2L));
            v2.AddRange(BitConverter.GetBytes(12345L));
            v2.AddRange(BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()));
            v2.AddRange(BitConverter.GetBytes(original.Length + 1));
            v2.AddRange(System.Text.Encoding.Unicode.GetBytes(original + "\0"));
            string f2 = Path.Combine(dir, "$IABC123.docx");
            File.WriteAllBytes(f2, v2.ToArray());
            Check("$I 버전 2: 원래 경로(확장자 포함)", read.Invoke(null, new object[] { f2 }), original);

            var v1 = new byte[24 + 520];
            BitConverter.GetBytes(1L).CopyTo(v1, 0);
            System.Text.Encoding.Unicode.GetBytes(original).CopyTo(v1, 24);
            string f1 = Path.Combine(dir, "$IDEF456.docx");
            File.WriteAllBytes(f1, v1);
            Check("$I 버전 1: 원래 경로", read.Invoke(null, new object[] { f1 }), original);

            string bad = Path.Combine(dir, "$Ibad");
            File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });
            Check("$I 깨진 파일 → null", read.Invoke(null, new object[] { bad }), null);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 휴지통 실제 시험 (--recycle, 따로 실행): 시험이 만든 임시 파일 하나만 휴지통으로 보내고 → 변경 알림으로 개수가 바뀌는지 →
    /// 목록에 보이는지 → 복원해서 원래 이름(확장자 포함) 그대로 돌아오는지. 끝나면 임시 파일도 지움 (휴지통엔 남기지 않음).
    /// </summary>
    private static int RecycleTests()
    {
        var asm = typeof(SettingsService).Assembly;
        var rb = asm.GetType("Mongdock.Services.RecycleBin")!;
        var watcherType = asm.GetType("Mongdock.Services.RecycleBinWatcher")!;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        void Pump(Func<bool> until, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (!until() && DateTime.UtcNow < end)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Thread.Sleep(50);
            }
        }

        var watcher = (IDisposable)Activator.CreateInstance(watcherType)!;
        long Count() => (long)watcherType.GetProperty("Count")!.GetValue(watcher)!;
        bool Known() => (bool)watcherType.GetProperty("Known")!.GetValue(watcher)!;
        string dir = Path.Combine(Path.GetTempPath(), "mongdock-rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "몽독 휴지통 시험.txt");
        try
        {
            Pump(Known, 5000);
            Check("시작 때 한 번 확인", Known(), true);
            long before = Count();
            File.WriteAllText(file, "mongdock recycle test");
            // RecycleBin.Send 직접 (Touched 없이) → 셸 변경 알림만으로 바뀌는지
            bool sent = (bool)rb.GetMethod("Send")!.Invoke(null, new object[] { new[] { file } })!;
            Check("휴지통으로 보냄", sent, true);
            Pump(() => Count() == before + 1, 8000);
            Check("변경 알림으로 개수 +1", Count(), before + 1);

            object? found = null;
            var t = new Thread(() =>
            {
                var list = (System.Collections.IEnumerable)rb.GetMethod("List")!.Invoke(null, new object[] { 20 })!;
                foreach (var item in list)
                    if ((string)item.GetType().GetProperty("OriginalFolder")!.GetValue(item)! is var from
                        && string.Equals(Path.TrimEndingDirectorySeparator(from), Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase))
                    { found = item; break; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            Check("최근 목록에 보임", found is not null, true);
            if (found is not null)
            {
                bool restored = (bool)rb.GetMethod("Restore")!.Invoke(null, new[] { found })!;
                Check("복원됨", restored, true);
                Check("원래 이름 그대로 (확장자 포함)", File.Exists(file), true);
                Pump(() => Count() == before, 8000);
                Check("복원 뒤 개수 원래대로", Count(), before);
            }
        }
        finally
        {
            watcher.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
        Console.WriteLine(_failed == 0 ? "휴지통 시험: 모두 통과" : $"휴지통 시험: {_failed}개 실패");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>앱 모음 판 자동 분류 (#24): 실행 파일 → 스토어 앱 → 이름 낱말 → 시작 메뉴 폴더 → 기타, 사용자가 옮긴 것 우선. 이 PC 앱 분포도 찍음.</summary>
    private static void AllAppsTests()
    {
        var asm = typeof(SettingsService).Assembly;
        var entry = asm.GetType("Mongdock.Services.AppEntry")!;
        var cat = asm.GetType("Mongdock.Services.AllAppsCatalog")!;
        object E(string key, string name, string? exe = null, string? family = null, string? folder = null) =>
            Activator.CreateInstance(entry, key, name, exe, family, folder, null, null)!;
        string C(object e) => (string)cat.GetMethod("Classify")!.Invoke(null, new[] { e })!;
        Check("카카오톡 exe → 소통", C(E(@"{X}\Kakao\KakaoTalk.exe", "카카오톡", "kakaotalk")), "chat");
        Check("계산기 스토어 앱 → 도구", C(E("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "계산기", null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe")), "tools");
        Check("이름 '미디어 플레이어' → 음악", C(E("X.Y_123!App", "미디어 플레이어")), "music");
        Check("모르는 exe + 폴더 Games → 게임", C(E(@"{X}\Foo\foo.exe", "Foo Quest", "foo", null, "Games\\Foo")), "games");
        Check("이름 낱말은 낱말 단위 ('Outline' 은 line 아님)", C(E(@"{X}\o.exe", "Outline Tool", "outlinetool")), "other");
        Check("모르는 앱 → 기타", C(E(@"{X}\zz.exe", "Zz", "zz")), "other");

        var settings = new Mongdock.Models.AllAppsSettings();
        var kakao = E(@"{X}\Kakao\KakaoTalk.exe", "카카오톡", "kakaotalk");
        settings.Overrides[@"{X}\Kakao\KakaoTalk.exe"] = "work";
        Check("사용자가 옮긴 묶음이 이김", cat.GetMethod("GroupOf")!.Invoke(null, new[] { settings, kakao }), "work");
        settings.Overrides[@"{X}\Kakao\KakaoTalk.exe"] = "g-gone";
        Check("지워진 사용자 묶음 → 자동 분류", cat.GetMethod("GroupOf")!.Invoke(null, new[] { settings, kakao }), "chat");
        var order = (List<string>)cat.GetMethod("GroupOrder")!.Invoke(null, new object[] { settings })!;
        Check("묶음 순서: 기타는 늘 끝", order[^1], "other");
        Check("독 경로 핀과 판 앱이 같은 실행 기록", cat.GetMethod("Identity", new[] { typeof(Mongdock.Models.PinItem) })!.Invoke(null, new object[] { new Mongdock.Models.PinItem { Kind = Mongdock.Models.PinKind.Exe, Target = @"C:\Program Files\Kakao\KakaoTalk.exe" } }),
            cat.GetMethod("Identity", new[] { entry })!.Invoke(null, new[] { kakao }));

        // 이 PC 의 실제 앱 분포 (참고용 — 실패 조건 아님)
        var apps = (System.Collections.IEnumerable)cat.GetMethod("Apps")!.Invoke(null, new object[] { false })!;
        var dist = new Dictionary<string, int>();
        var others = new List<string>();
        foreach (var a in apps)
        {
            string g = C(a);
            dist[g] = dist.GetValueOrDefault(g) + 1;
            if (g == "other") others.Add((string)entry.GetProperty("Name")!.GetValue(a)!);
        }
        Console.WriteLine("  (참고) 이 PC 앱 분류: " + string.Join(", ", dist.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        Console.WriteLine("  (참고) 기타: " + string.Join(" | ", others.Take(60)));
    }

    private static int RepairTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "mongdock-repair-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MONGDOCK_DATA_DIR", root);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        const string good = """
            {
              "settingsVersion": 4, "language": "en", "lastRunVersion": "0.5.0", "lastSeenVersion": "0.5.0",
              "dock": { "iconSize": 60, "mode": "Overlay" },
              "topBar": { "height": 30, "colorMode": "Fixed" },
              "pins": [ { "name": "메모장", "kind": "Exe", "target": "notepad.exe" }, { "name": "계산기", "kind": "Exe", "target": "calc.exe" } ]
            }
            """;
        (string Name, string Json)[] cases =
        {
            ("enum 오타", good.Replace("\"colorMode\": \"Fixed\"", "\"colorMode\": \"Dark\"")),
            ("숫자 자리에 글자", good.Replace("\"height\": 30", "\"height\": \"tall\"")),
            ("배열 원소의 enum 오타", good.Replace("{ \"name\": \"계산기\", \"kind\": \"Exe\"", "{ \"name\": \"계산기\", \"kind\": \"Weird\"")),
            ("두 군데 틀림", good.Replace("\"colorMode\": \"Fixed\"", "\"colorMode\": 7").Replace("\"mode\": \"Overlay\"", "\"mode\": \"Floating\"")),
        };
        try
        {
            foreach (var (name, json) in cases)
            {
                foreach (var f in Directory.GetFiles(root)) File.Delete(f);
                File.WriteAllText(path, json);
                using (var settings = new SettingsService())
                {
                    var s = settings.Current;
                    Check($"{name}: 언어 그대로", s.Language, "en");
                    Check($"{name}: 독 아이콘 크기 그대로", s.Dock.IconSize, 60.0);
                    Check($"{name}: 핀 수 (틀린 핀만 빠짐)", s.Pins.Count, name.StartsWith("배열") ? 1 : 2);
                    Check($"{name}: 첫 핀 이름", s.Pins.Count > 0 ? s.Pins[0].Name : "", "메모장");
                }
                Check($"{name}: .bad- 원본 보관", Directory.GetFiles(root, "settings.json.bad-*").Length, 1);
                Check($"{name}: 고친 파일은 다시 읽힘", SettingsService.ParseForImport(File.ReadAllText(path)).Language, "en");
            }

            // 틀린 값의 그 항목만 기본값인지
            foreach (var f in Directory.GetFiles(root)) File.Delete(f);
            File.WriteAllText(path, cases[0].Json);
            using (var settings = new SettingsService())
                Check("enum 오타 → 그 항목만 기본값(Auto)", settings.Current.TopBar.ColorMode, Mongdock.Models.TopBarColorMode.Auto);
            foreach (var f in Directory.GetFiles(root)) File.Delete(f);
            File.WriteAllText(path, cases[1].Json);
            using (var settings = new SettingsService())
                Check("글자 높이 → 기본값 26", settings.Current.TopBar.Height, 26.0);

            // 가져오기도 같은 읽기
            var imported = SettingsService.ParseForImport(cases[3].Json);
            Check("가져오기: 틀린 값 두 개 있어도 핀 유지", imported.Pins.Count, 2);
            Check("숫자로 적은 없는 enum 값(7) → 기본값", imported.TopBar.ColorMode, Mongdock.Models.TopBarColorMode.Auto);

            // 모르는 핀 종류(더 새 몽독의 것)는 그 핀만 건너뜀 — Exe 로 바뀌지 않음
            var future = SettingsService.ParseForImport("""
                { "settingsVersion": 5, "pins": [
                  { "name": "메모장", "kind": "Exe", "target": "notepad.exe" },
                  { "name": "아침 루틴", "kind": "Routine2", "target": "", "items": [ { "kind": "app" } ] },
                  { "name": "계산기", "kind": "Exe", "target": "calc.exe" } ] }
                """);
            Check("모르는 핀 종류는 그 핀만 빠짐", string.Join(",", future.Pins.Select(p => p.Name)), "메모장,계산기");
            Check("숫자로 적힌 모르는 핀 종류도 그 핀만 빠짐", string.Join(",", SettingsService.ParseForImport("""
                { "settingsVersion": 5, "pins": [ { "name": "A", "kind": 9, "target": "x" }, { "name": "B", "kind": "Exe", "target": "b.exe" } ] }
                """).Pins.Select(p => p.Name)), "B");

            // 독 폴더·루틴 (#24): 폴더는 목록 끝으로, 경로 없는 폴더·항목 없는 루틴은 빠짐, 옵션·id 채움
            var extras = SettingsService.ParseForImport("""
                { "settingsVersion": 5, "pins": [
                  { "name": "받은 파일", "kind": "Folder", "target": "C:\\Users\\u\\Downloads", "folder": { "sort": "name", "display": "folder" } },
                  { "name": "메모장", "kind": "Exe", "target": "notepad.exe" },
                  { "name": "빈 폴더 핀", "kind": "Folder", "target": "" },
                  { "name": "문서", "kind": "Folder", "target": "C:\\Users\\u\\Documents" },
                  { "name": "빈 루틴", "kind": "Routine", "routine": { "items": [] } },
                  { "name": "아침", "kind": "Routine", "routine": { "desktop": { "mode": "new" }, "items": [
                      { "kind": "app", "target": "notepad.exe", "monitor": { "mode": "index", "index": 1 }, "placement": { "mode": "left" }, "delayMs": 500 },
                      { "kind": "url" } ] } },
                  { "name": "계산기", "kind": "Exe", "target": "calc.exe" } ] }
                """);
            Check("폴더는 그 자리 그대로·잘못된 폴더/루틴 빠짐", string.Join(",", extras.Pins.Select(p => p.Name)), "받은 파일,메모장,문서,아침,계산기");
            var dl = extras.Pins.First(p => p.Name == "받은 파일");
            Check("폴더 옵션 읽힘 (옛 display 는 무시)", dl.Folder?.Sort, Mongdock.Models.FolderSort.Name);
            Check("옛 폴더 핀: 마지막 연 시각 채움 (새 파일 점은 지금부터)", dl.Folder?.LastOpened is not null, true);
            Check("폴더 옵션 없으면 기본값", extras.Pins.First(p => p.Name == "문서").Folder?.Sort, Mongdock.Models.FolderSort.Added);
            Check("폴더 id 채움", string.IsNullOrEmpty(dl.Id), false);
            var morningPin = extras.Pins.First(p => p.Name == "아침");
            Check("옛 루틴 핀 → 루틴 목록으로 옮김 (핀은 Id 만)", morningPin.Routine is null && extras.Routines.Any(r => r.Id == morningPin.Target), true);
            var morning = extras.Routines.First(r => r.Id == morningPin.Target);
            Check("루틴: 대상 없는 항목 빠짐", morning.Items.Count, 1);
            Check("루틴: 데스크톱·모니터·배치·지연", $"{morning.Desktop?.Mode}/{morning.Items[0].Monitor?.Mode}{morning.Items[0].Monitor?.Index}/{morning.Items[0].Placement?.Mode}/{morning.Items[0].DelayMs}", "New/Index1/Left/500");
            var again = SettingsService.ParseForImport(System.Text.Json.JsonSerializer.Serialize(extras, (System.Text.Json.JsonSerializerOptions)typeof(SettingsService).GetField("JsonOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!));
            Check("다시 저장·읽어도 그대로", string.Join(",", again.Pins.Select(p => p.Name + ":" + p.Id)), string.Join(",", extras.Pins.Select(p => p.Name + ":" + p.Id)));
            // 독 휴지통 (#24-C): 하나만, 늘 목록 끝
            var trash = SettingsService.ParseForImport("""
                { "settingsVersion": 5, "pins": [
                  { "name": "", "kind": "Special", "target": "recyclebin" },
                  { "name": "메모장", "kind": "Exe", "target": "notepad.exe" },
                  { "name": "", "kind": "Special", "target": "recyclebin" },
                  { "name": "문서", "kind": "Folder", "target": "C:/docs" } ] }
                """);
            Check("휴지통: 하나만 남고 맨 끝", string.Join(",", trash.Pins.Select(p => p.Target)), "notepad.exe,C:/docs,recyclebin");
            var rb = typeof(SettingsService).Assembly.GetType("Mongdock.Services.RecycleBin")!;
            Check("용량 표시 340MB", rb.GetMethod("FormatSize")!.Invoke(null, new object[] { 340L * 1024 * 1024 }), "340MB");
            Check("용량 표시 1.5GB", rb.GetMethod("FormatSize")!.Invoke(null, new object[] { 1536L * 1024 * 1024 }), "1.5GB");
            Check("휴지통 개수 읽힘", rb.GetMethod("Query")!.Invoke(null, null) is not null, true);
            RecycleInfoTests(rb);
            AllAppsTests();
            FolderListTests();

            // 5: 핀 이름 Finder·Launchpad → 파일 탐색기·앱 모음 (정확히 같은 이름·대상만, 사용자가 바꾼 이름은 그대로)
            var renamed = SettingsService.ParseForImport("""
                { "settingsVersion": 4, "pins": [
                  { "name": "Finder", "kind": "Exe", "target": "C:\\Windows\\explorer.exe" },
                  { "name": "Launchpad", "kind": "Special", "target": "launchpad" },
                  { "name": "내 Finder", "kind": "Exe", "target": "C:\\Windows\\explorer.exe" },
                  { "name": "Finder", "kind": "Exe", "target": "C:\\Tools\\finder.exe" } ] }
                """);
            Check("Finder → 파일 탐색기", renamed.Pins[0].Name, Mongdock.Services.DefaultPins.ExplorerName);
            Check("Launchpad → 앱 모음", renamed.Pins[1].Name, Mongdock.Services.DefaultPins.AllAppsName);
            Check("사용자가 바꾼 이름은 그대로", renamed.Pins[2].Name, "내 Finder");
            Check("다른 대상의 Finder 는 그대로", renamed.Pins[3].Name, "Finder");
            // 5: 사용 통계는 동의를 받고서만 — 옛 파일의 true 도 다시 물음, 새 파일의 답은 그대로
            Check("옛 파일 sendUsageStats true → 묻기 전(null)",
                SettingsService.ParseForImport("""{ "settingsVersion": 4, "sendUsageStats": true }""").SendUsageStats, (bool?)null);
            Check("이번 버전 파일의 답은 그대로",
                SettingsService.ParseForImport("""{ "settingsVersion": 5, "sendUsageStats": false }""").SendUsageStats, (bool?)false);

            // 잘린 JSON → 전체 손상 → .corrupt- 백업 + 기본값
            foreach (var f in Directory.GetFiles(root)) File.Delete(f);
            File.WriteAllText(path, good[..(good.Length / 2)]);
            using (var settings = new SettingsService())
                Check("잘린 JSON → 기본값(언어 자동)", settings.Current.Language, "");
            Check("잘린 JSON → .corrupt- 백업", Directory.GetFiles(root, "settings.json.corrupt-*").Length, 1);
            bool threw = false;
            try { SettingsService.ParseForImport(good[..(good.Length / 2)]); }
            catch (System.Text.Json.JsonException) { threw = true; }
            Check("잘린 JSON 가져오기 → 거절", threw, true);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        Console.WriteLine(_failed == 0 ? "설정 읽기 복구 시험: 모두 통과" : $"설정 읽기 복구 시험: {_failed}개 실패");
        return _failed == 0 ? 0 : 1;
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
            SettingsTransfer.PrepareExport(noUrl, settings, cals, includeCalendarUrls: false)();
            SettingsTransfer.PrepareExport(withUrl, settings, cals, includeCalendarUrls: true)();
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
