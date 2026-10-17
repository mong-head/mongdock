using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.Views;

namespace ReportTest;

/// <summary>
/// report-test            → 가리기 단위 시험 (실패하면 종료 코드 1)
/// report-test --png DIR  → 가리기 시험 + 신고 창을 라이트/다크로 렌더링해 DIR 에 PNG 저장
/// </summary>
internal static class Program
{
    private static int _failed;
    private static string _lang = "ko";

    [STAThread]
    private static int Main(string[] args)
    {
        // --focuscheck: 몽독 시작 순서대로 FocusRing.Init 을 먼저 부른 뒤 메뉴·버튼을 만들어도 터지지 않는지 (2026-10-13 설치 직후 MenuItem 정적 생성자 충돌)
        if (Array.IndexOf(args, "--focuscheck") >= 0)
        {
            try
            {
                if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.FocusRing")!.GetMethod("Init", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null);
                typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.MongToolTip")!.GetMethod("Init", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null);
                bool tipStyle = Application.Current!.Resources.Contains(typeof(System.Windows.Controls.ToolTip));
                _ = new System.Windows.Controls.ContextMenu();
                _ = new System.Windows.Controls.MenuItem();
                _ = new System.Windows.Controls.ComboBox();
                _ = new System.Windows.Controls.Primitives.ScrollBar();
                var b = new System.Windows.Controls.Button();
                var cb = new System.Windows.Controls.CheckBox();
                var host = new System.Windows.Window { Left = -20000, Top = -20000, Width = 100, Height = 100, ShowActivated = false, Content = new System.Windows.Controls.StackPanel { Children = { b, cb } } };
                host.Show(); // 붙을 때(Loaded) 점선이 지워지는지
                host.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                host.Activate();
                System.Windows.Input.Keyboard.Focus(b);
                System.Windows.Input.Keyboard.Focus(cb);
                Console.WriteLine($"포커스 점검: 통과 (몽독 툴팁 스타일 {tipStyle}, 붙음 {b.IsLoaded}, 버튼 점선 {(b.FocusVisualStyle is null ? "없음" : "있음")}, 체크 점선 {(cb.FocusVisualStyle is null ? "없음" : "있음")})");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("포커스 점검: 실패 " + ex); return 1; }
        }
        // --en 또는 --lang <코드>: 그 언어 화면으로 렌더링 (App 생성자가 언어를 다시 정하므로 렌더링 때 다시 적용)
        int langAt = Array.FindIndex(args, a => a == "--lang");
        _lang = args.Contains("--en") ? "en" : langAt >= 0 && langAt + 1 < args.Length ? args[langAt + 1] : "ko";
        // --settings DIR: 설정 창 페이지 PNG 만 (임시 데이터 폴더 — 다른 시험보다 먼저, AppInfo 를 건드리기 전에)
        int settingsAt = Array.FindIndex(args, a => a == "--settings");
        if (settingsAt >= 0 && settingsAt + 1 < args.Length)
        {
            Environment.SetEnvironmentVariable("MONGDOCK_DATA_DIR", Path.Combine(Path.GetTempPath(), "mongdock-settings-png-" + Guid.NewGuid().ToString("N")));
            Mongdock.Loc.Init(_lang);
            RenderSettingsPages(args[settingsAt + 1]);
            return 0;
        }
        // --shots DIR: 홈페이지 스크린샷 8장 (site-draft/shots.md) — 시험 데이터 폴더·예시 데이터로, --lang 없으면 한국어·영어 둘 다
        int shotsAt = Array.FindIndex(args, a => a is "--shots" or "--shots2"); // --shots2 = 홈페이지 v2 (독·상단바 중심)
        if (shotsAt >= 0 && shotsAt + 1 < args.Length)
        {
            string flag = args[shotsAt];
            string shotsDir = args[shotsAt + 1];
            if (langAt < 0)
            {
                int bad = 0;
                foreach (var l in new[] { "ko", "en", "ja", "zh-Hans", "zh-Hant", "de", "fr", "es" })
                {
                    using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"{flag} \"{shotsDir}\" --lang {l}") { UseShellExecute = false })!;
                    child.WaitForExit();
                    if (child.ExitCode != 0) bad++;
                }
                return bad == 0 ? 0 : 1;
            }
            string data = Path.Combine(Path.GetTempPath(), "mongdock-shots-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("MONGDOCK_DATA_DIR", data);
            Environment.SetEnvironmentVariable("MONGDOCK_FAKE_BATTERY", "76,charging");
            Environment.SetEnvironmentVariable("MONGDOCK_FAKE_BRIGHTNESS", _lang == "ko" ? "내장:70,외장 모니터:40" : "internal:70,External display:40");
            Mongdock.Loc.Init(_lang);
            return RenderShots(shotsDir, Path.Combine(data, "examples"), v2: flag == "--shots2");
        }
        Mongdock.Loc.Init(_lang);
        RedactionTests();
        MenuRuleLanguageTests();
        LanguageRuleTests();
        Console.WriteLine(_failed == 0 ? "가리기 시험: 모두 통과" : $"가리기 시험: {_failed}개 실패");

        // 이 PC 의 실제 로그를 가린 결과 (보내지 않음 — 눈으로 확인용)
        if (args.Contains("--log"))
            Console.WriteLine(ReportService.AppendLog("", ReportRedactor.Context.Current(Array.Empty<string>())));

        int iconsAt = Array.FindIndex(args, a => a == "--icons");
        if (iconsAt >= 0 && iconsAt + 1 < args.Length) RenderAllAppsIcons(args[iconsAt + 1]);

        int panelsAt = Array.FindIndex(args, a => a == "--panels");
        if (panelsAt >= 0 && panelsAt + 1 < args.Length) { RenderStatusPanels(args[panelsAt + 1]); return 0; }

        int introAt = Array.FindIndex(args, a => a == "--intro");
        if (introAt >= 0 && introAt + 1 < args.Length) { RenderIntroFrames(args[introAt + 1]); return 0; }

        int routinesAt = Array.FindIndex(args, a => a == "--routines");
        if (routinesAt >= 0 && routinesAt + 1 < args.Length) { RenderRoutines(args[routinesAt + 1]); return _failed == 0 ? 0 : 1; }

        // --seedcheck FILE: 새로 설치한 사용자처럼(빈 데이터 폴더) 윈도우 실행 기록 씨앗만으로 첫날 추천이 몇 개 뜨는지
        int seedcheck = Array.FindIndex(args, a => a == "--seedcheck");
        if (seedcheck >= 0 && seedcheck + 1 < args.Length)
        {
            string fresh = Path.Combine(Path.GetTempPath(), "mongdock-seed-" + Guid.NewGuid().ToString("N")[..8]);
            Environment.SetEnvironmentVariable("MONGDOCK_DATA_DIR", fresh); // AppInfo 를 건드리기 전에
            var asm = typeof(Mongdock.App).Assembly;
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var usage = asm.GetType("Mongdock.Services.AppUsage")!;
            int seeded = (int)usage.GetMethod("SeedFromUserAssist", flags)!.Invoke(null, new object?[] { null })!;
            var catalog = asm.GetType("Mongdock.Services.AllAppsCatalog")!;
            var apps = ((System.Collections.IEnumerable)catalog.GetMethod("Apps")!.Invoke(null, new object[] { false })!).Cast<object>().ToList();
            var identity = catalog.GetMethods(flags).First(m => m.Name == "Identity" && m.GetParameters()[0].ParameterType.Name == "AppEntry");
            var ids = apps.Select(a => (string)identity.Invoke(null, new[] { a })!).ToHashSet();
            var seedMap = (Dictionary<string, int>)usage.GetMethod("Seed", flags)!.Invoke(null, null)!;
            var lastMap = (Dictionary<string, string>)usage.GetMethod("Last", flags)!.Invoke(null, null)!;
            string d90 = DateTime.Now.AddDays(-90).ToString("yyyy-MM-dd");
            string seedLine = $"씨앗 단계: 전체 {seedMap.Count}, 3회 이상 {seedMap.Count(kv => kv.Value >= 3)}, 그중 마지막 실행일 있음 {seedMap.Count(kv => kv.Value >= 3 && lastMap.ContainsKey(kv.Key))}, 90일 안 {seedMap.Count(kv => kv.Value >= 3 && lastMap.TryGetValue(kv.Key, out var l) && string.CompareOrdinal(l, d90) >= 0)}, 앱 모음에 있음(3회 이상) {seedMap.Count(kv => kv.Value >= 3 && ids.Contains(kv.Key))}";
            var stages = usage.GetMethod("SuggestionStages", flags)!.Invoke(null, new object?[] { 16, null, null })!;
            var result = (List<string>)stages.GetType().GetField("Item4")!.GetValue(stages)!;
            int fromWin = (int)stages.GetType().GetField("Item3")!.GetValue(stages)!;
            File.WriteAllLines(args[seedcheck + 1], new[]
            {
                $"빈 데이터 폴더(새 설치)에서 윈도우 실행 기록 씨앗: {seeded}개",
                seedLine,
                $"추천 후보: {result.Count}개 (윈도우 기록으로 {fromWin}개), 앱 모음에 있는 앱 {result.Count(ids.Contains)}개 → 독 고정이 없으면 첫날 추천 {Math.Min(4, result.Count(ids.Contains))}칸 (기준: 90일 안·2회 이상)",
            });
            try { Directory.Delete(fresh, true); } catch { }
            return 0;
        }
        // --favdiag FILE: 이 PC 실제 기록으로 즐겨찾기 추천이 단계마다 몇 개 남는지 (앱 이름은 FILE 에만 — 로그에 안 남김). 읽기만.
        int favdiag = Array.FindIndex(args, a => a == "--favdiag");
        if (favdiag >= 0 && favdiag + 1 < args.Length)
        {
            var asm = typeof(Mongdock.App).Assembly;
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var settings = new SettingsService();
            var s = settings.Current;
            var catalog = asm.GetType("Mongdock.Services.AllAppsCatalog")!;
            var apps = ((System.Collections.IEnumerable)catalog.GetMethod("Apps")!.Invoke(null, new object[] { false })!).Cast<object>().ToList();
            string Key(object a) => (string)a.GetType().GetProperty("Key")!.GetValue(a)!;
            string Name(object a) => (string)a.GetType().GetProperty("Name")!.GetValue(a)!;
            var identity = catalog.GetMethods(flags).First(m => m.Name == "Identity" && m.GetParameters()[0].ParameterType.Name == "AppEntry");
            var visible = apps.Where(a => !s.AllApps.Hidden.Contains(Key(a), StringComparer.OrdinalIgnoreCase)).ToList();
            var byId = new Dictionary<string, object>();
            foreach (var a in visible) byId.TryAdd((string)identity.Invoke(null, new[] { a })!, a);
            var pinId = catalog.GetMethods(flags).First(m => m.Name == "Identity" && m.GetParameters()[0].ParameterType == typeof(PinItem));
            var docked = s.Pins.Select(p => (string?)pinId.Invoke(null, new object[] { p })).OfType<string>().ToHashSet();
            var usage = asm.GetType("Mongdock.Services.AppUsage")!;
            var stages = usage.GetMethod("SuggestionStages", flags)!.Invoke(null, new object?[] { 16, null, null })!;
            int Item(int i) => (int)stages.GetType().GetField("Item" + i)!.GetValue(stages)!;
            var result = (List<string>)stages.GetType().GetField("Item4")!.GetValue(stages)!;
            var panelOnly = (List<string>)stages.GetType().GetField("Item1")!.GetValue(stages)!;
            int oldShown = panelOnly.Where(byId.ContainsKey).Where(id => !docked.Contains(id) && !s.AllApps.Favorites.Contains(Key(byId[id]), StringComparer.OrdinalIgnoreCase)).Count();
            var mapped = result.Where(byId.ContainsKey).ToList();
            var notDocked = mapped.ToList(); // 독 고정 앱도 추천에 넣음 (2026-10-13)
            var notFav = notDocked.Where(id => !s.AllApps.Favorites.Contains(Key(byId[id]), StringComparer.OrdinalIgnoreCase)).ToList();
            var final = notFav.Where(id => !(s.AllApps.DismissedSuggestions.TryGetValue(Key(byId[id]), out var when) && DateTime.Now - when < TimeSpan.FromDays(30))).ToList();
            // 앱이 실제로 쓰는 계산 그대로 (AllAppsPanel.Suggestions 와 같은 Keep·SeedKeep)
            var isSystem = asm.GetType("Mongdock.Services.UnusedApps")!.GetMethod("IsSystemApp", flags)!;
            Func<string, bool> keep = id => byId.TryGetValue(id, out var a)
                && !s.AllApps.Favorites.Contains(Key(a), StringComparer.OrdinalIgnoreCase)
                && !(s.AllApps.DismissedSuggestions.TryGetValue(Key(a), out var w) && DateTime.Now - w < TimeSpan.FromDays(30));
            Func<string, bool> seedKeep = id => byId.TryGetValue(id, out var a) && !(bool)isSystem.Invoke(null, new[] { a })!;
            var real = (List<string>)usage.GetMethod("Suggestions", flags)!.Invoke(null, new object?[] { 4, keep, seedKeep })!;
            var lines = new List<string>
            {
                $"기록 켜짐: {s.AllApps.ShowSuggestions}",
                $"1) 판·독에서 연 기록만으로 후보(14일·2일·3회): {panelOnly.Count}개 — 독 고정 {panelOnly.Count(docked.Contains)}개, 앱 목록에 없음 {panelOnly.Count(id => !byId.ContainsKey(id))}개 → 예전 방식이면 보이는 추천 {oldShown}개",
                $"2) 새 창 기록까지 더한 후보: {Item(2)}개",
                $"3) 윈도우 기록(UserAssist)으로 채움: {Item(3)}개 → 후보 합계 {result.Count}개 (최대 16개까지 봄)",
                $"4) 앱 모음 목록에 있는 앱: {mapped.Count}개",
                $"5) 독 고정 앱은 빼지 않음: {notDocked.Count}개",
                $"6) 즐겨찾기 이미 든 앱 뺌: {notFav.Count}개",
                $"7) 30일 안에 뺀(×) 앱 뺌: {final.Count}개 → 판에 보이는 추천 {Math.Min(4, final.Count)}개",
                "추천 (PM 보고용): " + string.Join(", ", final.Take(4).Select(id => Name(byId[id]))),
                "앱과 같은 계산(빼기 먼저 → 1순위 → 2순위, 기본 앱은 2순위에서 뺌): " + string.Join(", ", real.Select(id => Name(byId[id]))) + $" ({real.Count}칸)",
            };
            File.WriteAllLines(args[favdiag + 1], lines);
            return 0;
        }
        int tour = Array.FindIndex(args, a => a == "--tour");
        if (tour >= 0 && tour + 1 < args.Length) return RenderTour(args[tour + 1], checkOnly: false);
        if (Array.IndexOf(args, "--tourcheck") >= 0)
        {
            // 둘러보기 장면·카드 점검 (시작 점검처럼 매번). --lang 없으면 모든 언어를 하나씩 (문구가 시작 때 정해져 언어마다 따로 실행)
            if (langAt >= 0 || args.Contains("--en")) return RenderTour(null, checkOnly: true);
            int failed = 0;
            foreach (var l in new[] { "ko", "en", "de", "fr", "es", "ja", "zh-Hans", "zh-Hant" })
            {
                using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"--tourcheck --lang {l}") { UseShellExecute = false })!;
                child.WaitForExit();
                if (child.ExitCode != 0) failed++;
            }
            Console.WriteLine(failed == 0 ? "둘러보기 점검: 모든 언어 통과" : $"둘러보기 점검: {failed}개 언어 실패");
            return failed == 0 ? 0 : 1;
        }
        int types = Array.FindIndex(args, a => a == "--types");
        if (types >= 0 && types + 1 < args.Length) { _typesOnly = true; RenderAllAppsPanel(args[types + 1]); return 0; }
        int allApps = Array.FindIndex(args, a => a == "--allapps");
        if (allApps >= 0 && allApps + 1 < args.Length) RenderAllAppsPanel(args[allApps + 1]);
        if (Array.IndexOf(args, "--dockfit") >= 0) { DockFitReport(); return 0; }
        if (Array.IndexOf(args, "--clipdemo") >= 0) { ClipDemo(); return 0; }
        if (Array.IndexOf(args, "--suggest") >= 0) return SuggestTable();
        if (Array.IndexOf(args, "--tourmem") >= 0) return TourMemory(); // 둘러보기 장면 그림 메모리: 다 그린 뒤 / 정리 뒤 (화면 없음)
        if (Array.IndexOf(args, "--appbarcheck") >= 0) return AppBarCheck(); // 0.6.2 모니터 여러 대 작업 표시줄 깜빡임 — 자리 같으면 안 알림·고리 끊김 (실제 AppBar 등록 없음)
        int volAt = Array.FindIndex(args, a => a == "--volumecheck");
        if (volAt >= 0) return VolumeCheck(volAt + 1 < args.Length ? args[volAt + 1] : null); // 0.8 앱별 볼륨·볼륨 표시 (#21) — 실제 볼륨은 안 바꿈
        if (Array.IndexOf(args, "--clipcheck") >= 0) return ClipCheck(); // 0.6.3 문제 신고 영역 캡처·붙여넣기 (#13) — 실제 캡처 도구·클립보드는 안 건드림
        if (Array.IndexOf(args, "--profilecheck") >= 0) return ProfileCheck(); // 0.6.1 루틴 브라우저 계정·탐색기 창 (화면·실제 실행 없음)
        if (Array.IndexOf(args, "--hotkeycheck") >= 0) return HotkeyCheck(); // 0.6 단축키 규칙·저장·키 처리 점검 (화면·실제 입력 없음)
        if (Array.IndexOf(args, "--reviewcheck") >= 0) return ReviewCheck(); // 0.5.0 검토 수정 점검 (화면 없음)
        int unin = Array.FindIndex(args, a => a == "--uninstall");
        if (unin >= 0) return UninstallTest(unin + 1 < args.Length ? args[unin + 1] : null);
        if (Array.IndexOf(args, "--games") >= 0)
        {
            // 이 PC 의 게임 목록 (§15-3): 런처별 개수와 앞 몇 개 (이름·링크·exe 있는지)
            var gc = typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.GameCatalog")!;
            var all = (System.Collections.IEnumerable)gc.GetMethod("All", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
            var games = all.Cast<object>().ToList();
            string P(object g, string n) => g.GetType().GetProperty(n)!.GetValue(g)?.ToString() ?? "";
            Console.WriteLine($"게임 {games.Count}개: " + string.Join(", ", games.GroupBy(g => P(g, "Launcher")).Select(x => $"{x.Key} {x.Count()}")));
            foreach (var g in games.Take(15)) Console.WriteLine($"  [{P(g, "Launcher")}] {P(g, "Name")} → {P(g, "Launch")} (exe {(P(g, "Exe").Length > 0 ? (File.Exists(P(g, "Exe")) ? "있음" : "없음") : "모름")})");
            // 앱 모음 "게임" 폴더 분류 (런처 게임 링크·라이브러리 exe)
            var cat = typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.AllAppsCatalog")!;
            var apps = ((System.Collections.IEnumerable)cat.GetMethod("Apps")!.Invoke(null, new object[] { false })!).Cast<object>().ToList();
            var isGame = cat.GetMethod("IsGame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var classify = cat.GetMethod("Classify")!;
            var gameApps = apps.Where(a => (bool)isGame.Invoke(null, new[] { a })!).ToList();
            Console.WriteLine($"앱 모음에서 게임으로 분류: {gameApps.Count}개 (전체 '게임' 분류 {apps.Count(a => (string)classify.Invoke(null, new[] { a })! == "games")}개)");
            foreach (var a in gameApps.Take(15)) Console.WriteLine($"  {P(a, "Name")} ← {P(a, "Key")}");
            return 0;
        }

        int trashIcons = Array.FindIndex(args, a => a == "--trash-icons");
        if (trashIcons >= 0 && trashIcons + 1 < args.Length) RenderTrashIcons(args[trashIcons + 1]);

        int folderIcons = Array.FindIndex(args, a => a == "--folder-icons");
        if (folderIcons >= 0 && folderIcons + 1 < args.Length) RenderFolderIcons(args[folderIcons + 1]);

        int png = Array.FindIndex(args, a => a == "--png");
        if (png >= 0 && png + 1 < args.Length) RenderPngs(args[png + 1]);
        return _failed == 0 ? 0 : 1;
    }

    // ───────────────────────── 가리기 ─────────────────────────

    private static void RedactionTests()
    {
        var ctx = new ReportRedactor.Context("melon", "DESKTOP-AB12CD",
            new[] { "연봉 협상안.xlsx - Excel", "카카오톡", "ab", "Google", "Windows" });
        string R(string t) => ReportRedactor.Redact(t, ctx);
        string L(string t) => ReportRedactor.RedactLog(t, ctx);

        // 이메일
        Check("이메일", R("답장: hong.gil-dong+x@example.co.kr 로"), "답장: <이메일> 로");
        Check("이메일 여러 개", R("a@b.com, c_d@e.org"), "<이메일>, <이메일>");

        // URL (iCal 비밀 주소 포함)
        Check("iCal https", R("캘린더 https://calendar.google.com/calendar/ical/abc%40group/private-123/basic.ics 실패"), "캘린더 <주소> 실패");
        Check("webcal", R("webcal://p01-caldav.icloud.com/published/2/MTIz"), "<주소>");
        Check("http 와 따옴표", R("열기 \"http://example.com/a?b=c\" 끝"), "열기 \"<주소>\" 끝");
        Check("www", R("www.naver.com 열림"), "<주소> 열림");
        Check("ms-settings 는 그대로", R("ms-settings:privacy-microphone"), "ms-settings:privacy-microphone");

        // 파일·폴더 경로 → <경로>.확장자
        Check("내 프로필 경로", R(@"파일 C:\Users\melon\Desktop\a.txt"), "파일 <경로>.txt");
        Check("회사 폴더 문서", L(@"INFO  [1] 파일 열기 실패: D:\회사\2026 연봉계약서.pdf"), "INFO  [1] 파일 열기 실패: <경로>.pdf");
        Check("공백 있는 사용자 폴더", R(@"C:\Users\홍 길동\AppData\x"), "<경로>");
        Check("JSON 이스케이프 경로", R(@"""C:\\Users\\someone\\x.json"""), @"""<경로>.json""");
        Check("슬래시 경로", R("C:/Users/someone/x"), "<경로>");
        Check("UNC 경로", R(@"열기 \\nas\공유\급여 2026.xlsx"), "열기 <경로>.xlsx");
        Check("장치 이름은 그대로", R(@"전체 화면 앱: True (\\.\DISPLAY1)"), @"전체 화면 앱: True (\\.\DISPLAY1)");
        Check("화살표 두 경로", R(@"적용: C:\a\b.wav → C:\c\d.wav"), "적용: <경로>.wav → <경로>.wav");
        Check("따옴표 안 경로 + 괄호", R(@"알림 소리 적용: ""D:\내 소리\딩동.wav"" (→ C:\Users\melon\x.wav)"), @"알림 소리 적용: ""<경로>.wav"" (→ <경로>.wav)");
        Check("스택 줄", R(@"   at A.B() in C:\dev\x\File.cs:line 288"), "   at A.B() in <경로>.cs:line 288");
        Check("드라이브 없는 Users", R("/Users/someone/Library"), "/Users/<사용자>/Library");
        Check("시각은 그대로", R("2026-10-09 10:00:00.000 INFO"), "2026-10-09 10:00:00.000 INFO");

        // 사용자 이름·기기 이름 (낱말 단위)
        Check("기기 이름", R("기기 DESKTOP-AB12CD 에서"), "기기 <기기> 에서");
        Check("사용자 이름 낱말", R("user melon logged"), "user <사용자> logged");
        Check("다른 낱말 일부는 그대로", R("watermelons"), "watermelons");

        // 창 제목: 로그에만, 3자 이상, 긴 것부터, URL·경로를 먼저 가린 뒤
        Check("앞부분엔 창 제목 안 씀", R("윈도우: Windows 11 Home"), "윈도우: Windows 11 Home");
        Check("로그 창 제목", L("포그라운드: 연봉 협상안.xlsx - Excel"), "포그라운드: <창 제목>");
        Check("로그 짧은 제목", L("카카오톡 열림"), "<창 제목> 열림");
        Check("2자 제목은 무시", L("tab bar"), "tab bar");
        Check("exe 이름은 그대로", L("트레이 아이콘 추가: Google.exe"), "트레이 아이콘 추가: Google.exe");
        Check("프로세스/클래스는 그대로", L("작업 표시줄이 다시 보여 바로 숨김 (Shell_TrayWnd, 포그라운드 google/Chrome_WidgetWin_1)"),
            "작업 표시줄이 다시 보여 바로 숨김 (Shell_TrayWnd, 포그라운드 google/Chrome_WidgetWin_1)");
        Check("AUMID 는 그대로", L("새 알림 1개: Google_pzs8sxrjxfjjc!Google"), "새 알림 1개: Google_pzs8sxrjxfjjc!Google");
        Check("낱말 제목은 가림", L("포그라운드: Google 열림"), "포그라운드: <창 제목> 열림");
        Check("URL 안의 창 제목(순서)", L("가져오기 실패: https://calendar.google.com/x/private-1/basic.ics"), "가져오기 실패: <주소>");

        // 로그: 작은따옴표 안 문구 (줄 안 첫 ' ~ 마지막 ')
        Check("로그 따옴표", L("실행 불가 핀 (이름만): '비밀 문서 - 메모장'"), "실행 불가 핀 (이름만): '<가림>'");
        Check("아포스트로피", L("블루투스 'Melon's AirPods' 연결"), "블루투스 '<가림>' 연결");
        Check("긴 따옴표", L("'" + new string('가', 500) + "'"), "'<가림>'");
        Check("이름 목록", L("기본 고정 앱: '파일 탐색기, 내 비밀 앱'"), "기본 고정 앱: '<가림>'");
        Check("로그 경로+URL", L(@"업데이트 다운로드 완료: https://github.com/x/y.exe → C:\Users\melon\AppData\Local\Temp\y.exe"),
            "업데이트 다운로드 완료: <주소> → <경로>.exe");
        Check("빈 문자열", R(""), "");

        // PC 를 오래 알아볼 값: GUID·긴 숫자 (트레이 "윈도우 ID")
        Check("트레이 윈도우 ID", L("트레이 아이콘 배치: chrome.exe:5 → ⌃ (Default, 윈도우 ID 14798508125447284239)"),
            "트레이 아이콘 배치: chrome.exe:5 → ⌃ (Default, 윈도우 ID <번호>)");
        Check("GUID", L("트레이 아이콘 배치: ba82e2dc-f405-47ad-b032-cf0faa0e3933 → ⌃"), "트레이 아이콘 배치: <번호> → ⌃");
        Check("중괄호 GUID", R("{6CDEC4D3-9697-40DF-B6C2-96E9ED842C0C}"), "<번호>");
        Check("시각·pid·짧은 숫자는 그대로", R("2026-10-09 18:58:32.924 pid 12596 0x1017E 1920×1080"), "2026-10-09 18:58:32.924 pid 12596 0x1017E 1920×1080");
    }

    // 앱 메뉴 규칙 title_en·text_en: 영어면 영어, 한국어면 원문 (MenuRules 는 internal 이라 리플렉션)
    private static void MenuRuleLanguageTests()
    {
        var rules = typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.MenuRules")!;
        string First(string lang)
        {
            Mongdock.Loc.Init(lang);
            object set = rules.GetMethod("LoadEmbedded")!.Invoke(null, null)!;
            var apps = (System.Collections.IEnumerable)set.GetType().GetProperty("Apps")!.GetValue(set)!;
            object chrome = apps.Cast<object>().First();
            var menus = (List<AppMenuDef>)chrome.GetType().GetProperty("Menus")!.GetValue(chrome)!;
            return $"{apps.Cast<object>().Count()} {menus[0].Title} {menus[0].Items[0].Text}";
        }
        Check("앱 메뉴 한국어", First("ko"), "8 파일 새 탭");
        Check("앱 메뉴 영어", First("en"), "8 File New Tab");
        Mongdock.Loc.Init(_lang);
    }

    // 언어 고르기 규칙 (#23): 설정 값·윈도우 문화권 → 지원 코드, 사전 대체 순서
    private static void LanguageRuleTests()
    {
        string N(string v) => Mongdock.Loc.Normalize(v) ?? "(자동)";
        Check("zh-TW → 번체", N("zh-TW"), "zh-Hant");
        Check("zh-HK → 번체", N("zh-HK"), "zh-Hant");
        Check("zh-MO → 번체", N("zh-MO"), "zh-Hant");
        Check("zh-Hant → 번체", N("zh-hant"), "zh-Hant");
        Check("zh-CN → 간체", N("zh-CN"), "zh-Hans");
        Check("zh-SG → 간체", N("zh-SG"), "zh-Hans");
        Check("ja-JP → ja", N("ja-JP"), "ja");
        Check("DE → de", N("DE"), "de");
        Check("fr-CA → fr", N("fr-CA"), "fr");
        Check("es-MX → es", N("es-MX"), "es");
        Check("모르는 언어 pt-BR → 자동", N("pt-BR"), "(자동)");
        Check("이상한 값 → 자동", N("xx-?!"), "(자동)");
        Check("빈 값 → 자동", N(""), "(자동)");
        Mongdock.Loc.Init("de");
        Check("독일어 사전", Mongdock.Loc.T("언어"), "Sprache");
        Check("사전에 없는 문구 → 한국어", Mongdock.Loc.T("사전에 없는 문구"), "사전에 없는 문구");
        Check("독일어 날짜", Mongdock.Loc.DateFull(new DateTime(2026, 10, 9)), "9. Oktober 2026");
        Mongdock.Loc.Init("ja");
        Check("일본어 날짜", Mongdock.Loc.DateFull(new DateTime(2026, 10, 9)), "2026年10月9日");
        Mongdock.Loc.Init(_lang);
    }

    private static void Check(string name, string actual, string expected)
    {
        if (actual == expected) { Console.WriteLine($"  통과  {name}"); return; }
        _failed++;
        Console.WriteLine($"  실패  {name}\n        기대: {expected}\n        실제: {actual}");
    }

    // ───────────────────────── PNG ─────────────────────────

    private static void RenderPngs(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent(); // Themes/Controls.xaml·Menus.xaml (CardButton, IconFont)
        var ctx = new ReportRedactor.Context("melon", "DESKTOP-AB12CD", new[] { "연봉 협상안.xlsx - Excel" });
        string sample = ReportRedactor.Redact(
            "앱: mongdock 0.4.2 (설치 프로그램)\n윈도우: Windows 11 Home 24H2 (26200.6584)\n언어: ko-KR\n모니터 1: 2560×1440, 배율 125%, 주 모니터\n배터리: 없음\n터치: 없음\n\n[설정 요약]\n독: 켜짐, 위치 Bottom, 모드 Reserve, 테마 System, 아이콘 52\n\n[최근 로그]\n", ctx)
            + ReportRedactor.RedactLog(@"2026-10-09 10:00:00.000 INFO  [1] 파일 열기 실패: C:\Users\melon\Desktop\연봉 협상안.xlsx - Excel", ctx) + "\n"
            + ReportRedactor.RedactLog("2026-10-09 10:00:01.000 WARN  [7] 캘린더 'Work' 가져오기 실패: https://calendar.google.com/x/private-1/basic.ics", ctx);

        foreach (var theme in new[] { DockTheme.Light, DockTheme.Dark })
        {
            var settings = new Settings();
            settings.Dock.Theme = theme;
            foreach (bool open in new[] { false, true })
            {
                var w = new ReportWindow(settings)
                {
                    Left = -20000,
                    Top = -20000,
                    ShowActivated = false,
                };
                w.Show();
                w.SetDiagnostics(sample);
                w.SetPreviewState(ReportKind.Bug, open ? "" : "독에서 카카오톡이 안 열려요", "독에서 카카오톡 아이콘을 누르면 창이 안 떠요.\n다시 누르면 떠요.", open ? "me@example.com" : "", open);
                if (open)
                {
                    // 첨부 (#26): 화면 캡처 같은 그림 + 로그 파일
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xB9, 0xA8, 0xF0), Color.FromRgb(0x9E, 0xD8, 0xF5), 30), null, new Rect(0, 0, 1600, 900));
                        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(200, 150, 900, 560), 16, 16);
                    }
                    var rtb = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    var enc = new JpegBitmapEncoder { QualityLevel = 85 };
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using var ms = new MemoryStream();
                    enc.Save(ms);
                    w.AddPreviewAttachment(new Mongdock.Services.ReportAttachment("screen-20261013-131500.jpg", "image/jpeg", ms.ToArray()));
                    w.AddPreviewAttachment(new Mongdock.Services.ReportAttachment("kakao.log", "text/plain", new byte[48_000]));
                    // 영역 캡처 기다리는 중 줄 (#13)
                    ((FrameworkElement)typeof(ReportWindow).GetField("_clipWaiting", Any)!.GetValue(w)!).Visibility = Visibility.Visible;
                }
                w.UpdateLayout();
                string name = $"report-{(Mongdock.Loc.IsKorean ? "" : Mongdock.Loc.Code + "-")}{theme.ToString().ToLowerInvariant()}{(open ? "-details" : "")}.png";
                Save(w, Path.Combine(dir, name));
                w.Close();
                Console.WriteLine($"  저장  {name}");
            }
        }
    }

    /// <summary>
    /// 독 "앱 모음" 아이콘 미리 보기 (#d26): 둥근 사각형·원본 스타일 × 16~128px, 라이트/다크 배경 + 16·24·32px 4배 확대(픽셀 확인).
    /// 앱 쪽 그리기 함수는 internal 이라 리플렉션.
    /// </summary>
    private static void RenderAllAppsIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        var asm = typeof(Mongdock.App).Assembly;
        var mac = (ImageSource)asm.GetType("Mongdock.Services.MacIconRenderer")!.GetMethod("AllApps")!.Invoke(null, null)!;
        var flat = (ImageSource)asm.GetType("Mongdock.Services.IconService")!
            .GetMethod("CreateAllAppsIcon", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128 };
        foreach (var (name, bg) in new[] { ("light", Color.FromRgb(0xF2, 0xF2, 0xF6)), ("dark", Color.FromRgb(0x1E, 0x1E, 0x22)) })
        {
            const int pad = 16, rowH = 150;
            int width = pad + sizes.Sum(s => s + pad) + 3 * (32 * 4 + pad);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, width, rowH * 2));
                for (int row = 0; row < 2; row++)
                {
                    var img = row == 0 ? mac : flat;
                    double x = pad, baseY = row * rowH + rowH - pad;
                    foreach (int s in sizes)
                    {
                        // 앱처럼 큰 그림을 그 크기로 줄여서 (독은 256 을 슬롯 크기로 그림)
                        dc.DrawImage(Downscale(img, s), new Rect(x, baseY - s, s, s));
                        x += s + pad;
                    }
                    foreach (int s in new[] { 16, 24, 32 })
                    {
                        var small = Downscale(img, s);
                        var zoom = new DrawingGroup();
                        RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor);
                        zoom.Children.Add(new ImageDrawing(small, new Rect(0, 0, s * 4, s * 4)));
                        dc.PushTransform(new TranslateTransform(x, baseY - s * 4));
                        dc.DrawDrawing(zoom);
                        dc.Pop();
                        x += 32 * 4 + pad;
                    }
                }
            }
            var rtb = new RenderTargetBitmap(width, rowH * 2, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string path = Path.Combine(dir, $"all-apps-{name}.png");
            using (var fs = File.Create(path)) enc.Save(fs);
            Console.WriteLine($"  저장  {path}");
        }
    }

    /// <summary>
    /// 독 폴더 아이콘 (#24-B): 이전 것(윈도우 폴더 아이콘) + 몽독 폴더(일반·알려진 폴더 종류별·없어진 폴더).
    /// 라이트·다크 독 위에 52px·확대(94px)·2배 화면(104·188px) 으로 한 장씩, 그리고 시안마다 256 원본.
    /// </summary>
    private static void RenderFolderIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        var asm = typeof(Mongdock.App).Assembly;
        var mac = asm.GetType("Mongdock.Services.MacIconRenderer")!;
        var svc = asm.GetType("Mongdock.Services.DockFolderService")!;
        var glyphType = asm.GetType("Mongdock.Services.FolderGlyph")!;
        BitmapSource? Thumb(string p) => (BitmapSource?)svc.GetMethod("Thumbnail")!.Invoke(null, new object[] { p, 160 });
        BitmapSource Folder(string g, bool missing = false) => (BitmapSource)mac.GetMethod("Folder")!.Invoke(null, new object?[] { Enum.Parse(glyphType, g), missing, null })!;

        // 비교용 지금(이전) 아이콘: 폴더 셸 아이콘을 맥 판에
        var shellFolder = Thumb(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var nowFolder = shellFolder is null ? null : (BitmapSource?)mac.GetMethod("Normalize")!.Invoke(null, new object[] { shellFolder });

        var rows = new List<(string Label, BitmapSource? Img, string File)>
        {
            ("이전: 윈도우 폴더 아이콘", nowFolder, "now-folder"),
            ("일반 폴더 (A)", Folder("None"), "A-folder"),
            ("다운로드", Folder("Downloads"), "C-downloads"),
            ("문서", Folder("Documents"), "C-documents"),
            ("사진", Folder("Pictures"), "C-pictures"),
            ("바탕 화면", Folder("Desktop"), "C-desktop"),
            ("음악", Folder("Music"), "C-music"),
            ("동영상", Folder("Videos"), "C-videos"),
            ("없어진 폴더", Folder("Downloads", true), "missing"),
            ("휴지통 (빔)", (BitmapSource)mac.GetMethod("Trash")!.Invoke(null, new object?[] { false, null })!, "trash-empty"),
            ("휴지통 (참)", (BitmapSource)mac.GetMethod("Trash")!.Invoke(null, new object?[] { true, null })!, "trash-full"),
        };
        foreach (var (_, img, file) in rows)
            if (img is not null) SavePng(img, Path.Combine(dir, $"{file}-256.png"));

        RenderPickerSamples(dir, mac, glyphType);

        int[] sizes = { 52, 94, 104, 188 };
        var allApps = (ImageSource)mac.GetMethod("AllApps")!.Invoke(null, null)!;
        foreach (var (name, bg, dock, ink) in new[]
        {
            ("light", Color.FromRgb(0xE9, 0xEC, 0xF4), Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF), Color.FromRgb(0x22, 0x22, 0x28)),
            ("dark", Color.FromRgb(0x14, 0x16, 0x1C), Color.FromArgb(0xC8, 0x2C, 0x2C, 0x33), Color.FromRgb(0xEE, 0xEE, 0xF2)),
        })
        {
            const int pad = 18, labelW = 290;
            int rowH = sizes.Max() + pad * 2;
            int width = labelW + sizes.Sum(s => s + pad) + 52 + pad * 3;
            int height = rows.Count * rowH;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, width, height));
                for (int i = 0; i < rows.Count; i++)
                {
                    var (label, img, _) = rows[i];
                    double y0 = i * rowH, baseY = y0 + rowH - pad;
                    var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface("Malgun Gothic"), 15, new SolidColorBrush(ink), 1.0);
                    dc.DrawText(text, new Point(pad, y0 + (rowH - text.Height) / 2));
                    // 독 판 (앱 모음 아이콘 옆에 놓아 비교)
                    double dockW = 52 * 2 + pad * 3, dockX = labelW - pad;
                    dc.DrawRoundedRectangle(new SolidColorBrush(dock), null, new Rect(dockX, baseY - 52 - 10, dockW, 52 + 20), 16, 16);
                    dc.DrawImage(Downscale(allApps, 52), new Rect(dockX + pad, baseY - 52, 52, 52));
                    double x = dockX + pad * 2 + 52;
                    if (img is null) continue;
                    foreach (int s in sizes)
                    {
                        dc.DrawImage(Downscale(img, s), new Rect(x, baseY - s, s, s));
                        x += s + pad;
                    }
                }
            }
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            string path = Path.Combine(dir, $"folder-icons-{name}.png");
            SavePng(rtb, path);
            Console.WriteLine($"  저장  {path}");
        }
    }

    /// <summary>아이콘 고르기 견본: 기호 30개(몽독 판) + 판 색 8가지(폴더 그대로 / 글자) — 52px.</summary>
    private static void RenderPickerSamples(string dir, Type mac, Type glyphType)
    {
        var renderer = typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.PinIconRenderer")!;
        var glyphs = (string[])renderer.GetField("Glyphs")!.GetValue(null)!;
        var colors = (string[])renderer.GetField("Colors")!.GetValue(null)!;
        var symbol = mac.GetMethod("Symbol")!;
        var folder = mac.GetMethod("Folder")!;
        const int s = 52, pad = 12, cols = 10;
        int rows = (glyphs.Length + cols - 1) / cols + 3;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF4)), null, new Rect(0, 0, pad + cols * (s + pad), pad + rows * (s + pad)));
            for (int i = 0; i < glyphs.Length; i++)
                dc.DrawImage(Downscale((ImageSource)symbol.Invoke(null, new object?[] { "mongdock", glyphs[i], null })!, s), new Rect(pad + i % cols * (s + pad), pad + i / cols * (s + pad), s, s));
            int r0 = (glyphs.Length + cols - 1) / cols;
            for (int i = 0; i < colors.Length; i++)
            {
                dc.DrawImage(Downscale((ImageSource)folder.Invoke(null, new object?[] { Enum.Parse(glyphType, "Downloads"), false, colors[i] })!, s), new Rect(pad + i * (s + pad), pad + r0 * (s + pad), s, s));
                dc.DrawImage(Downscale((ImageSource)symbol.Invoke(null, new object?[] { colors[i], null, i % 2 == 0 ? "업" : "W" })!, s), new Rect(pad + i * (s + pad), pad + (r0 + 1) * (s + pad), s, s));
                dc.DrawImage(Downscale((ImageSource)symbol.Invoke(null, new object?[] { colors[i], glyphs[i * 3], null })!, s), new Rect(pad + i * (s + pad), pad + (r0 + 2) * (s + pad), s, s));
            }
        }
        var rtb = new RenderTargetBitmap(pad + cols * (s + pad), pad + rows * (s + pad), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        string path = Path.Combine(dir, "icon-picker-samples.png");
        SavePng(rtb, path);
        Console.WriteLine($"  저장  {path}");
    }

    /// <summary>
    /// 판 없는 휴지통 시안 (--trash-icons DIR): A 유리·메시 / B 흰 통 + 줄무늬 × 빈·찬. 라이트·다크 독 위에서
    /// 앱 모음·다운로드 폴더 옆 52px, 확대 94px, 그리고 256 낱장.
    /// </summary>
    private static void RenderTrashIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        var mac = typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.MacIconRenderer")!;
        var glyphType = typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.FolderGlyph")!;
        ImageSource Trash(bool full, bool glass) => (ImageSource)mac.GetMethod("TrashStandalone")!.Invoke(null, new object[] { full, glass })!;
        var allApps = (ImageSource)mac.GetMethod("AllApps")!.Invoke(null, null)!;
        var downloads = (ImageSource)mac.GetMethod("Folder")!.Invoke(null, new object?[] { Enum.Parse(glyphType, "Downloads"), false, null })!;
        var variants = new[] { ("A 유리 통", true), ("B 흰 통 + 줄무늬", false) };
        foreach (var (label, glass) in variants)
            foreach (bool full in new[] { false, true })
                SavePng((BitmapSource)Trash(full, glass), Path.Combine(dir, $"{(glass ? "A" : "B")}-{(full ? "full" : "empty")}-256.png"));
        foreach (var (theme, bg, dock, ink) in new[]
        {
            ("light", Color.FromRgb(0xE9, 0xEC, 0xF4), Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF), Color.FromRgb(0x22, 0x22, 0x28)),
            ("dark", Color.FromRgb(0x14, 0x16, 0x1C), Color.FromArgb(0xC8, 0x2C, 0x2C, 0x33), Color.FromRgb(0xEE, 0xEE, 0xF2)),
        })
        {
            const int pad = 18, rowH = 150, labelW = 190;
            int width = labelW + (52 + pad) * 5 + 94 * 2 + pad * 4;
            int height = rowH * 2;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, width, height));
                for (int i = 0; i < variants.Length; i++)
                {
                    var (label, glass) = variants[i];
                    double y0 = i * rowH, baseY = y0 + rowH - pad - 20;
                    dc.DrawText(new System.Windows.Media.FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface("Malgun Gothic"), 15, new SolidColorBrush(ink), 1.0), new Point(pad, y0 + rowH / 2 - 10));
                    // 독: 앱 모음 · 다운로드 · | · 빈 휴지통 · 찬 휴지통
                    double x = labelW;
                    double dockW = (52 + pad) * 4 + pad * 2;
                    dc.DrawRoundedRectangle(new SolidColorBrush(dock), null, new Rect(x - pad, baseY - 52 - 10, dockW, 72), 16, 16);
                    dc.DrawImage(Downscale(allApps, 52), new Rect(x, baseY - 52, 52, 52)); x += 52 + pad;
                    dc.DrawImage(Downscale(downloads, 52), new Rect(x, baseY - 52, 52, 52)); x += 52 + pad;
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x50, ink.R, ink.G, ink.B)), null, new Rect(x - pad / 2 - 1, baseY - 46, 1.5, 40));
                    dc.DrawImage(Downscale(Trash(false, glass), 52), new Rect(x, baseY - 52, 52, 52)); x += 52 + pad;
                    dc.DrawImage(Downscale(Trash(true, glass), 52), new Rect(x, baseY - 52, 52, 52)); x += 52 + pad * 3;
                    // 확대(1.8배)
                    dc.DrawImage(Downscale(Trash(false, glass), 94), new Rect(x, baseY - 94 + 10, 94, 94)); x += 94 + pad;
                    dc.DrawImage(Downscale(Trash(true, glass), 94), new Rect(x, baseY - 94 + 10, 94, 94));
                }
            }
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            string path = Path.Combine(dir, $"trash-icons-{theme}.png");
            SavePng(rtb, path);
            Console.WriteLine($"  저장  {path}");
        }
    }

    private static void SavePng(BitmapSource img, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>그림을 size x size 픽셀로 고품질 축소 (독 슬롯에 그리는 것과 같게).</summary>
    private static BitmapSource Downscale(ImageSource src, int size)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, size, size));
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// 설정 창 페이지마다 PNG (#23 — 언어별 글자 잘림 확인). 앱과 같은 서비스를 만들되 Start 하지 않음(훅·폴링 없음),
    /// 데이터 폴더는 임시(MONGDOCK_DATA_DIR, Main 맨 앞에서). 페이지 본문(스크롤 안 내용)을 끝까지 그림.
    /// </summary>
    /// <summary>설정 → 단축키 그림용 상태 (시험 데이터 폴더 설정): 앱 모음 판 Ctrl+Alt+A, 제어 센터 Alt+C(경고), 알림 받는 중,
    /// 루틴 하나 Ctrl+Alt+1 + 막힘 안내, 검색 겹침 안내([바꾸기]).</summary>
    private static void HotkeyPageStates(Window w, SettingsService settings)
    {
        var s = settings.Current;
        s.Hotkeys.AllApps = new HotkeyBinding { On = true, Key = "Ctrl+Alt+A" };
        s.Hotkeys.ControlCenter = new HotkeyBinding { On = true, Key = "Alt+C" };
        s.Hotkeys.Notifications = new HotkeyBinding { On = true, Key = null };
        if (s.Routines.Count == 0)
        {
            s.Routines.Add(new RoutineDef { Id = "shot-r1", Name = Mongdock.Loc.T("업무 시작"), Hotkey = new HotkeyBinding { On = true, Key = "Ctrl+Alt+1" } });
            s.Routines.Add(new RoutineDef { Id = "shot-r2", Name = Mongdock.Loc.T("공부") });
        }
        var t = w.GetType();
        t.GetField("_recordingSlot", Any)!.SetValue(w, "notifications");
        var notes = (System.Collections.IDictionary)t.GetField("_hotkeyNotes", Any)!.GetValue(w)!;
        var noteType = t.GetNestedType("HotkeyNote", Any)!;
        object Note(string text, bool error, string? from = null, HotkeyChord? chord = null) => Activator.CreateInstance(noteType, text, error, from, chord)!;
        notes.Clear();
        notes["controlCenter"] = Note(Mongdock.Loc.T("앱 메뉴 단축키(Alt+글자)와 겹칠 수 있어요."), false);
        var cc = HotkeyChord.Parse("Ctrl+C")!.Value;
        notes["routine:shot-r2"] = Note(Mongdock.Loc.F($"{cc.Display} — {Mongdock.Loc.T("다른 앱이 흔히 쓰는 키예요 (복사·붙여넣기·저장 같은).")}"), true);
        var ca = HotkeyChord.Parse("Ctrl+Alt+A")!.Value;
        notes["search"] = Note(Mongdock.Loc.F($"{ca.Display} — 이미 「{Mongdock.Loc.T("앱 모음 판")}」에 쓰고 있어요."), true, "allApps", ca);
    }

    private static void RenderSettingsPages(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown; // 렌더링 중 창이 닫혀도 앱이 꺼지지 않게
        var settings = new SettingsService();
        Mongdock.ViewModels.UiFonts.Apply(settings.Current);
        var tracker = new WindowTracker();
        var launcher = new AppLauncher(tracker);
        var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
            new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
            new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
        var type = typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.SettingsWindow")!; // internal
        foreach (var theme in new[] { "light", "dark" }) // 라이트·다크 (몽독 톤 전/후 비교)
        {
        settings.Current.Dock.Theme = theme == "dark" ? DockTheme.Dark : DockTheme.Light;
        Mongdock.ViewModels.UiTheme.Apply(settings.Current);
        var w = (Window)type.GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, new[] { typeof(AppServices) })!.Invoke(new object[] { services });
        w.Left = -20000; w.Top = -20000; w.ShowActivated = false;
        w.Show();
        var pageType = type.GetNestedType("Page", System.Reflection.BindingFlags.NonPublic)!;
        var go = type.GetMethod("GoToPage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var rebuild = type.GetMethod("Rebuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var page in Enum.GetValues(pageType))
        {
            go.Invoke(w, new[] { page });
            if (page.ToString() == "Hotkeys") HotkeyPageStates(w, settings); // 0.6: 켠 키·경고·막힘·겹침·키 받는 중을 한 장에
            // GoToPage 는 한 박자 늦게 다시 그림 → 바로 다시 그리게
            rebuild.Invoke(w, null);
            w.UpdateLayout(); // 디스패처를 돌리지 않음 — 돌리면 App.OnStartup 이 실행돼 (실행 중인 몽독이 있으면) 앱이 종료됨
            // 가장 큰 ScrollViewer 의 내용 = 페이지 본문
            var scroll = Descendants(w).OfType<System.Windows.Controls.ScrollViewer>().OrderByDescending(s => s.ActualWidth * s.ActualHeight).FirstOrDefault();
            if (scroll?.Content is not FrameworkElement body) continue;
            double width = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
            body.Measure(new Size(width, double.PositiveInfinity));
            double height = Math.Max(body.DesiredSize.Height, 50);
            body.Arrange(new Rect(0, 0, width, height));
            body.UpdateLayout();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(body) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.None }, null, new Rect(0, 0, width, height));
            }
            const double scale = 1.25;
            var rtb = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string path = Path.Combine(dir, $"settings-{_lang}-{theme}-{page.ToString()!.ToLowerInvariant()}.png");
            using (var fs = File.Create(path)) enc.Save(fs);
            Console.WriteLine($"  저장  {path}");
            if (Convert.ToInt32(page) == 0 && w.Content is FrameworkElement whole && whole.ActualWidth > 0)
            {
                // 왼쪽 메뉴까지 보이는 창 전체 한 장 (메뉴 아이콘 확인용)
                var all = new RenderTargetBitmap((int)(whole.ActualWidth * scale), (int)(whole.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                var wv = new DrawingVisual();
                using (var dc = wv.RenderOpen())
                {
                    dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, whole.ActualWidth, whole.ActualHeight));
                    dc.DrawRectangle(new VisualBrush(whole) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, whole.ActualWidth, whole.ActualHeight));
                }
                all.Render(wv);
                var enc2 = new PngBitmapEncoder();
                enc2.Frames.Add(BitmapFrame.Create(all));
                string wpath = Path.Combine(dir, $"settings-{_lang}-{theme}-window.png");
                using (var fs = File.Create(wpath)) enc2.Save(fs);
                Console.WriteLine($"  저장  {wpath}");
                // 다크 메뉴 아이콘 판 비교: 지금 / A 깊은 라벤더 / B 지금 그라데이션 70% — 사이드바만 나란히
                if (theme == "dark")
                {
                    var variantField = type.GetField("DarkTileVariant", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                    const double cw = 200, ch = 430, zz = 2;
                    var combo = new RenderTargetBitmap((int)(cw * 3 * zz), (int)(ch * zz), 96 * zz, 96 * zz, PixelFormats.Pbgra32);
                    var vv = new DrawingVisual();
                    using (var dc = vv.RenderOpen())
                    {
                        int k = 0;
                        foreach (var variant in new[] { "now", "A", "B" })
                        {
                            variantField.SetValue(null, variant);
                            rebuild.Invoke(w, null);
                            w.UpdateLayout();
                            var cur = (FrameworkElement)w.Content;
                            var snap = new RenderTargetBitmap((int)(cw * zz), (int)(ch * zz), 96 * zz, 96 * zz, PixelFormats.Pbgra32);
                            var one = new DrawingVisual();
                            using (var d1 = one.RenderOpen())
                                d1.DrawRectangle(new VisualBrush(cur) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, cw, ch) }, null, new Rect(0, 0, cw, ch));
                            snap.Render(one);
                            snap.Freeze();
                            dc.DrawImage(snap, new Rect(k * cw, 0, cw, ch));
                            k++;
                        }
                    }
                    combo.Render(vv);
                    variantField.SetValue(null, "now");
                    rebuild.Invoke(w, null);
                    w.UpdateLayout();
                    var enc4 = new PngBitmapEncoder();
                    enc4.Frames.Add(BitmapFrame.Create(combo));
                    string vpath = Path.Combine(dir, "settings-dark-icons-now-A-B.png");
                    using (var fs = File.Create(vpath)) enc4.Save(fs);
                    Console.WriteLine($"  저장  {vpath}");
                }
                // 제목 줄 위치 판정 (WM_NCHITTEST): 위 띠 = 끌기(2), 최대화 동그라미 = 스냅(9), 닫기 = 내용(1), 오른쪽 끝 = 크기 조절(11), 아래 오른쪽 = 17
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    var dpiS = VisualTreeHelper.GetDpi(w);
                    int Hit(Point dip)
                    {
                        var sp = w.PointToScreen(dip);
                        long lp = ((long)(short)sp.Y << 16) | (ushort)(short)sp.X;
                        return (int)NcHit(hwnd, 0x0084, IntPtr.Zero, new IntPtr(lp));
                    }
                    double ww = w.ActualWidth, wh = w.ActualHeight;
                    var checks = new (string Name, Point At, int Want)[]
                    {
                        ("위 띠 가운데", new Point(ww / 2, 18), 2), ("최대화 동그라미", new Point(ww - 14 - 20 - 6, 18), 9),
                        ("닫기 동그라미", new Point(ww - 14 - 6, 18), 1), ("최소화 동그라미", new Point(ww - 14 - 40 - 6, 18), 1),
                        ("왼쪽 위 (예전 동그라미 자리)", new Point(14 + 6, 18), 2), ("본문", new Point(ww / 2, wh / 2), 1),
                        ("오른쪽 끝", new Point(ww - 2, wh / 2), 11), ("오른쪽 아래", new Point(ww - 2, wh - 2), 17),
                    };
                    foreach (var (n, at, want) in checks)
                    {
                        int got = Hit(at);
                        Console.WriteLine($"  {(got == want ? "통과" : "실패")}  제목 줄 판정 {n}: {got} (기대 {want}) — 배율 {dpiS.DpiScaleX:0.##}");
                    }
                }
                // 제목 줄 동그라미 3가지 상태 (활성 / 비활성 / 호버) — 오른쪽 위만 크게 (0.5.1: 최소화·최대화·닫기가 오른쪽 위)
                var preview = typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.MongTitleBar")!.GetMethod("Preview", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                foreach (var (state, act, hov) in new[] { ("active", true, false), ("inactive", false, false), ("hover", true, true) })
                {
                    preview.Invoke(null, new object[] { w, act, hov });
                    whole = (FrameworkElement)w.Content; // 페이지를 다시 그리면 내용이 바뀜 — 지금 동그라미가 든 내용으로 (예전엔 다크가 회색으로 나옴)
                    whole.UpdateLayout();
                    const double z = 3;
                    // 창 전체를 그린 뒤 오른쪽 위 260×60 만 잘라 냄 (VisualBrush 뷰박스는 가끔 빈 그림이 나옴)
                    var full = new RenderTargetBitmap((int)Math.Ceiling(whole.ActualWidth * z), (int)Math.Ceiling(whole.ActualHeight * z), 96 * z, 96 * z, PixelFormats.Pbgra32);
                    var cv = new DrawingVisual();
                    using (var dc = cv.RenderOpen())
                    {
                        dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, whole.ActualWidth, whole.ActualHeight));
                        dc.DrawRectangle(new VisualBrush(whole) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, whole.ActualWidth, whole.ActualHeight));
                    }
                    full.Render(cv);
                    var crop = new CroppedBitmap(full, new Int32Rect(full.PixelWidth - (int)(260 * z), 0, (int)(260 * z), (int)(60 * z)));
                    var enc3 = new PngBitmapEncoder();
                    enc3.Frames.Add(BitmapFrame.Create(crop));
                    string cpath = Path.Combine(dir, $"titlebar-{_lang}-{theme}-{state}.png");
                    using (var fs = File.Create(cpath)) enc3.Save(fs);
                    Console.WriteLine($"  저장  {cpath}");
                }
            }
        }
        w.Close();
        }
        GC.KeepAlive(app);
    }

    /// <summary>
    /// --dockfit: 이 PC 사용자 settings.json(읽기만)의 독 값과 주 모니터 작업 영역으로 실제 DockFit 계산 — 아이콘 수별 줄인 크기·간격·"⋯" 칸 수,
    /// 그리고 확대해도 끝 아이콘이 화면 안에 드는지 (#25 사용자: 오른쪽 세로 독 위·아래 잘림).
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr NcHit(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// --clipdemo: 오른쪽 세로 독 구조(창 Grid → 오른쪽 정렬 판 Border → StackPanel → 확대 아이콘)에서 판이 원하는 길이가 창보다 길 때
    /// 판 밖(왼쪽)으로 튀어나온 확대 아이콘이 잘리는지 (WPF 배치 잘림). 왼쪽 튀어나온 자리의 픽셀이 칠해졌으면 "보임".
    /// </summary>
    /// <summary>
    /// --suggest: 루틴 추천(spec-routines §15) 판정표 — 가짜 실행 기록으로 RoutineSuggest.Find 를 돌려 기대와 맞는지.
    /// (설치 3일·주 1회·전체 화면·방해 금지·일시 정지·3번 거절은 묻는 단계에서 거름 — 표 아래 따로)
    /// </summary>
    /// <summary>--types: 판 글자 배율 안 A/B/C 그림만 (빠르게).</summary>
    private static bool _typesOnly;

    /// <summary>
    /// 잠자는 앱 지우기 시험 (--uninstall [결과 파일]): 진짜 앱은 실행하지 않고, 시험용 제거 프로그램 레지스트리 항목(HKCU, mongdock-test-*)만 만들어
    /// 그 UninstallString(cmd·reg 만 씀)을 돌림. 끝나면 시험 항목을 모두 지움.
    /// 경우: 지움 / 아무것도 안 함(= 사용자 취소) / 자식을 띄우고 먼저 끝남(NSIS 처럼) / 같은 묶음 앱 둘 = 한 번 / 보호된 앱 건너뜀 / [나머지 취소].
    /// </summary>
    private static int UninstallTest(string? outFile)
    {
        var asm = typeof(Mongdock.App).Assembly;
        var un = asm.GetType("Mongdock.Services.AppUninstall")!;
        var removalType = un.GetNestedType("Removal", System.Reflection.BindingFlags.NonPublic)!;
        var rowType = asm.GetType("Mongdock.Services.UnusedApps")!.GetNestedType("Row")!;
        var entryType = asm.GetType("Mongdock.Services.AppEntry")!;
        const string root = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        var lines = new List<string>();
        int fail = 0;
        void Check(string what, bool ok) { if (!ok) fail++; lines.Add($"{(ok ? "통과" : "실패")}  {what}"); Console.WriteLine(lines[^1]); }
        string Key(string id) => "mongdock-test-" + id;
        void Make(string id, string cmd)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + "\\" + Key(id));
            k.SetValue("DisplayName", "mongdock test " + id);
            k.SetValue("UninstallString", cmd);
        }
        string Del(string id) => $"reg delete \"HKCU\\{root}\\{Key(id)}\" /f";
        object Entry(string id) => System.Runtime.Serialization.FormatterServices.GetUninitializedObject(entryType) is var e && SetKey(e, "test:" + id) ? e : e;
        bool SetKey(object e, string key)
        {
            foreach (var (name, value) in new[] { ("Key", key), ("Name", key) })
            {
                var f = entryType.GetField($"<{name}>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                f?.SetValue(e, value);
            }
            return true;
        }
        object Removal(string id, string? cmd, string? cannot = null, string? group = null) =>
            Activator.CreateInstance(removalType, "uninstall:" + (group ?? id), "test " + (group ?? id), cmd, "HKCU|" + Key(group ?? id), null, cannot)!;
        object Row(string id, object removal) => Activator.CreateInstance(rowType, Entry(id), null, (long?)1000, null, null, removal)!;
        var plan = un.GetMethod("Plan")!;
        var run = un.GetMethod("RunAsync")!;
        un.GetProperty("WaitLimit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, TimeSpan.FromSeconds(40));
        (int Removed, int Failed, int Settings, int Skipped) Run(List<object> rows, int cancelAfter = -1)
        {
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType))!;
            foreach (var r in rows) list.Add(r);
            var jobs = plan.Invoke(null, new object[] { list })!;
            using var cts = new CancellationTokenSource();
            Action<int, int, string> progress = (i, n, name) => { if (i - 1 == cancelAfter) cts.Cancel(); };
            var task = (Task)run.Invoke(null, new object[] { jobs, progress, cts.Token })!;
            task.GetAwaiter().GetResult();
            var res = task.GetType().GetProperty("Result")!.GetValue(task)!;
            int P(string n) => (int)res.GetType().GetProperty(n)!.GetValue(res)!;
            return (P("Removed"), P("Failed"), P("Settings"), P("Skipped"));
        }
        bool Exists(string id) { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + "\\" + Key(id)); return k is not null; }
        var ids = new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l" };
        try
        {
            // 1) 제거 프로그램이 자기 항목을 지움 → 지움
            Make("a", $"cmd.exe /c {Del("a")}");
            var r1 = Run(new() { Row("a", Removal("a", $"cmd.exe /c {Del("a")}")) });
            Check($"제거 프로그램이 지움 → 지움 1 (지움 {r1.Removed}, 실패 {r1.Failed})", r1.Removed == 1 && r1.Failed == 0 && !Exists("a"));
            // 2) 아무것도 안 하고 끝남 (= 사용자가 제거 창에서 취소) → 실패
            Make("b", "cmd.exe /c exit 0");
            var r2 = Run(new() { Row("b", Removal("b", "cmd.exe /c exit 0")) });
            Check($"제거 창 취소(항목 남음) → 실패 1 (지움 {r2.Removed}, 실패 {r2.Failed})", r2.Removed == 0 && r2.Failed == 1 && Exists("b"));
            // 3) 자식을 띄우고 먼저 끝남, 자식이 3초 뒤 지움 (NSIS 처럼) → 자식까지 기다려 지움
            string late = $"cmd.exe /c start \"\" /min cmd.exe /c \"ping -n 4 127.0.0.1 >nul & {Del("c")}\"";
            Make("c", late);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r3 = Run(new() { Row("c", Removal("c", late)) });
            Check($"자식 프로세스가 늦게 지움 → 기다려서 지움 1 ({sw.Elapsed.TotalSeconds:0.0}초)", r3.Removed == 1 && !Exists("c") && sw.Elapsed.TotalSeconds >= 2.5);
            // 4) 같은 묶음 앱 둘 → 제거 프로그램 한 번 (두 번 돌면 두 번째는 항목이 없어 reg 가 실패하지만 결과는 지움 2 가 됨)
            string cnt = Path.Combine(Path.GetTempPath(), "mongdock-test-count.txt");
            File.Delete(cnt);
            string bundleCmd = $"cmd.exe /c echo x>>\"{cnt}\" & {Del("d")}";
            Make("d", bundleCmd);
            var r4 = Run(new() { Row("d1", Removal("d1", bundleCmd, group: "d")), Row("d2", Removal("d2", bundleCmd, group: "d")) });
            int runs = File.Exists(cnt) ? File.ReadAllLines(cnt).Length : 0;
            Check($"같은 묶음 앱 둘 → 제거 프로그램 1번 (실행 {runs}번, 지움 {r4.Removed})", runs == 1 && r4.Removed == 1);
            File.Delete(cnt);
            // 5) 보호된 앱·제거 프로그램 없음 → 건너뜀 (실행 안 함)
            Make("e", $"cmd.exe /c {Del("e")}");
            var r5 = Run(new() { Row("e", Removal("e", $"cmd.exe /c {Del("e")}", cannot: "보호된 앱")), Row("f", Removal("f", null, cannot: "제거 프로그램 없음")) });
            Check($"보호된 앱·제거 프로그램 없음 → 건너뜀 2, 실행 안 함 (건너뜀 {r5.Skipped}, 항목 남음 {Exists("e")})", r5.Skipped == 2 && r5.Removed == 0 && Exists("e"));
            // 6) [나머지 취소]: 첫 번째가 도는 중 취소 → 첫 번째는 끝까지, 나머지는 실행 안 함(실패로 셈)
            Make("g", $"cmd.exe /c {Del("g")}");
            Make("a", $"cmd.exe /c {Del("a")}");
            var r6 = Run(new() { Row("g", Removal("g", $"cmd.exe /c {Del("g")}")), Row("a", Removal("a", $"cmd.exe /c {Del("a")}")) }, cancelAfter: 0);
            Check($"[나머지 취소] → 지움 1·남음 1, 두 번째는 실행 안 함 (지움 {r6.Removed}, 실패 {r6.Failed}, 두 번째 항목 남음 {Exists("a")})", r6.Removed == 1 && r6.Failed == 1 && Exists("a"));
            // 8) 따옴표 없는 공백 경로 (실제 앱에 흔함): cmd.exe 를 "공백 있는 폴더\fake uninst.exe" 로 복사해 실행
            string fakeDir = Path.Combine(Path.GetTempPath(), "mongdock test dir");
            Directory.CreateDirectory(fakeDir);
            string fakeExe = Path.Combine(fakeDir, "fake uninst.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeExe, true);
            string unquoted = $"{fakeExe} /c {Del("h")}";
            Make("h", unquoted);
            var r8 = Run(new() { Row("h", Removal("h", unquoted)) });
            Check($"따옴표 없는 공백 경로 → 실행되어 지움 (지움 {r8.Removed})", r8.Removed == 1 && !Exists("h"));
            // 9) 항목을 먼저 지우고 완료 창을 띄운 채 있는 제거 프로그램 → 항목이 사라지면 바로 다음으로 (예전엔 창이 닫힐 때까지 기다림)
            string stay = $"cmd.exe /c {Del("i")} & ping -n 20 127.0.0.1 >nul";
            Make("i", stay);
            sw.Restart();
            var r9 = Run(new() { Row("i", Removal("i", stay)) });
            Check($"항목 먼저 사라짐 + 제거 프로그램 계속 열림 → 바로 지움 ({sw.Elapsed.TotalSeconds:0.0}초)", r9.Removed == 1 && sw.Elapsed.TotalSeconds < 6);
            // 10) 제거 프로그램이 브라우저(설문 페이지)를 띄우고 끝남, 항목은 남음 → 브라우저는 기다리지 않음 (예전엔 브라우저가 닫힐 때까지 "멈춤")
            string fakeBrowser = Path.Combine(fakeDir, "msedge.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeBrowser, true);
            string survey = $"cmd.exe /c start \"\" /min \"{fakeBrowser}\" /c ping -n 25 127.0.0.1";
            Make("j", survey);
            sw.Restart();
            var r10 = Run(new() { Row("j", Removal("j", survey)) });
            Check($"브라우저를 띄우고 끝남(항목 남음) → 브라우저는 안 기다리고 실패 ({sw.Elapsed.TotalSeconds:0.0}초)", r10.Failed == 1 && sw.Elapsed.TotalSeconds < 10);
            // 11) [기다리지 않기]: 오래 열려 있는 제거 프로그램 → 2초 뒤 기다리지 않기 → 실패로 넘어감
            string slow = "cmd.exe /c ping -n 30 127.0.0.1 >nul";
            Make("k", slow);
            var skipTimer = new System.Threading.Timer(_ => un.GetMethod("SkipWait")!.Invoke(null, null), null, 2500, Timeout.Infinite);
            sw.Restart();
            var r11 = Run(new() { Row("k", Removal("k", slow)) });
            skipTimer.Dispose();
            Check($"[기다리지 않기] → 바로 다음으로, 실패 1 ({sw.Elapsed.TotalSeconds:0.0}초)", r11.Failed == 1 && sw.Elapsed.TotalSeconds < 6);
            // 12) 스토어판 경로: 셸(탐색기)로 실행해도 기다리고 판정 (프로세스 핸들 없이 같은 이름 프로세스로 찾음)
            un.GetProperty("ShellForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, true);
            string viaShell = $"\"{fakeExe}\" /c ping -n 4 127.0.0.1 >nul & {Del("l")}";
            Make("l", viaShell);
            sw.Restart();
            var r12 = Run(new() { Row("l", Removal("l", viaShell)) });
            un.GetProperty("ShellForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, false);
            Check($"셸로 실행(스토어판) → 끝까지 기다려 지움 ({sw.Elapsed.TotalSeconds:0.0}초)", r12.Removed == 1 && sw.Elapsed.TotalSeconds >= 2.5);
            try { Directory.Delete(fakeDir, true); } catch { /* 아직 쓰는 중이면 남음 */ }
            // 13) MsiExec /I{GUID} → /X{GUID} (실행하지 않고 바꾸기만)
            var msi = un.GetMethod("MsiRemoveArgs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            string M(string exe, string a) => (string)msi.Invoke(null, new object[] { exe, a })!;
            const string G = "{12345678-1234-1234-1234-123456789ABC}";
            Check($"MsiExec /I{{GUID}} → {M("MsiExec.exe", "/I" + G)}", M("MsiExec.exe", "/I" + G) == "/X" + G);
            Check($"MsiExec /qb /i {{GUID}} → {M("msiexec", "/qb /i " + G)}", M("msiexec", "/qb /i " + G) == "/qb /X" + G);
            Check("msiexec 아닌 프로그램의 /I 는 그대로", M("setup.exe", "/I" + G) == "/I" + G);
            // 14) 64·32비트 레지스트리 자리 (읽기만): HKLM 64 의 실제 항목 하나·32(WOW6432Node) 항목 하나가 각각 자기 자리에서 보이는지
            foreach (var (tag, view) in new[] { ("HKLM64", Microsoft.Win32.RegistryView.Registry64), ("HKLM32", Microsoft.Win32.RegistryView.Registry32) })
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view).OpenSubKey(root);
                string? any = baseKey?.GetSubKeyNames().FirstOrDefault();
                var exists = un.GetMethod("Exists", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                bool found = any is not null && (bool)exists.Invoke(null, new object?[] { tag + "|" + any })!;
                Check($"레지스트리 {tag} 항목을 그 자리에서 찾음", found);
            }
            // 7) UninstallString 나누기
            var split = un.GetMethod("Split", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            string S(string c) => split.Invoke(null, new object[] { c }) is { } t ? $"{t.GetType().GetField("Item1")!.GetValue(t)}|{t.GetType().GetField("Item2")!.GetValue(t)}" : "null";
            Check($"나누기: 따옴표 경로 → {S("\"C:\\Program Files\\X\\uninst.exe\" /S")}", S("\"C:\\Program Files\\X\\uninst.exe\" /S") == "C:\\Program Files\\X\\uninst.exe|/S");
            Check($"나누기: msiexec → {S("MsiExec.exe /X{ABC}")}", S("MsiExec.exe /X{ABC}") == "MsiExec.exe|/X{ABC}");
        }
        finally
        {
            foreach (var id in ids) Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root + "\\" + Key(id), false); // 시험 항목 모두 지움
            lines.Add($"시험 항목 남음: {ids.Count(Exists)}개");
        }
        if (outFile is not null) File.WriteAllLines(outFile, lines);
        return fail == 0 ? 0 : 1;
    }

    private static int SuggestTable()
    {
        var asm = typeof(Mongdock.App).Assembly;
        var t = asm.GetType("Mongdock.Services.RoutineSuggest")!;
        var any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var launchType = t.GetNestedType("Launch", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        var find = t.GetMethod("Find", any)!;
        var whenText = t.GetMethod("WhenText", any)!;
        var now = new DateTime(2026, 10, 16, 18, 0, 0); // 금요일 저녁
        var monday = new DateTime(2026, 10, 5); // 2주 전 월요일
        System.Collections.IList Make(IEnumerable<(string Id, DateTime At)> items)
        {
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(launchType))!;
            foreach (var (id, at) in items) list.Add(Activator.CreateInstance(launchType, id, at));
            return list;
        }
        IEnumerable<(string, DateTime)> Days(IEnumerable<int> dayOffsets, TimeSpan start, params string[] apps)
        {
            foreach (int d in dayOffsets)
                for (int i = 0; i < apps.Length; i++)
                    yield return (apps[i], monday.AddDays(d) + start + TimeSpan.FromMinutes(4 * i));
        }
        var weekdays = new[] { 7, 8, 9, 10, 11 }; // 지난주 월~이번 금 평일 5일 (판정은 최근 7일 = 10/9 18시 뒤)
        var none = new HashSet<string>();
        var empty = new List<IReadOnlyCollection<string>>();
        int fail = 0;
        void Case(string name, IEnumerable<(string, DateTime)> launches, bool expect, ISet<string>? startup = null,
            List<IReadOnlyCollection<string>>? routines = null, List<IReadOnlyCollection<string>>? declined = null)
        {
            var r = find.Invoke(null, new object?[] { Make(launches), now, startup ?? none, routines ?? empty, declined ?? empty });
            string got = r is null ? "안 물음" : "물음: " + string.Join("·", ((System.Collections.IEnumerable)r.GetType().GetProperty("Apps")!.GetValue(r)!).Cast<string>().Select(x => x[4..]))
                + $" ({((System.Collections.ICollection)r.GetType().GetProperty("Days")!.GetValue(r)!).Count}일, {whenText.Invoke(null, new[] { r })})";
            bool ok = (r is not null) == expect;
            if (!ok) fail++;
            Console.WriteLine($"  {(ok ? "통과" : "실패")}  {name,-34} → {got}");
        }
        var nine = TimeSpan.FromHours(9);
        Case("평일 아침 3앱 5일", Days(weekdays, nine, "exe:outlook", "exe:chrome", "exe:hwp"), true);
        Case("앱 2개뿐", Days(weekdays, nine, "exe:outlook", "exe:chrome"), false);
        Case("2일만 반복", Days(new[] { 8, 9 }, nine, "exe:outlook", "exe:chrome", "exe:hwp"), false);
        Case("3일 반복 (최소)", Days(new[] { 7, 8, 9 }, nine, "exe:outlook", "exe:chrome", "exe:hwp"), true);
        Case("시각이 날마다 2시간 이상 다름", Days(new[] { 7, 10 }, nine, "exe:outlook", "exe:chrome", "exe:hwp")
            .Concat(Days(new[] { 8, 11 }, TimeSpan.FromHours(11.5), "exe:outlook", "exe:chrome", "exe:hwp"))
            .Concat(Days(new[] { 9 }, TimeSpan.FromHours(14), "exe:outlook", "exe:chrome", "exe:hwp")), false);
        Case("시각 ±40분 흔들림", Days(new[] { 7, 8 }, nine, "exe:outlook", "exe:chrome", "exe:hwp")
            .Concat(Days(new[] { 9, 10 }, TimeSpan.FromHours(9.6), "exe:outlook", "exe:chrome", "exe:hwp")), true);
        Case("30분 넘게 흩어져 켬", weekdays.SelectMany(d => new[] { ("exe:outlook", monday.AddDays(d) + nine), ("exe:chrome", monday.AddDays(d) + nine + TimeSpan.FromMinutes(25)), ("exe:hwp", monday.AddDays(d) + nine + TimeSpan.FromMinutes(55)) }), false);
        Case("시작 프로그램(카톡) 끼면 2앱", Days(weekdays, nine, "exe:kakaotalk", "exe:outlook", "exe:chrome"), false, new HashSet<string> { "exe:kakaotalk" });
        Case("시작 프로그램 빼고도 3앱", Days(weekdays, nine, "exe:kakaotalk", "exe:outlook", "exe:chrome", "exe:hwp"), true, new HashSet<string> { "exe:kakaotalk" });
        Case("기존 루틴과 같은 묶음", Days(weekdays, nine, "exe:outlook", "exe:chrome", "exe:hwp"), false,
            routines: new List<IReadOnlyCollection<string>> { new[] { "exe:outlook", "exe:chrome", "exe:hwp" } });
        Case("거절한 묶음", Days(weekdays, nine, "exe:outlook", "exe:chrome", "exe:hwp"), false,
            declined: new List<IReadOnlyCollection<string>> { new[] { "exe:outlook", "exe:chrome", "exe:hwp" } });
        Case("거절한 묶음과 1개만 겹침", Days(weekdays, nine, "exe:code", "exe:slack", "exe:figma"), true,
            declined: new List<IReadOnlyCollection<string>> { new[] { "exe:outlook", "exe:chrome", "exe:code" } });
        Case("7일보다 오래된 기록 (보관은 14일)", Days(weekdays, nine, "exe:outlook", "exe:chrome", "exe:hwp").Select(x => (x.Item1, x.Item2.AddDays(-7))), false);
        Case("금·토·일 저녁 게임 묶음", Days(new[] { 4, 5, 6 }, TimeSpan.FromHours(20), "exe:steam", "exe:discord", "exe:chrome"), true);
        Case("주말 저녁 3일 (8일 전 하루는 빠짐)", Days(new[] { -1, 5, 6 }, TimeSpan.FromHours(20), "exe:steam", "exe:discord", "exe:chrome"), false);
        Case("매일 4앱 중 하루 1개 빠짐", Days(new[] { 7, 8, 10 }, nine, "exe:outlook", "exe:chrome", "exe:hwp", "exe:teams")
            .Concat(Days(new[] { 9 }, nine, "exe:outlook", "exe:chrome", "exe:hwp")), true);
        // 브라우저 페이지 힌트 (§15-1): 제목 정리
        var clean = t.GetMethod("CleanTitle", any)!;
        foreach (var (raw, want) in new[]
        {
            ("Gmail - 받은편지함 (3) - user@company.com - Gmail - Google Chrome", "Gmail - 받은편지함 (3) - user@company.com - Gmail"[..40] + "…"),
            ("(12) 받은편지함 - Gmail - Google Chrome", "받은편지함 - Gmail"),
            ("Notion – 팀 위키 - Microsoft​ Edge", "Notion – 팀 위키"),
            ("새 탭 - Google Chrome", null),
            ("YouTube — Mozilla Firefox", "YouTube"),
            (new string('가', 60) + " - Whale", new string('가', 40) + "…"),
        })
        {
            string? got = (string?)clean.Invoke(null, new object?[] { raw });
            bool ok = got == want;
            if (!ok) fail++;
            Console.WriteLine($"  {(ok ? "통과" : "실패")}  제목 정리 {raw[..Math.Min(30, raw.Length)],-30} → {got ?? "(없음)"}");
        }
        Console.WriteLine(fail == 0 ? "루틴 추천 판정: 모두 통과" : $"루틴 추천 판정: {fail}개 실패");
        Console.WriteLine("  (묻는 단계에서 거름: 설치 3일 안, 지난 추천 7일 안, 전체 화면·방해 금지, 일시 정지, 루틴 추천 끔·자주 쓰는 앱 기록 끔, 3번 연속 거절이면 끔)");
        // 즐겨찾기 추천 순서 (2026-10-13 사용자 확정): 1순위 몽독 기록 → 남은 칸만 2순위 윈도우 씨앗(90일·2회 이상), 섞어 더하지 않음, 빼기 먼저
        {
            var pick = asm.GetType("Mongdock.Services.AppUsage")!.GetMethod("Pick", any)!;
            var today = new DateTime(2026, 10, 13);
            string D(int daysAgo) => today.AddDays(-daysAgo).ToString("yyyy-MM-dd");
            (List<string> R, int W) Run(List<string> mongdock, List<(string, int, string)> seeds, Func<string, bool>? keep = null, Func<string, bool>? seedKeep = null)
            {
                var r = pick.Invoke(null, new object?[] { mongdock, seeds, 4, keep ?? (_ => true), today, seedKeep })!;
                return ((List<string>)r.GetType().GetField("Item1")!.GetValue(r)!, (int)r.GetType().GetField("Item2")!.GetValue(r)!);
            }
            void CaseFav(string name, (List<string> R, int W) got, string[] expect, int expectWin)
            {
                bool ok = got.R.SequenceEqual(expect) && got.W == expectWin;
                if (!ok) fail++;
                Console.WriteLine($"  {(ok ? "통과" : "실패")}  즐겨찾기 추천 {name,-22} → {string.Join(",", got.R)} (씨앗 {got.W}칸)");
            }
            var seeds = new List<(string, int, string)> { ("w1", 9, D(3)), ("w2", 5, D(1)), ("w3", 5, D(10)), ("w4", 2, D(80)), ("w5", 1, D(0)), ("w6", 7, D(120)) };
            CaseFav("첫날: 씨앗만 4칸", Run(new(), seeds), new[] { "w1", "w2", "w3", "w4" }, 4); // 1회(w5)·90일 넘은(w6) 빼고, 같은 횟수는 최근 순
            CaseFav("몽독 2 + 씨앗 2", Run(new() { "m1", "w1", "m2" }, seeds, id => id != "w1"), new[] { "m1", "m2", "w2", "w3" }, 2); // w1 은 독 고정 → 씨앗으로도 안 들어옴
            CaseFav("씨앗의 윈도우 기본 앱 뺌", Run(new() { "sys" }, seeds, null, id => id != "w1" && id != "w2"), new[] { "sys", "w3", "w4" }, 2); // 1순위의 기본 앱(sys)은 그대로, 씨앗에서만 w1·w2(기본 앱) 뺌, 억지로 안 채움
            CaseFav("몽독 기록만 4칸", Run(new() { "m1", "m2", "m3", "m4", "m5" }, seeds), new[] { "m1", "m2", "m3", "m4" }, 0);
        }
        // 추천 칸 수: 즐겨찾기 0개 4칸 · 1개 3칸 · 2개 이상 2칸
        {
            var slots = asm.GetType("Mongdock.Views.AllAppsPanel")!.GetMethod("SuggestionSlots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            foreach (var (favs, expect) in new[] { (0, 4), (1, 3), (2, 2), (3, 2), (8, 2) })
            {
                int got = (int)slots.Invoke(null, new object[] { favs })!;
                bool ok = got == expect;
                if (!ok) fail++;
                Console.WriteLine($"  {(ok ? "통과" : "실패")}  {$"추천 칸 수: 즐겨찾기 {favs}개",-34} → {got}칸 (기대 {expect})");
            }
        }
        // 루틴 링크 해석·허용 (리뷰 H1·H2·M2)
        var links = asm.GetType("Mongdock.Services.RoutineLinks")!;
        var parse = links.GetMethod("Parse")!;
        var allowed = links.GetMethod("Allowed")!;
        (string Link, string? Expect, bool? Allow)[] cases =
        {
            ("localhost:3000", "https://localhost:3000/", true),
            ("intranet:8080/x", "https://intranet:8080/x", true),
            ("mail.company.com:8443/x", "https://mail.company.com:8443/x", true),
            ("naver.com", "https://naver.com/", true),
            ("ms-msdt:/id x", null, false),
            ("ms-visio:ofe|u|http://x/a.vsd", null, false),
            ("search-ms:query=x", null, false),
            ("C:\\x.exe", null, false),
            ("discord:x\" --gpu-launcher=\"calc\"", null, false),
            ("javascript:alert(1)", null, false),
        };
        foreach (var (link, expect, allow) in cases)
        {
            var u = (Uri?)parse.Invoke(null, new object[] { link });
            bool ok = u is null ? allow != true : (expect is null || u.AbsoluteUri == expect) && (allow is null || (bool)allowed.Invoke(null, new object[] { u })! == allow);
            if (!ok) fail++;
            Console.WriteLine($"  {(ok ? "통과" : "실패")}  링크 {link} → {u?.AbsoluteUri ?? "(없음)"}");
        }
        return fail == 0 ? 0 : 1;
    }

    private static void ClipDemo()
    {
        foreach (var (longer, fixedPanels) in new[] { (false, false), (true, false), (true, true) })
        {
            var root = new System.Windows.Controls.Grid { Width = 100, Height = 200, Background = Brushes.White };
            System.Windows.Controls.Border panel = fixedPanels ? new Mongdock.Views.NoClipBorder() : new System.Windows.Controls.Border();
            panel.HorizontalAlignment = HorizontalAlignment.Right; panel.VerticalAlignment = VerticalAlignment.Center; panel.Width = 50; panel.Background = Brushes.LightGray;
            var unused = new System.Windows.Controls.Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Width = 50, Background = Brushes.LightGray };
            System.Windows.Controls.StackPanel stack = fixedPanels ? new Mongdock.Views.NoClipStackPanel() : new System.Windows.Controls.StackPanel();
            var item = new System.Windows.Controls.Grid { Width = 50, Height = longer ? 260 : 150 };
            var icon = new System.Windows.Shapes.Rectangle { Width = 40, Height = 40, Fill = Brushes.Red, HorizontalAlignment = HorizontalAlignment.Right, RenderTransformOrigin = new Point(1, 0.5), RenderTransform = new ScaleTransform(2, 2) };
            item.Children.Add(icon);
            stack.Children.Add(item);
            panel.Child = stack;
            root.Children.Add(panel);
            root.Measure(new Size(100, 200));
            root.Arrange(new Rect(0, 0, 100, 200));
            root.UpdateLayout();
            var rtb = new RenderTargetBitmap(100, 200, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var px = new byte[4];
            rtb.CopyPixels(new Int32Rect(20, 100, 1, 1), px, 4, 0); // 판(50~100) 왼쪽, 확대 아이콘이 덮어야 할 자리
            bool red = px[2] > 200 && px[1] < 80;
            Console.WriteLine($"  {(fixedPanels ? "고친 판" : "예전 판")}, 판이 원하는 길이가 창보다 {(longer ? "김" : "짧음")}: 판 밖 확대 아이콘 {(red ? "보임" : "잘림")}");
        }
    }

    private static void DockFitReport()
    {
        var asm = typeof(Mongdock.App).Assembly;
        var fit = asm.GetType("Mongdock.Views.DockFit")!;
        var any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mongdock", "settings.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var d = doc.RootElement.GetProperty("dock");
        double icon = d.GetProperty("iconSize").GetDouble(), spacing = d.GetProperty("iconSpacing").GetDouble(), scale = d.GetProperty("hoverScale").GetDouble();
        bool wave = d.GetProperty("waveMagnification").GetBoolean();
        double offset = d.TryGetProperty("offset", out var off) ? off.GetDouble() : 0.5;
        string edge = d.GetProperty("edge").GetString() ?? "Bottom";
        var top = doc.RootElement.GetProperty("topBar");
        double barH = top.GetProperty("enabled").GetBoolean() ? top.GetProperty("height").GetDouble() : 0;
        var wa = SystemParameters.WorkArea;
        double start = edge is "Left" or "Right" ? Math.Max(wa.Top, barH) : wa.Left, end = edge is "Left" or "Right" ? wa.Bottom : wa.Right;
        double avail = end - start;
        fit.GetField("HoverScale", any)!.SetValue(null, scale);
        fit.GetField("Wave", any)!.SetValue(null, wave);
        Console.WriteLine($"독 {edge}, 아이콘 {icon}, 간격 {spacing}, 확대 {scale}{(wave ? " 파도" : "")}, 쓸 수 있는 길이 {avail:0} DIP ({start:0}~{end:0})");
        double Len(double ic, int n) => (double)fit.GetMethod("PanelLength", any)!.Invoke(null, new object[] { ic, spacing, n, 2, 0.0 })!;
        double Rest(double ic, int n) => (double)fit.GetMethod("RestLength", any)!.Invoke(null, new object[] { ic, spacing, n, 2, 0.0 })!;
        int pins = doc.RootElement.GetProperty("pins").GetArrayLength();
        foreach (int n in new[] { pins, pins + 4, pins + 8, pins + 12, pins + 16, pins + 19, pins + 24 })
        {
            var r = ((double Icon, double Spacing))fit.GetMethod("Fit", any)!.Invoke(null, new object[] { icon, spacing, n, 2, avail, 0.0 })!;
            int cap = (int)fit.GetMethod("Capacity", any)!.Invoke(null, new object[] { r.Icon, r.Spacing, n, 2, avail, 0.0 })!;
            int shown = Math.Min(n, cap);
            double len = Len(r.Icon, shown);
            Console.WriteLine($"  아이콘 {n,2}개 → 크기 {r.Icon:0}, {(cap == int.MaxValue ? "넘침 없음" : $"⋯ 칸 (보일 칸 {cap}, 감춤 {n - cap + 1})")}, 쉬는 길이 {Rest(r.Icon, shown):0} + 확대 {len - Rest(r.Icon, shown):0} = {len:0} / {avail - 16:0} {(len <= avail - 16 ? "들어감" : "넘침!")}");
        }
        // 사용자 실제 독 개수(--dockfit N, 기본 고정 앱 + 19)로 커서 위 끝·가운데·아래 끝 3장 (판은 창 가운데라 양쪽으로 반씩 늘어남)
        int argN = Array.IndexOf(Environment.GetCommandLineArgs(), "--dockfit");
        var cl = Environment.GetCommandLineArgs();
        int total = argN >= 0 && argN + 1 < cl.Length && int.TryParse(cl[argN + 1], out int tn) ? tn : pins + 19;
        var rr = ((double Icon, double Spacing))fit.GetMethod("Fit", any)!.Invoke(null, new object[] { icon, spacing, total, 2, avail, 0.0 })!;
        int capacity = (int)fit.GetMethod("Capacity", any)!.Invoke(null, new object[] { rr.Icon, rr.Spacing, total, 2, avail, 0.0 })!;
        int visible = Math.Min(total, capacity);
        double ic2 = rr.Icon, slot = ic2 + spacing, rad = ic2 * 2.5, pad = Math.Round(Math.Max(5, ic2 * 0.12));
        double rest = Rest(ic2, visible), growth = Len(ic2, visible) - rest;
        double screenH = SystemParameters.PrimaryScreenHeight;
        double clampLen = rest + growth;
        double center = Math.Clamp(offset * screenH, start + 8 + clampLen / 2, end - 8 - clampLen / 2);
        string outDir = Path.Combine(Path.GetTempPath(), "mongdock-dockfit");
        if (Array.IndexOf(cl, "--out") is int oi && oi >= 0 && oi + 1 < cl.Length) outDir = cl[oi + 1];
        Directory.CreateDirectory(outDir);
        foreach (var (name, frac) in new[] { ("top", 0.0), ("middle", 0.5), ("bottom", 1.0) })
        {
            double cur = frac * visible * slot;
            var scales = Enumerable.Range(0, visible).Select(i =>
            {
                double dd = Math.Abs(cur - (i * slot + slot / 2));
                return wave ? 1 + (scale - 1) * Math.Max(0, Math.Cos(Math.PI * Math.Min(dd, rad) / (2 * rad))) : (dd <= slot / 2 ? scale : 1);
            }).ToList();
            double grownLen = rest + scales.Sum(sc => (sc - 1) * ic2);
            double panelTop = center - grownLen / 2;
            const double W = 160;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xF2)), null, new Rect(0, 0, W, screenH));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x38)), null, new Rect(0, 0, W, start)); // 상단바
                var lineP = new Pen(Brushes.Red, 1) { DashStyle = DashStyles.Dash };
                dc.DrawLine(lineP, new Point(0, start), new Point(W, start));
                dc.DrawLine(lineP, new Point(0, end - 0.5), new Point(W, end - 0.5));
                double panelW = ic2 + pad * 2 + 2, right = W - 10;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD9, 0xD8, 0xD6, 0xE0)), new Pen(Brushes.Gray, 0.5), new Rect(right - panelW, panelTop, panelW, grownLen), 14, 14);
                double y = panelTop + pad + 1;
                for (int i = 0; i < visible; i++)
                {
                    double h = ic2 * scales[i];
                    double yy = y + spacing / 2;
                    var fill = new SolidColorBrush(i == visible - 1 && capacity != int.MaxValue ? Color.FromRgb(0xB6, 0xAE, 0xCC) : Color.FromRgb((byte)(0x5B + i * 7 % 120), 0x8F, 0xEE));
                    dc.DrawRoundedRectangle(fill, null, new Rect(right - pad - 1 - h, yy, h, h), h * 0.22, h * 0.22);
                    y += h + spacing;
                }
                var ft = new FormattedText($"{name}: {visible}칸{(capacity != int.MaxValue ? " (⋯ 포함)" : "")} {ic2:0}px", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(4, 6));
                bool inside = panelTop >= start - 0.5 && panelTop + grownLen <= end + 0.5;
                var res = new FormattedText(inside ? "화면 안" : "잘림!", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, inside ? Brushes.SeaGreen : Brushes.Red, 1.0);
                dc.DrawText(res, new Point(4, screenH - 22));
                Console.WriteLine($"  커서 {name}: 독 {panelTop:0}~{panelTop + grownLen:0} (쓸 수 있는 {start:0}~{end:0}) {(inside ? "화면 안" : "잘림!")}");
            }
            var bmp = new RenderTargetBitmap((int)W, (int)screenH, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            string file = Path.Combine(outDir, $"dockfit-{name}.png");
            using (var fs = File.Create(file)) enc.Save(fs);
        }
        Console.WriteLine($"  그림: {outDir}");
    }

    /// <summary>
    /// 앱 모음 판 그림 (--allapps DIR): 이 PC 의 실제 앱 목록으로 처음 화면 · 묶음 하나 펼침 · 검색 결과 3장 (라이트·다크).
    /// 설정은 기본값(사본) — 사용자 settings.json 은 쓰지 않음. 디스패처를 돌리지 않음(돌리면 App.OnStartup 이 실행됨).
    /// </summary>
    /// <summary>
    /// 둘러보기 그림 (--tour DIR): 몽독이 그 자리에서 그리는 장면(TourScenes — 앱과 같은 코드)으로 카드 미리보기 + 장, 지금 언어(--lang)·라이트·다크.
    /// 첫 설치 흐름(안내 + 6단계)과 0.4.x → 0.5.0 업데이트 흐름(새 기능 4장 + 그 밖에 바뀐 것)을 시트 한 장씩.
    /// --tourcheck: 모든 장을 그려 가리킬 요소·빈 그림을 점검, 하나라도 문제면 실패(종료 코드 1).
    /// </summary>
    /// <summary>
    /// 둘러보기 카드 글이 잘리지 않는지 (첫 설치 흐름·0.4.x → 0.5.0 업데이트 흐름): 앱처럼 모든 단계로 높이를 정한 뒤
    /// 단계마다 채워서 본문이 받은 높이보다 크면(아래 버튼 줄에 가림) 문제, 한 줄 글이 말줄임되면 문제, 단계 중간에 카드가 늘어나면 문제.
    /// </summary>
    private static int CardFit(AppServices services, Mongdock.ViewModels.UiPalette palette, string theme)
    {
        var asm = typeof(Mongdock.App).Assembly;
        var any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        var coach = asm.GetType("Mongdock.Views.CoachMarks")!;
        var scenes = asm.GetType("Mongdock.Views.TourScenes")!;
        var cardType = asm.GetType("Mongdock.Views.TourCard")!;
        var stepType = asm.GetType("Mongdock.Views.TourStep")!;
        coach.GetMethod("Init", any)!.Invoke(null, new object?[] { services, (Func<Mongdock.Services.CoachAnchor, (Rect, MonitorInfo)?>)(_ => null), null });
        var problems = new List<string>();
        foreach (var flow in new[] { "tour", "update", "update06", "update061", "update062", "update08", "catchup" }) // update = 0.4.2→0.5.0, update06 = 0.5.0→0.6.0 (장면 카드여야 함), catchup = 0.2.0→지금 (오래된 버전 — 카드가 화면에 들어가야 함)
        {
            var pages = (flow == "tour"
                ? (System.Collections.IEnumerable)coach.GetMethod("BuildTourPages", any)!.Invoke(null, null)!
                : (System.Collections.IEnumerable)coach.GetMethod("BuildWhatsNew", any)!.Invoke(null, (flow == "catchup" ? new object[] { new Version(0, 2, 0), Mongdock.Services.WhatsNew.Current } : flow == "update08" ? new object[] { new Version(0, 6, 5), new Version(0, 8, 0) } : flow == "update062" ? new object[] { new Version(0, 6, 1), new Version(0, 6, 2) } : flow == "update061" ? new object[] { new Version(0, 6, 0), new Version(0, 6, 1) } : flow == "update06" ? new object[] { new Version(0, 5, 0), new Version(0, 6, 0) } : new object[] { new Version(0, 4, 2), new Version(0, 5, 0) }))!).Cast<object>().ToList();
            if (flow is "update06" or "update061" or "update08" && !pages.Any(x => x.GetType().GetProperty("Pictures")!.GetValue(x) is string))
                problems.Add($"{flow} 새 기능 안내에 장면 카드가 없음 (예전 말풍선으로 뜸)");
            var steps = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(stepType))!;
            var titles = new List<string>();
            foreach (var pg in pages)
            {
                T? P<T>(string n) => (T?)pg.GetType().GetProperty(n)!.GetValue(pg);
                string? key = P<string>("Pictures");
                var step = Activator.CreateInstance(stepType, P<string>("Title"), P<string>("Body"), new List<ImageSource>())!;
                void Set(string n, object? v) => stepType.GetProperty(n)!.SetValue(step, v);
                if (P<object>("Groups") is { } groups) Set("Groups", groups);
                Set("More", P<int>("More"));
                if (P<bool>("ChangelogLink")) Set("Link", P<string>("LinkText"));
                if (P<object>("Action") is { } act) Set("Action", act);
                Set("Footnote", P<string>("Footnote"));
                Set("NextText", P<string>("NextText"));
                Set("ShowLogo", P<bool>("ShowLogo"));
                if (P<object>("Inline") is { } inl) Set("InlineText", (string)inl.GetType().GetProperty("Label")!.GetValue(inl)!);
                Set("CanTry", P<object>("Anchor")?.ToString() != "Center");
                Set("FramesPending", key is not null && (int)scenes.GetMethod("Count", any)!.Invoke(null, new object?[] { key })! > 0);
                steps.Add(step);
                titles.Add(P<string>("Title") ?? "");
                // 버튼을 누른 뒤 모습 (가장 긴 결과 한 줄 + [되돌리기])도 잘리지 않아야 함
                if (P<object>("Inline") is { } inl2)
                {
                    var after = Activator.CreateInstance(stepType, P<string>("Title"), P<string>("Body"), new List<ImageSource>())!;
                    foreach (var prop in stepType.GetProperties().Where(x => x.CanWrite && x.Name is not ("Title" or "Body" or "Frames"))) prop.SetValue(after, prop.GetValue(step));
                    stepType.GetProperty("InlineText")!.SetValue(after, null);
                    stepType.GetProperty("ResultText")!.SetValue(after, (string)inl2.GetType().GetProperty("LongestResult")!.GetValue(inl2)!);
                    stepType.GetProperty("CanUndo")!.SetValue(after, true);
                    steps.Add(after);
                    titles.Add((P<string>("Title") ?? "") + " (누른 뒤)");
                }
            }
            var card = (FrameworkElement)Activator.CreateInstance(cardType, any, null, new object[] { palette }, null)!;
            var host = new System.Windows.Controls.Border { Child = card }; // 앱처럼 부모 아래 (상속 글꼴)
            // 화면 높이 한도: 1920×1080 125% · 위 상단바(작업 영역 838 DIP, 오류 신고 uid 29 때 PC) - 위아래 8
            const double maxCard = 838 - 16;
            cardType.GetMethod("FixHeight", any)!.Invoke(card, new object[] { steps, maxCard });
            double fixedHeight = card.Height;
            if (fixedHeight > maxCard + 0.5)
            {
                var nat = cardType.GetMethod("NaturalHeight", any)!;
                var tall = new List<string>();
                for (int i = 0; i < steps.Count; i++)
                {
                    cardType.GetMethod("Show", any)!.Invoke(card, new object[] { steps[i]!, i, steps.Count });
                    tall.Add($"{i + 1}:{(double)nat.Invoke(card, null)!:0}");
                }
                problems.Add($"{flow}: 카드가 화면보다 큼 ({fixedHeight:0} > {maxCard:0}) — 장별 {string.Join(" ", tall)}");
            }
            for (int i = 0; i < steps.Count; i++)
            {
                cardType.GetMethod("Show", any)!.Invoke(card, new object[] { steps[i]!, i, steps.Count });
                var fit = cardType.GetMethod("FitForTest", any)!.Invoke(card, null)!;
                double overflow = (double)fit.GetType().GetField("Item1")!.GetValue(fit)!;
                var trimmed = (List<string>)fit.GetType().GetField("Item2")!.GetValue(fit)!;
                string where = $"{(flow == "tour" ? "첫 설치" : "업데이트")} {i + 1}/{steps.Count} \"{titles[i]}\"";
                if (overflow > 0.5) problems.Add($"{where}: 글이 {overflow:0}px 잘림");
                if (trimmed.Count > 0) problems.Add($"{where}: 말줄임 {string.Join(" · ", trimmed)}");
                if (card.Height > fixedHeight + 0.5) problems.Add($"{where}: 카드 높이가 늘어남 ({fixedHeight:0} → {card.Height:0}) — 높이 재기에서 빠짐");
            }
            Console.WriteLine($"  카드 글 잘림 점검 ({_lang}, {theme}, {(flow == "tour" ? "첫 설치" : "업데이트")} {steps.Count}장, 높이 {fixedHeight:0})");
            host.Child = null;
        }
        // 업데이트 "독 폴더·휴지통" 카드 버튼: 첫 설치 흐름엔 없고 업데이트 흐름엔 있음
        {
            object? Inline(object pg) => pg.GetType().GetProperty("Inline")!.GetValue(pg);
            var tourPages = ((System.Collections.IEnumerable)coach.GetMethod("BuildTourPages", any)!.Invoke(null, null)!).Cast<object>().ToList();
            var updPages = ((System.Collections.IEnumerable)coach.GetMethod("BuildWhatsNew", any)!.Invoke(null, new object[] { new Version(0, 4, 2), new Version(0, 5, 0) })!).Cast<object>().ToList();
            if (tourPages.Any(x => Inline(x) is not null)) problems.Add("첫 설치 둘러보기에 [다운로드·휴지통 넣기] 버튼이 있음");
            if (!updPages.Any(x => Inline(x) is not null && (string?)x.GetType().GetProperty("Pictures")!.GetValue(x) == "stacks")) problems.Add("업데이트 '독 폴더·휴지통' 카드에 [다운로드·휴지통 넣기] 버튼이 없음");
        }
        if (theme == "light") problems.AddRange(StackPinRules());
        Console.WriteLine($"  카드 글 잘림 점검 ({_lang}, {theme}): {(problems.Count == 0 ? "통과" : $"문제 {problems.Count}개")}");
        foreach (var x in problems) Console.WriteLine($"    - {x}");
        return problems.Count;
    }

    /// <summary>[다운로드·휴지통 넣기] 규칙 (시험 핀 목록으로만): 없는 것만, 다운로드는 휴지통 바로 앞, 휴지통은 맨 끝, 둘 다 있으면 그대로.</summary>
    private static List<string> StackPinRules()
    {
        var problems = new List<string>();
        string? dl = (string?)typeof(DefaultPins).Assembly.GetType("Mongdock.Services.DockFolderService")!.GetMethod("DownloadsFolder", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null);
        PinItem App(string n) => new() { Kind = PinKind.Exe, Target = $@"C:	est\{n}.exe", Name = n };
        string Show(List<PinItem> l) => string.Join(" | ", l.Select(p => p.Kind == PinKind.Special && p.Target == DefaultPins.RecycleBinTarget ? "휴지통" : p.Kind == PinKind.Folder ? "폴더:" + Path.GetFileName(p.Target) : p.Name));
        bool IsBin(PinItem p) => p.Kind == PinKind.Special && p.Target == DefaultPins.RecycleBinTarget;
        bool IsDl(PinItem p) => dl is not null && p.Kind == PinKind.Folder && string.Equals(p.Target, dl, StringComparison.OrdinalIgnoreCase);

        // ① 둘 다 없음 → 다운로드, 휴지통 순으로 끝에
        var a = new List<PinItem> { App("a"), App("b") };
        var ra = DefaultPins.AddDockStacks(a);
        bool okA = IsBin(a[^1]) && (dl is null ? a.Count == 3 : IsDl(a[^2]) && a.Count == 4) && ra.RecycleBin && ra.Downloads == (dl is not null);
        if (!okA) problems.Add($"넣기 규칙 ① 둘 다 없음: {Show(a)}");
        // ② 휴지통만 있음(가운데에 있어도) → 다운로드를 휴지통 바로 앞에, 휴지통은 그대로
        var b = new List<PinItem> { App("a"), DefaultPins.RecycleBinPin(), App("b") };
        var rb = DefaultPins.AddDockStacks(b);
        int bin = b.FindIndex(IsBin);
        bool okB = !rb.RecycleBin && b.Count(IsBin) == 1 && (dl is null ? b.Count == 3 : bin > 0 && IsDl(b[bin - 1]));
        if (!okB) problems.Add($"넣기 규칙 ② 휴지통만 있음: {Show(b)}");
        // ③ 다른 폴더만 있음 → 다운로드도 넣음 (새 설치와 달리 업데이트 버튼은 "다운로드가 없으면")
        var c = new List<PinItem> { App("a"), new() { Kind = PinKind.Folder, Target = @"C:	est\docs", Name = "docs" } };
        DefaultPins.AddDockStacks(c);
        if (dl is not null && !c.Any(IsDl)) problems.Add($"넣기 규칙 ③ 다른 폴더만 있음: {Show(c)}");
        // ④ 둘 다 있음 → 그대로, 버튼 안 보임
        var d = new List<PinItem> { App("a") };
        DefaultPins.AddDockStacks(d);
        var before = Show(d);
        var rd = DefaultPins.AddDockStacks(d);
        if (rd.Downloads || rd.RecycleBin || Show(d) != before || DefaultPins.DockStacksMissing(d)) problems.Add($"넣기 규칙 ④ 둘 다 있음인데 바뀜: {Show(d)}");
        if (!DefaultPins.DockStacksMissing(new List<PinItem> { App("a") })) problems.Add("넣기 규칙: 둘 다 없는데 버튼이 안 보임");
        Console.WriteLine($"  다운로드·휴지통 넣기 규칙 (다운로드 폴더 {(dl is null ? "없음" : "있음")}): {(problems.Count == 0 ? "통과" : $"문제 {problems.Count}개")} — ① {Show(a)} / ② {Show(b)}");
        return problems;
    }

    /// <summary>홈페이지 스크린샷: TourScenes.Shot 로 그려 {lang}-{이름}.png (라이트).</summary>
    private static int RenderShots(string dir, string exampleDir, bool v2 = false)
    {
        string Pre = _lang.ToLowerInvariant(); // 파일 이름: ko, en, ja, zh-hans, zh-hant, de, fr, es
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(exampleDir);
        // 앱 리소스(몽독 로고 등 pack://application 경로)를 report-test 가 아니라 몽독 어셈블리에서 찾게
        try { Application.ResourceAssembly = typeof(Mongdock.App).Assembly; } catch (InvalidOperationException) { }
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var asm = typeof(Mongdock.App).Assembly;
        var settings = new SettingsService(); // 시험 데이터 폴더 (MONGDOCK_DATA_DIR)
        settings.Current.Dock.Theme = DockTheme.Light;
        Mongdock.ViewModels.UiFonts.Apply(settings.Current);
        Mongdock.ViewModels.UiTheme.Apply(settings.Current);
        var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
        var tracker = new WindowTracker();
        var launcher = new AppLauncher(tracker);
        var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
            new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
            new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
        asm.GetType("Mongdock.Services.RoutineService")!.GetMethod("Init", Any)!.Invoke(null, new object[] { services });
        var scenes = asm.GetType("Mongdock.Views.TourScenes")!;
        var names = v2 ? Array.Empty<string>() : (string[])scenes.GetField("ShotNames", Any)!.GetValue(null)!;
        int problems = 0;
        if (v2)
        {
            void Save2(BitmapSource img, string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using var fs = File.Create(path);
                enc.Save(fs);
            }
            var dark = Mongdock.ViewModels.UiPalette.Dark;
            foreach (var set in (Array)scenes.GetField("AnimSets2", Any)!.GetValue(null)!)
            {
                string key = (string)set.GetType().GetField("Item1")!.GetValue(set)!;
                int count = (int)set.GetType().GetField("Item2")!.GetValue(set)!;
                var list = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    var img = (BitmapSource?)scenes.GetMethod("AnimFrame2", Any)!.Invoke(null, new object[] { services, palette, dark, key, i, list });
                    if (img is null) { list.Add($"{i + 1}장 없음"); continue; }
                    Save2(img, Path.Combine(dir, "anim", key, $"{Pre}-{i + 1:00}.png"));
                }
                Console.WriteLine($"  anim/{key} {count}장 {(list.Count == 0 ? "통과" : "문제: " + string.Join(" / ", list))}");
                problems += list.Count;
            }
            var hl = new List<string>();
            var hero2 = (BitmapSource?)scenes.GetMethod("Hero2", Any)!.Invoke(null, new object[] { services, palette, hl });
            if (hero2 is not null) Save2(hero2, Path.Combine(dir, $"hero2-{Pre}.png"));
            Console.WriteLine($"  hero2-{Pre}.png {(hero2 is null ? "없음" : $"{hero2.PixelWidth}×{hero2.PixelHeight}")} {(hl.Count == 0 ? "통과" : "문제: " + string.Join(" / ", hl))}");
            settings.Dispose();
            GC.KeepAlive(app);
            return problems + hl.Count == 0 && hero2 is not null ? 0 : 1;
        }
        foreach (var name in names)
        {
            var list = new List<string>();
            var img = (BitmapSource?)scenes.GetMethod("Shot", Any)!.Invoke(null, new object[] { services, palette, name, exampleDir, list });
            if (img is null) list.Add("그림 없음");
            else
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using var fs = File.Create(Path.Combine(dir, $"{Pre}-{name}.png"));
                enc.Save(fs);
            }
            Console.WriteLine($"  {Pre}-{name}.png {(img is null ? "" : $"{img.PixelWidth}×{img.PixelHeight}")} {(list.Count == 0 ? "통과" : "문제: " + string.Join(" / ", list))}");
            problems += list.Count;
        }
        void SavePng(BitmapSource img, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        // 움직이는 장면: anim/{기능}/{lang}-NN.png
        var sets = (Array)scenes.GetField("AnimSets", Any)!.GetValue(null)!;
        foreach (var set in sets)
        {
            string key = (string)set.GetType().GetField("Item1")!.GetValue(set)!;
            int count = (int)set.GetType().GetField("Item2")!.GetValue(set)!;
            var list = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var img = (BitmapSource?)scenes.GetMethod("AnimFrame", Any)!.Invoke(null, new object[] { services, palette, key, i, exampleDir, list });
                if (img is null) { list.Add($"{i + 1}장 없음"); continue; }
                SavePng(img, Path.Combine(dir, "anim", key, $"{Pre}-{i + 1:00}.png"));
            }
            Console.WriteLine($"  anim/{key} {count}장 {(list.Count == 0 ? "통과" : "문제: " + string.Join(" / ", list))}");
            problems += list.Count;
        }
        {
            var list = new List<string>();
            var hero = (BitmapSource?)scenes.GetMethod("Hero", Any)!.Invoke(null, new object[] { services, palette, exampleDir, list });
            if (hero is not null) SavePng(hero, Path.Combine(dir, $"hero-{Pre}.png"));
            Console.WriteLine($"  hero-{Pre}.png {(hero is null ? "없음" : $"{hero.PixelWidth}×{hero.PixelHeight}")} {(list.Count == 0 ? "통과" : "문제: " + string.Join(" / ", list))}");
            problems += list.Count + (hero is null ? 1 : 0);
        }
        settings.Dispose();
        GC.KeepAlive(app);
        return problems == 0 ? 0 : 1;
    }

    /// <summary>0.5.0 검토 수정 점검: 제거 항목 폴더 연결(넓은 폴더 빼기), 이름 비교, 따옴표 없는 제거 명령, 스팀 링크, 빠른 경로 확인.</summary>
    /// <summary>
    /// --tourmem: 둘러보기 장면을 앱처럼 그려 캐시에 담은 뒤(첫 설치 둘러보기 + 0.6.0 업데이트, 라이트·다크) 전용 메모리·작업 집합,
    /// 그다음 TourScenes.ReleaseCache(둘러보기가 끝날 때 앱이 부르는 것) 뒤 값. 각 값은 GC 3번 뒤.
    /// </summary>
    private static int TourMemory()
    {
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = new SettingsService();
        Mongdock.ViewModels.UiFonts.Apply(settings.Current);
        var tracker = new WindowTracker();
        var launcher = new AppLauncher(tracker);
        var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
            new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
            new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
        var scenes = typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.TourScenes")!;
        int Count(string key) => (int)scenes.GetMethod("Count", Any)!.Invoke(null, new object?[] { key })!;
        string Mem()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            proc.Refresh();
            return $"전용 {proc.PrivateMemorySize64 / 1048576}MB, 작업 집합 {proc.WorkingSet64 / 1048576}MB, GC 힙 {GC.GetTotalMemory(false) / 1048576}MB";
        }
        // 앱처럼 처음 한 장을 한 번 그려 WPF 준비(글꼴·자원)를 끝낸 뒤를 기준으로
        settings.Current.Dock.Theme = DockTheme.Light;
        Mongdock.ViewModels.UiTheme.Apply(settings.Current);
        scenes.GetMethod("Frame", Any)!.Invoke(null, new object[] { services, "settings", 0, Mongdock.ViewModels.UiTheme.Palette(settings.Current) });
        scenes.GetMethod("ReleaseCache", Any)!.Invoke(null, null);
        string baseline = Mem();
        Console.WriteLine($"  기준 (장면 0장): {baseline}");
        var keys = new[] { "dock", "topbar", "spotlight", "allapps", "routines", "stacks", "settings", "hotkeys" };
        foreach (var theme in new[] { DockTheme.Light, DockTheme.Dark })
        {
            settings.Current.Dock.Theme = theme;
            Mongdock.ViewModels.UiTheme.Apply(settings.Current);
            var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
            foreach (var key in keys)
                for (int i = 0; i < Count(key); i++)
                    scenes.GetMethod("Frame", Any)!.Invoke(null, new object[] { services, key, i, palette });
            int cached = (int)scenes.GetProperty("CachedCount", Any)!.GetValue(null)!;
            Console.WriteLine($"  {theme} 까지 그린 뒤 (캐시 {cached}장): {Mem()}");
        }
        int released = (int)scenes.GetMethod("ReleaseCache", Any)!.Invoke(null, null)!;
        Console.WriteLine($"  정리 뒤 ({released}장 놓음, 캐시 {(int)scenes.GetProperty("CachedCount", Any)!.GetValue(null)!}장): {Mem()}");
        GC.KeepAlive(app);
        return 0;
    }

    /// <summary>시험 그림용 예시 계정 목록 (리플렉션으로 Func&lt;string, IReadOnlyList&lt;Profile&gt;&gt; 를 만들려고).</summary>
    private sealed class ExampleProfiles
    {
        private readonly System.Collections.IList _list;
        public ExampleProfiles(System.Collections.IList list) => _list = list;
        public IReadOnlyList<TP> Get<TP>(string exe) => _list.Cast<TP>().ToList();
    }

    /// <summary>
    /// --appbarcheck: DesktopWindowService.ShouldSendSetPos (자리 알림을 보낼지) + 가짜 탐색기 흉내로 상단바 3개 고리:
    /// 탐색기는 SETPOS 를 받을 때마다 다른 AppBar 들에 자리 바뀜 알림을 보내고 작업 표시줄을 다시 띄움. 예전 규칙(늘 보냄)이면 끝없이 돌고,
    /// 새 규칙이면 처음 한 바퀴 뒤 멈추는지. 실제 AppBar 를 등록하지 않음(화면 예약 없음).
    /// </summary>
    private static int AppBarCheck()
    {
        var asm = typeof(Mongdock.App).Assembly;
        int bad = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  {(ok ? "통과" : "실패")}  {what}"); if (!ok) bad++; }
        var dws = asm.GetType("Mongdock.Services.DesktopWindowService")!;
        var rectT = asm.GetType("Mongdock.Native.RECT")!;
        object R(int l, int t, int r, int b2)
        {
            var o = Activator.CreateInstance(rectT)!;
            foreach (var (n, v) in new[] { ("Left", l), ("Top", t), ("Right", r), ("Bottom", b2) })
            {
                var f = rectT.GetField(n);
                if (f is not null) f.SetValue(o, v); else rectT.GetProperty(n)!.SetValue(o, v);
            }
            return o;
        }
        var should = dws.GetMethod("ShouldSendSetPos", Any)!;
        bool Send(bool sent, object prop, object last) => (bool)should.Invoke(null, new object[] { sent, prop, last })!;
        var bar = R(0, 0, 1920, 32);
        Check("등록 뒤 처음 → 보냄", Send(false, bar, R(0, 0, 0, 0)));
        Check("처음이면 자리가 같아 보여도 보냄", Send(false, bar, bar));
        Check("보낸 뒤 자리 그대로 → 안 보냄", !Send(true, bar, bar));
        Check("자리 바뀜(두께) → 보냄", Send(true, R(0, 0, 1920, 36), bar));
        Check("자리 바뀜(다른 모니터) → 보냄", Send(true, R(1920, 0, 3840, 32), bar));

        // 가짜 탐색기: 상단바 3개(모니터 3대). 알림 큐가 빌 때까지 돌리고 SETPOS·작업 표시줄 다시 띄움 횟수를 셈
        (int setPos, int reshow, bool endless) Simulate(bool newRule)
        {
            var rects = new[] { R(0, 0, 1920, 32), R(1920, 0, 3840, 32), R(3840, 0, 5760, 32) };
            var last = new object[] { R(0, 0, 0, 0), R(0, 0, 0, 0), R(0, 0, 0, 0) };
            var sent = new bool[3];
            var queue = new Queue<int>(new[] { 0, 1, 2 }); // 처음 배치
            int setPos = 0, reshow = 0, steps = 0;
            while (queue.Count > 0 && steps++ < 1000)
            {
                int i = queue.Dequeue();
                bool go = newRule ? Send(sent[i], rects[i], last[i]) : true;
                if (!go) continue;
                setPos++;
                sent[i] = true;
                last[i] = rects[i];
                reshow++; // 탐색기가 작업 표시줄 배치를 다시 계산 → 숨겨 둔 작업 표시줄을 다시 띄움
                for (int j = 0; j < 3; j++) if (j != i && !queue.Contains(j)) queue.Enqueue(j); // 다른 AppBar 에 자리 바뀜 알림
            }
            return (setPos, reshow, steps >= 1000);
        }
        var oldRule = Simulate(false);
        var newRuleRun = Simulate(true);
        Check($"예전 규칙(늘 보냄): 끝없이 돎 {oldRule.endless} (자리 알림 {oldRule.setPos}번 — 1000단계에서 끊음)", oldRule.endless);
        Check($"새 규칙: 멈춤 (자리 알림 {newRuleRun.setPos}번, 작업 표시줄 다시 띄움 {newRuleRun.reshow}번 — 상단바마다 한 번)", !newRuleRun.endless && newRuleRun.setPos == 3);
        // 둘러보기 카드 위치 (오류 신고 uid 29): 카드가 화면보다 크면 범위가 뒤집혀 예외 → 이제 가운데
        var tcw = asm.GetType("Mongdock.Views.TourCardWindow")!;
        double Fit(double top, double h, double wt, double wb) => (double)tcw.GetMethod("FitTop", Any)!.Invoke(null, new object[] { top, h, wt, wb, 28.0 })!;
        double crash = double.NaN;
        try { crash = Fit(300, 1013, 25.6, 864); } catch (Exception ex) { Check($"카드가 화면보다 클 때 예외 없음 ({ex.InnerException?.Message})", false); }
        if (!double.IsNaN(crash)) Check($"카드가 화면보다 클 때(신고 값 h 1013, 작업 영역 25.6~864) → 가운데 {crash:0.#}", Math.Abs(crash - (25.6 + (864 - 25.6 - 1013) / 2)) < 0.01);
        Check("들어가면 그대로", Fit(200, 600, 25.6, 864) == 200);
        Check("아래로 넘치면 끌어올림", Math.Abs(Fit(500, 600, 25.6, 864) - (864 - 600 + 28 - 8)) < 0.01);
        // 독 아이콘 크기 흔들림 막기 (#18): 줄이기는 바로, 키우기는 줄인 지 10초 + 2초 계속일 때만
        var dw = asm.GetType("Mongdock.Views.DockWindow")!;
        bool Grows(double of, double os, double nf, double ns) => (bool)dw.GetMethod("FitGrows", Any)!.Invoke(null, new object[] { of, os, nf, ns })!;
        bool Allowed(long now, long shrunk, long since) => (bool)dw.GetMethod("FitGrowAllowed", Any)!.Invoke(null, new object[] { now, shrunk, since })!;
        Check("50 → 설정값(0)은 키우기", Grows(50, -1, 0, -1));
        Check("47 → 50 은 키우기", Grows(47, -1, 50, -1));
        Check("50 → 47 은 줄이기", !Grows(50, -1, 47, -1));
        Check("설정값 → 50 은 줄이기", !Grows(0, -1, 50, -1));
        Check("줄인 지 3초 → 키우기 미룸", !Allowed(13_000, 10_000, 10_500));
        Check("줄인 지 12초, 들어간 지 1초 → 미룸", !Allowed(22_000, 10_000, 21_000));
        Check("줄인 지 12초, 2초 계속 들어감 → 키움", Allowed(22_000, 10_000, 20_000));
        // 23:02 로그처럼 아이콘이 몇 초마다 생겼다 없어지는 흉내: 25초 동안 몇 번 바뀌나 (예전 6번)
        {
            double fit = 0; long shrunk = 0, since = 0; int changes = 0;
            for (long t = 0; t <= 25_000; t += 500)
            {
                double want = (t / 3000) % 2 == 0 ? 50 : 0; // 3초마다 넘침 ↔ 들어감
                bool grows = Grows(fit, -1, want, -1);
                if (Math.Abs(want - fit) < 0.5) { since = 0; continue; }
                if (grows) { if (since == 0) since = t + 1; if (!Allowed(t + 1, shrunk, since)) continue; since = 0; }
                else shrunk = t + 1;
                fit = want; changes++;
            }
            Check($"3초마다 넘침↔들어감 25초: 크기 바뀜 {changes}번 (예전 규칙 9번, 키우기는 10초에 한 번까지)", changes <= 5);
        }
        Console.WriteLine(bad == 0 ? "AppBar 점검: 모두 통과" : $"AppBar 점검: 실패 {bad}개");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>--profilecheck: 루틴 항목 계정 인자(있음·없어짐·안 고름·크로미움 아님), 저장 형식(없으면 안 씀·옛 판 무시), 창 명령줄에서 계정 읽기.</summary>
    /// <summary>
    /// --clipcheck (#13): 클립보드 데이터 → 첨부(PNG·비트맵·글자·큰 그림 줄이기), 신고 창 붙여넣기(파일·그림·글자·개수 제한),
    /// 영역 캡처 흐름(가짜 캡처 도구·가짜 클립보드: 받음·취소·도구 화면 못 찾음·열기 실패·멈춤·시간 제한). 실제 클립보드·캡처 도구는 쓰지 않음.
    /// </summary>
    /// <summary>
    /// --volumecheck [DIR] (#21): 볼륨 한 칸 계산, 단축키 표의 볼륨 키(설정 켬/끔), 볼륨 키를 누르고 있을 때 반복(다른 단축키는 한 번),
    /// 그림 — 소리 판 앱별 볼륨(예시 목록)·볼륨 표시(45%·음소거) 라이트/다크. 실제 볼륨·키보드 훅은 쓰지 않음(가짜 상태·훅 흉내).
    /// </summary>
    private static int VolumeCheck(string? dir)
    {
        int bad = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  {(ok ? "통과" : "실패")}  {what}"); if (!ok) bad++; }
        var asm = typeof(Mongdock.App).Assembly;
        var osdType = asm.GetType("Mongdock.Views.VolumeOsdWindow")!;
        double Next(double v, bool up) => (double)osdType.GetMethod("Next", Any)!.Invoke(null, new object[] { v, up })!;
        Check($"0.50 → 올림 {Next(0.5, true):0.00}", Math.Abs(Next(0.5, true) - 0.52) < 1e-9);
        Check($"0.51(칸 사이) → 올림 {Next(0.51, true):0.00}", Math.Abs(Next(0.51, true) - 0.52) < 1e-9);
        Check($"0.01 → 내림 {Next(0.01, false):0.00}", Next(0.01, false) == 0);
        Check($"0.51 → 내림 {Next(0.51, false):0.00}", Math.Abs(Next(0.51, false) - 0.50) < 1e-9);
        Check($"1.00 → 올림 그대로 {Next(1, true):0.00}", Next(1, true) == 1);
        // 단축키 표
        var hc = asm.GetType("Mongdock.Views.HotkeyController")!;
        var table = hc.GetMethod("Table", Any)!;
        var s = new Settings();
        var t = (Dictionary<HotkeyChord, string>)table.Invoke(null, new object[] { s })!;
        Check($"설정 켬(기본) → 볼륨 키 3개 ({t.Values.Count(v => v.StartsWith("volume:"))})", t[new HotkeyChord(false, false, false, false, 0xAF)] == "volume:up" && t[new HotkeyChord(false, false, false, false, 0xAE)] == "volume:down" && t[new HotkeyChord(false, false, false, false, 0xAD)] == "volume:mute");
        s.TopBar.VolumeIndicator = false;
        t = (Dictionary<HotkeyChord, string>)table.Invoke(null, new object[] { s })!;
        Check("설정 끔 → 볼륨 키 없음 (윈도우가 처리)", !t.Values.Any(v => v.StartsWith("volume:")));
        Check("Shift+볼륨 올림은 몽독이 안 받음", !((Dictionary<HotkeyChord, string>)table.Invoke(null, new object[] { new Settings() })!).ContainsKey(new HotkeyChord(false, false, true, false, 0xAF)));
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        // 비용 (#21): 소리 판이 열린 동안 1초마다 하는 앱 목록 읽기 시간 (실제 장치, 읽기만) + 볼륨 표시 창을 처음 띄울 때 메모리
        {
            AppVolumeService.List(); // 처음(이름 캐시 채움)
            var swList = System.Diagnostics.Stopwatch.StartNew();
            var cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            int rows = 0;
            for (int k = 0; k < 20; k++) rows = AppVolumeService.List().Count;
            var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu0;
            Console.WriteLine($"  정보  앱 목록 읽기: 한 번 {swList.Elapsed.TotalMilliseconds / 20:0.0}ms (CPU {cpu.TotalMilliseconds / 20:0.0}ms), 이 PC 앱 {rows}개 — 판이 열린 동안 1초에 한 번");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            var s0 = new SettingsService();
            var tr = new WindowTracker();
            var la = new AppLauncher(tr);
            var sv = new AppServices(s0, tr, la, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
                new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(s0), new StartupService(),
                new NotificationService(tr, la), new TrayIconService(), new CalendarFeedService(s0));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long mid = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            osdType.GetMethod("Show", Any, new[] { typeof(AppServices), typeof(double), typeof(bool) })!.Invoke(null, new object[] { sv, 0.5, false });
            var ow = (Window)osdType.GetField("_instance", Any)!.GetValue(null)!;
            ow.UpdateLayout();
            System.Threading.Thread.Sleep(300);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long after = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            Console.WriteLine($"  정보  볼륨 표시 창 처음 띄우기: 전용 +{(after - mid) / 1048576.0:0.0}MB (창 {ow.Width:0}×{ow.Height:0} DIP 하나를 계속 다시 씀)");
            ow.Close();
            osdType.GetField("_instance", Any)!.SetValue(null, null);
            s0.Dispose();
            _ = before;
        }
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            var listProp = typeof(AppVolumeService).GetProperty("ListForTest", Any)!;
            listProp.SetValue(null, (Func<IReadOnlyList<AppVolume>>)(() => new List<AppVolume>
            {
                new("chrome", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", null, new uint[] { 1 }, 0.8, false, true, false),
                new("notepad", "Spotify", @"C:\Windows\System32\notepad.exe", null, new uint[] { 2 }, 0.45, false, true, false),
                new("explorer", "Discord — 아주 긴 앱 이름 예시", @"C:\Windows\explorer.exe", null, new uint[] { 3 }, 0.6, true, false, false),
                new(AppVolumeService.SystemKey, Mongdock.Loc.T("시스템 소리"), null, null, new uint[] { 0 }, 1.0, false, false, true),
            }));
            var kindType = asm.GetType("Mongdock.Views.StatusPanelKind")!;
            var panelType = asm.GetType("Mongdock.Views.StatusPanelWindow")!;
            Console.WriteLine($"  정보  앱 {Application.Current is not null}, MacSwitch {Application.Current?.TryFindResource("MacSwitch") is not null}");
            foreach (var theme in new[] { "light", "dark" })
            {
                var settings = new SettingsService();
                settings.Current.Dock.Theme = theme == "dark" ? DockTheme.Dark : DockTheme.Light;
                Mongdock.ViewModels.UiTheme.Apply(settings.Current);
                Mongdock.ViewModels.UiFonts.Apply(settings.Current);
                var tracker = new WindowTracker();
                var launcher = new AppLauncher(tracker);
                var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
                    new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
                    new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
                var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
                var w = (Window)panelType.GetConstructors(Any)[0].Invoke(new object[] { services, Enum.Parse(kindType, "Volume"), palette, 0 });
                ((Delegate)panelType.GetField("_applyAppVolumeForTest", Any)!.GetValue(w)!).DynamicInvoke(AppVolumeService.List());
                var content = (FrameworkElement)w.Content;
                content.Opacity = 1;
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = content.DesiredSize;
                content.Arrange(new Rect(size));
                content.UpdateLayout();
                SaveVisual(content, size, theme, Path.Combine(dir, $"panel-{theme}-volume-apps.png"));
                w.Close();
                // 테마마다 표시 창을 새로 (같은 창을 그림으로 뜨면 앞 장면이 남음 — 색 바뀜 자체는 위 정보 줄로 확인)
                if (osdType.GetField("_instance", Any)!.GetValue(null) is Window old) { old.Close(); osdType.GetField("_instance", Any)!.SetValue(null, null); }
                foreach (var (vol, muted, tag) in new[] { (0.45, false, "45"), (0.0, true, "mute") })
                {
                    osdType.GetMethod("Show", Any, new[] { typeof(AppServices), typeof(double), typeof(bool) })!.Invoke(null, new object[] { services, vol, muted });
                    var osd = (Window)osdType.GetField("_instance", Any)!.GetValue(null)!;
                    var card = (FrameworkElement)osd.Content;
                    Console.WriteLine($"  정보  표시 {theme}: 팔레트 밝음 {palette.IsLight}, 카드 {((System.Windows.Controls.Border)card).Background}");
                    card.BeginAnimation(UIElement.OpacityProperty, null);
                    card.Opacity = 1;
                    card.RenderTransform = Transform.Identity;
                    SaveVisual(card, new Size(osd.Width, osd.Height), theme, Path.Combine(dir, $"volume-osd-{theme}-{tag}.png"));
                    osdType.GetMethod("HideNow", Any)!.Invoke(null, null);
                }
                Check($"그림 {theme}: 소리 판·볼륨 표시 2장", true);
                settings.Dispose();
            }
            listProp.SetValue(null, null);
        }
        // 훅 흉내: 볼륨 올림을 누르고 있으면(다운 3번) 3번, 음소거는 1번, 업은 삼킴
        var keyState = typeof(HotkeyService).GetProperty("KeyStateForTest", Any)!;
        keyState.SetValue(null, (Func<int, bool>)(_ => false));
        var hs = new HotkeyService();
        var sim = typeof(HotkeyService).GetMethod("SimulateForTest", Any)!;
        bool Sim(int vk, bool down) => (bool)sim.Invoke(hs, new object[] { vk, down })!;
        var pressed = new List<string>();
        hs.Pressed += a => pressed.Add(a);
        hs.Apply((Dictionary<HotkeyChord, string>)table.Invoke(null, new object[] { new Settings() })!);
        bool sw1 = Sim(0xAF, true), sw2 = Sim(0xAF, true), sw3 = Sim(0xAF, true), up1 = Sim(0xAF, false);
        bool m1 = Sim(0xAD, true), m2 = Sim(0xAD, true), mu = Sim(0xAD, false);
        for (int i = 0; i < 5; i++) System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); // 훅이 올린 Pressed 처리
        Check($"볼륨 올림 누르고 있기(다운 3번) → {pressed.Count(a => a == "volume:up")}번, 다운·업 삼킴 {sw1 && sw2 && sw3 && up1}", pressed.Count(a => a == "volume:up") == 3 && sw1 && sw2 && sw3 && up1);
        Check($"음소거 누르고 있기 → {pressed.Count(a => a == "volume:mute")}번 (한 번만), 삼킴 {m1 && m2 && mu}", pressed.Count(a => a == "volume:mute") == 1 && m1 && m2 && mu);
        Check($"다른 키(A)는 그대로 통과", !Sim(0x41, true));
        GC.KeepAlive(hs); // Dispose 하면 훅 스레드 정리가 시험 앱 Dispatcher 까지 끝내 그림을 못 그림 — 시험이 끝나면 프로세스째 정리
        keyState.SetValue(null, null);

        // 재생 진행 막대 (#22): 재생 앱 이름 ↔ 독 항목 짝
        var dockType = asm.GetType("Mongdock.Views.DockWindow")!;
        bool Match(string src, PinItem? pin, params AppWindowInfo[] wins) => (bool)dockType.GetMethod("MediaSourceMatches", Any)!.Invoke(null, new object?[] { src, pin, wins })!;
        AppWindowInfo Win(string path, string? aumid = null) => new(IntPtr.Zero, "t", path, aumid, false);
        var spotifyPin = new PinItem { Kind = PinKind.Exe, Target = @"C:\Users\a\AppData\Roaming\Spotify\Spotify.exe" };
        Check("Spotify.exe ↔ Spotify 핀", Match("Spotify.exe", spotifyPin));
        Check("Chrome ↔ chrome.exe 창", Match("Chrome", null, Win(@"C:\Program Files\Google\Chrome\Application\chrome.exe")));
        Check("MSEdge ↔ msedge.exe 창", Match("MSEdge", null, Win(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")));
        Check("스토어 앱 AUMID ↔ 그 앱 핀", Match("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", new PinItem { Kind = PinKind.Aumid, Target = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" }));
        Check("다른 앱은 아님 (Spotify ↔ chrome)", !Match("Spotify.exe", null, Win(@"C:\x\chrome.exe")));
        Check("빈 이름은 아님", !Match("  ", spotifyPin));
        if (dir is not null)
        {
            // 독 아이콘 그림: 재생 진행 40% (라이트·다크 독, 아이콘 52·72)
            foreach (var (theme, size) in new[] { ("light", 52.0), ("dark", 52.0), ("light", 72.0) })
            {
                var ds = new DockSettings { Theme = theme == "dark" ? DockTheme.Dark : DockTheme.Light, IconSize = size };
                var layout = Mongdock.ViewModels.DockLayout.From(ds, theme == "light");
                var icon = new IconService().GetIcon(new PinItem { Kind = PinKind.Exe, Target = @"C:\Windows\System32\notepad.exe" }, IconStyle.Mac);
                var vm = new Mongdock.ViewModels.DockItemViewModel("t", null, false, "Spotify", icon) { IsRunning = true, Progress = 0.4 };
                var view = (FrameworkElement)Activator.CreateInstance(asm.GetType("Mongdock.Views.DockItemView")!, vm, layout)!;
                var host = new System.Windows.Controls.Border { Padding = new Thickness(16), Background = new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x2A, 0x2A, 0x30) : Color.FromRgb(0xF0, 0xF0, 0xF5)), Child = view };
                host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();
                SaveVisual(host, host.DesiredSize, theme, Path.Combine(dir, $"dock-progress-{theme}-{size:0}.png"));
            }
        }
        // 창 최소화 효과 (#20): 윈도우 최소화 애니메이션 끄기·되돌리기 (가짜 시스템 설정 — 실제 설정은 안 건드림)
        {
            var was = typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.WindowAnimationSetting")!;
            var sysProp = was.GetProperty("SystemForTest", Any)!;
            bool sysOn = true;
            sysProp.SetValue(null, ((Func<bool?>)(() => sysOn), (Action<bool>)(v => sysOn = v)));
            string statePath = (string)was.GetProperty("StatePath", Any)!.GetValue(null)!;
            if (File.Exists(statePath)) File.Delete(statePath);
            bool TurnOff() => (bool)was.GetMethod("TurnOff", Any)!.Invoke(null, null)!;
            void Restore(string why) => was.GetMethod("Restore", Any)!.Invoke(null, new object[] { why });
            bool ByMong() => (bool)was.GetProperty("TurnedOffByMongdock", Any)!.GetValue(null)!;
            Check($"켜기 → 윈도우 효과 끔 {TurnOff()}, 기록 {File.Exists(statePath)}, 몽독이 끈 상태 {ByMong()}", !sysOn && File.Exists(statePath) && ByMong());
            Check("다시 켜기 → 그대로(이미 꺼짐), 기록 유지", !TurnOff() && !sysOn && File.Exists(statePath));
            // 갑자기 꺼짐 흉내: 기록이 남은 채로 다음 실행 → 효과 끔이면 되돌림
            Restore("이전 실행 복구");
            Check($"끄기·다음 실행 복구 → 윈도우 효과 되돌림 {sysOn}, 기록 지움 {!File.Exists(statePath)}", sysOn && !File.Exists(statePath));
            // 원래 꺼져 있던 PC: 끄고 되돌려도 꺼진 채
            sysOn = false;
            TurnOff();
            Restore("끔");
            Check("원래 꺼져 있었으면 되돌려도 꺼진 채", !sysOn && !File.Exists(statePath));
            sysProp.SetValue(null, null);
        }
        // 모양 계산 (차분하게·아이콘으로·지니 + 흐려짐만, 되살리기는 같은 길 거꾸로)
        {
            var mec = typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.MinimizeEffectController")!;
            var mm = typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.MinimizeMotion")!;
            Rect Genie(Rect f, Rect t, double v0, double v1, double x) => (Rect)mec.GetMethod("GenieStrip", Any, new[] { typeof(Rect), typeof(Rect), typeof(double), typeof(double), typeof(double) })!.Invoke(null, new object[] { f, t, v0, v1, x })!;
            (Rect R, double O) Frame(MinimizeEffect e, DockEdge edge, Rect f, Rect t, double x) => ((Rect, double))mm.GetMethod("Frame", Any)!.Invoke(null, new object[] { e, edge, f, t, x })!;
            int Dur(MinimizeEffect e, bool restore, MinimizeSpeed sp = MinimizeSpeed.Normal) => (int)mm.GetMethod("DurationMs", Any)!.Invoke(null, new object[] { e, restore, sp })!;
            double PathAt(double t, bool restore) => (double)mm.GetMethod("Path", Any)!.Invoke(null, new object[] { t, restore })!;
            double Op(MinimizeEffect e, double x) => (double)mm.GetMethod("Opacity", Any)!.Invoke(null, new object[] { e, x })!;
            double BlurAt(MinimizeEffect e, double x) => (double)mm.GetMethod("Blur", Any)!.Invoke(null, new object[] { e, x })!;
            MinimizeEffect Resolve(MinimizeEffect e, DockEdge edge, bool reduced) => (MinimizeEffect)mec.GetMethod("Resolve", Any)!.Invoke(null, new object[] { e, edge, reduced })!;
            bool Near(Rect a, Rect b) => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;
            var from = new Rect(300, 150, 1200, 760);
            var icon = new Rect(880, 1000, 64, 64);

            Check($"시간(보통): 차분하게 {Dur(MinimizeEffect.Settle, false)} · 아이콘으로 {Dur(MinimizeEffect.Scale, false)} · 지니 {Dur(MinimizeEffect.Genie, false)} ms, 되살리기 0.8배",
                Dur(MinimizeEffect.Settle, false) == 220 && Dur(MinimizeEffect.Scale, false) == 300 && Dur(MinimizeEffect.Genie, false) == 480
                && Dur(MinimizeEffect.Settle, true) == 176 && Dur(MinimizeEffect.Genie, true) == 384);
            Check($"속도: 지니 빠르게 {Dur(MinimizeEffect.Genie, false, MinimizeSpeed.Fast)} · 느리게 {Dur(MinimizeEffect.Genie, false, MinimizeSpeed.Slow)} ms, 흐려짐만은 속도와 상관없이 {Dur(MinimizeEffect.Off, false, MinimizeSpeed.Slow)}",
                Dur(MinimizeEffect.Genie, false, MinimizeSpeed.Fast) == 336 && Dur(MinimizeEffect.Genie, false, MinimizeSpeed.Slow) == 672 && Dur(MinimizeEffect.Off, false, MinimizeSpeed.Slow) == 120);
            // 들어갈 때: 천천히 시작(처음 몇 프레임에 다 움직이지 않음)·천천히 멈춤, 한 방향·0~1 안(탄성 없음)
            bool Mono(bool restore)
            {
                double prev = PathAt(0, restore);
                for (int i = 1; i <= 100; i++)
                {
                    double p = PathAt(i / 100.0, restore);
                    if (p < 0 || p > 1 || (restore ? p > prev + 1e-9 : p < prev - 1e-9)) return false;
                    prev = p;
                }
                return true;
            }
            double first = PathAt(0.1, false), mid = PathAt(0.55, false) - PathAt(0.45, false), last = 1 - PathAt(0.9, false);
            double frame2 = PathAt(33.0 / Dur(MinimizeEffect.Settle, false), false);
            Check($"들어갈 때: 처음 10% {first:0.000} < 가운데 10% {mid:0.000} > 끝 10% {last:0.000}, 둘째 프레임(33ms)까지 {frame2:P0}만 움직임, 탄성 없음",
                Mono(false) && first < mid && last < mid && frame2 < 0.25);
            double rFirst = 1 - PathAt(0.1, true), rLast = PathAt(0.9, true);
            Check($"되살리기: 끝(1) → 창 자리(0), 창 자리에 닿을 때 천천히 (처음 10% {rFirst:0.000} > 끝 10% {rLast:0.000}), 탄성 없음",
                PathAt(0, true) == 1 && PathAt(1, true) == 0 && Mono(true) && rFirst > rLast * 5);
            Check($"흐려짐은 끝 30% 안에서만 (p=0.7 불투명도 {Op(MinimizeEffect.Settle, 0.7):0.00}, p=1 차분하게 {Op(MinimizeEffect.Settle, 1):0.00} · 아이콘으로 {Op(MinimizeEffect.Scale, 1):0.00})",
                Op(MinimizeEffect.Settle, 0.7) == 1 && Op(MinimizeEffect.Genie, 0.5) == 1 && Op(MinimizeEffect.Settle, 1) == 0 && Math.Abs(Op(MinimizeEffect.Scale, 1) - 0.1) < 1e-9);
            Check($"아이콘으로: 끝에서만 흐림 (p=0.6 {BlurAt(MinimizeEffect.Scale, 0.6):0}px, p=1 {BlurAt(MinimizeEffect.Scale, 1):0}px), 다른 모양은 흐림 없음",
                BlurAt(MinimizeEffect.Scale, 0.6) == 0 && BlurAt(MinimizeEffect.Scale, 1) > 0 && BlurAt(MinimizeEffect.Settle, 1) == 0);

            foreach (var e in new[] { MinimizeEffect.Settle, MinimizeEffect.Scale, MinimizeEffect.Off })
            {
                var (r0, o0) = Frame(e, DockEdge.Bottom, from, icon, 0);
                Check($"{e} p=0 → 창 자리 그대로·불투명", Near(r0, from) && o0 == 1);
            }
            var settleEnd = Frame(MinimizeEffect.Settle, DockEdge.Bottom, from, icon, 1);
            Check($"차분하게 p=1 → 0.82배 ({settleEnd.R.Width:0}×{settleEnd.R.Height:0}), 아래로 {settleEnd.R.Bottom - from.Bottom:0}px, 아이콘 쪽으로, 사라짐",
                Math.Abs(settleEnd.R.Width - from.Width * 0.82) < 0.5 && Math.Abs(settleEnd.R.Bottom - (from.Bottom + from.Height * 0.18)) < 0.5
                && Math.Abs(settleEnd.R.X + settleEnd.R.Width / 2 - (900 + (912 - 900) * 0.35)) < 0.5 && settleEnd.O == 0);
            var settleLeft = Frame(MinimizeEffect.Settle, DockEdge.Left, from, icon, 1);
            Check("차분하게 (독 왼쪽) → 왼쪽으로", settleLeft.R.Left < from.Left && Math.Abs(settleLeft.R.Height - from.Height * 0.82) < 0.5);
            var scaleEnd = Frame(MinimizeEffect.Scale, DockEdge.Bottom, from, icon, 1);
            Check($"아이콘으로 p=1 → 아이콘 자리, 옅게 ({scaleEnd.O:0.00})", Near(scaleEnd.R, icon) && scaleEnd.O < 0.2 && scaleEnd.O > 0);
            var fade = Frame(MinimizeEffect.Off, DockEdge.Bottom, from, icon, 0.5);
            Check("흐려짐만 → 자리 그대로, 처음부터 옅어짐 (p=0.5 → 0.5)", Near(fade.R, from) && Math.Abs(fade.O - 0.5) < 1e-9);
            Check("지니는 독이 아래일 때만 (옆이면 아이콘으로), 애니메이션 줄이기·절전 → 흐려짐만",
                Resolve(MinimizeEffect.Genie, DockEdge.Bottom, false) == MinimizeEffect.Genie && Resolve(MinimizeEffect.Genie, DockEdge.Left, false) == MinimizeEffect.Scale
                && Resolve(MinimizeEffect.Settle, DockEdge.Left, false) == MinimizeEffect.Settle && Resolve(MinimizeEffect.Settle, DockEdge.Bottom, true) == MinimizeEffect.Off);

            var top0 = Genie(from, icon, 0, 1.0 / 24, 0);
            Check($"지니 p=0 → 띠가 창과 같은 폭·자리 ({top0.Left:0}~{top0.Right:0}, 위 {top0.Top:0})", Math.Abs(top0.Left - from.Left) < 0.5 && Math.Abs(top0.Right - from.Right) < 0.5 && Math.Abs(top0.Top - from.Top) < 0.5);
            var bendTop = Genie(from, icon, 0, 1.0 / 24, 0.15);
            var bendBottom = Genie(from, icon, 23.0 / 24, 1, 0.15);
            Check($"지니 앞부분 p=0.15 → 위는 조금만 내려가고(위 {bendTop.Top - from.Top:0}px) 아래가 먼저 좁아져 휨 ({bendBottom.Width:0} < {bendTop.Width:0})",
                bendTop.Top - from.Top < 0.1 * (icon.Top - from.Top) && bendBottom.Width < bendTop.Width * 0.95);
            // 휘는 모양이 단계적으로 이어짐: 창 옆선의 기울기(띠 사이 각도)가 프레임마다(60fps) 급하게 바뀌지 않음 + 참고로 띠 폭 변화
            double Angle(double p, int i)
            {
                var r0 = Genie(from, icon, i / 24.0, (i + 1) / 24.0, p);
                var r1 = Genie(from, icon, (i + 1) / 24.0, (i + 2) / 24.0, p);
                return Math.Atan2(r1.Left - r0.Left, Math.Max(1e-6, r1.Top - r0.Top)) * 180 / Math.PI;
            }
            double maxTurn = 0, maxJump = 0;
            (int K, int Strip, double P) turnAt = default;
            int frames = (int)Math.Ceiling(Dur(MinimizeEffect.Genie, false) / 16.7);
            for (int k = 1; k <= frames; k++)
            {
                double p0 = PathAt((k - 1) / (double)frames, false), p1 = PathAt(k / (double)frames, false);
                for (int i = 0; i < 23; i++)
                {
                    // 끝 20%(아이콘 목에 눌려 들어가며 띠 높이가 몇 px — 작고 옅어 각도가 보이지 않음)는 뺌
                    double d = p1 > 0.8 || Genie(from, icon, i / 24.0, (i + 1) / 24.0, p1).Height < 4 ? 0 : Math.Abs(Angle(p1, i) - Angle(p0, i));
                    if (d > maxTurn) { maxTurn = d; turnAt = (k, i, p1); }
                    maxJump = Math.Max(maxJump, Math.Abs(Genie(from, icon, i / 24.0, (i + 1) / 24.0, p1).Width - Genie(from, icon, i / 24.0, (i + 1) / 24.0, p0).Width) / from.Width);
                }
            }
            Check($"지니: 옆선 각도가 한 프레임(60fps)에 가장 많이 바뀌는 양 {maxTurn:0.0}° (길 80%까지·띠 높이 4px 이상, 10° 아래 — 급한 꺾임 없음, 기준은 개발 임시값; 프레임 {turnAt.K}/{frames}, 띠 {turnAt.Strip}, p={turnAt.P:0.00}), 참고: 띠 폭 변화 최대 창 폭의 {maxJump:P1}", maxTurn < 10);
            var endTop = Genie(from, icon, 0, 1.0 / 24, 1);
            var endBottom = Genie(from, icon, 23.0 / 24, 1, 1);
            Check($"지니 p=1 → 아이콘 안 ({endTop.Left:0}~{endTop.Right:0}, {endTop.Top:0}~{endBottom.Bottom:0})", endTop.Left >= icon.Left - 0.5 && endTop.Right <= icon.Right + 0.5 && endTop.Top >= icon.Top - 0.5 && endBottom.Bottom <= icon.Bottom + 0.5);
            if (dir is not null)
            {
                // 모양 그림: 1920×1080 화면을 1/4 로, 3가지 모양을 칸마다, 실제 시간 0·20·40·60·80·95% 의 자리를 겹쳐 그림 (테두리 굵기 = 불투명도)
                var ts = new[] { 0.0, 0.2, 0.4, 0.6, 0.8, 0.95 };
                var colors = new[] { Colors.SteelBlue, Colors.MediumPurple, Colors.Orchid, Colors.Coral, Colors.Goldenrod, Colors.Gold };
                var shapes = new[] { (MinimizeEffect.Settle, "차분하게"), (MinimizeEffect.Scale, "아이콘으로"), (MinimizeEffect.Genie, "지니") };
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1440, 270));
                    for (int si = 0; si < shapes.Length; si++)
                    {
                        var (e, name) = shapes[si];
                        dc.PushTransform(new TranslateTransform(si * 480, 0));
                        dc.DrawRectangle(null, new Pen(Brushes.LightGray, 1), new Rect(0, 0, 480, 270));
                        dc.DrawText(new FormattedText($"{name} ({Dur(e, false)}ms)", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 13, Brushes.Black, 1), new Point(8, 6));
                        dc.PushTransform(new ScaleTransform(0.25, 0.25));
                        dc.DrawRectangle(null, new Pen(Brushes.Gray, 4), icon);
                        for (int k = 0; k < ts.Length; k++)
                        {
                            var c = colors[k];
                            double p = PathAt(ts[k], false);
                            if (e == MinimizeEffect.Genie)
                                for (int i = 0; i < 24; i++)
                                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(50, c.R, c.G, c.B)), new Pen(new SolidColorBrush(c), 3), Genie(from, icon, i / 24.0, (i + 1) / 24.0, p));
                            else
                            {
                                var (r, o) = Frame(e, DockEdge.Bottom, from, icon, p);
                                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(70 * o), c.R, c.G, c.B)), new Pen(new SolidColorBrush(c), 2 + 6 * o), r);
                            }
                        }
                        dc.Pop();
                        dc.Pop();
                    }
                }
                var rtb = new RenderTargetBitmap(1440, 270, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(dir, "minimize-shapes.png"))) enc.Save(fs);
                Console.WriteLine($"  저장  {Path.Combine(dir, "minimize-shapes.png")}");
            }
        }
        Console.WriteLine(bad == 0 ? "볼륨 점검: 모두 통과" : $"볼륨 점검: 실패 {bad}개");
        return bad == 0 ? 0 : 1;
    }

    private static void SaveVisual(FrameworkElement el, Size size, string theme, string path)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x14, 0x16, 0x1C) : Color.FromRgb(0xE9, 0xEC, 0xF4)), null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(el) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), Stretch = Stretch.None }, null, new Rect(size));
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        Console.WriteLine($"  저장  {path}");
    }

    private static int ClipCheck()
    {
        int bad = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  {(ok ? "통과" : "실패")}  {what}"); if (!ok) bad++; }
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        byte[] Png(int w, int h)
        {
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, w, h));
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        DataObject PngData(int w, int h) { var d = new DataObject(); d.SetData("PNG", new MemoryStream(Png(w, h))); return d; }

        // 1) 클립보드 데이터 → 첨부
        var small = ScreenClip.FromDataObject(PngData(400, 300));
        Check($"PNG 그림 → {small?.Name} ({small?.Mime})", small is { Mime: "image/png" } && small.Name.StartsWith("clip-") && small.Name.EndsWith(".png"));
        var big = ScreenClip.FromDataObject(PngData(3000, 1800));
        var bigFrame = big is null ? null : BitmapFrame.Create(new MemoryStream(big.Data));
        Check($"큰 그림 3000×1800 → {big?.Mime} {bigFrame?.PixelWidth}×{bigFrame?.PixelHeight} (긴 변 2000)", big is { Mime: "image/jpeg" } && bigFrame?.PixelWidth == 2000);
        var bmpData = new DataObject();
        bmpData.SetData(DataFormats.Bitmap, BitmapFrame.Create(new MemoryStream(Png(200, 100))));
        var fromBmp = ScreenClip.FromDataObject(bmpData);
        Check($"비트맵 형식만 → {fromBmp?.Mime}", fromBmp is { Mime: "image/png" });
        Check("글자만 → 그림 아님", ScreenClip.FromDataObject(new DataObject(DataFormats.UnicodeText, "hello")) is null);

        // 2) 신고 창 붙여넣기
        var w = new ReportWindow(new Settings()) { Left = -20000, Top = -20000, ShowActivated = false };
        w.Show();
        var paste = typeof(ReportWindow).GetMethod("PasteAttachments", Any)!;
        var list = (System.Collections.IList)typeof(ReportWindow).GetField("_attachments", Any)!.GetValue(w)!;
        var err = (System.Windows.Controls.TextBlock)typeof(ReportWindow).GetField("_attachError", Any)!.GetValue(w)!;
        bool Paste(IDataObject d) => (bool)paste.Invoke(w, new object[] { d })!;
        Check("글자 붙여넣기 → 입력 칸에 맡김 (첨부 안 함)", !Paste(new DataObject(DataFormats.UnicodeText, "hello")) && list.Count == 0);
        Check($"그림 붙여넣기 → 첨부 {list.Count + 1}개", Paste(PngData(640, 480)) && list.Count == 1);
        string tmp = Path.Combine(Path.GetTempPath(), "mongdock-clipcheck");
        Directory.CreateDirectory(tmp);
        string png = Path.Combine(tmp, "shot.png"), txt = Path.Combine(tmp, "note.txt"), exe = Path.Combine(tmp, "tool.exe");
        File.WriteAllBytes(png, Png(300, 200));
        File.WriteAllText(txt, @"C:\Users\melon\Desktop\연봉.xlsx");
        File.WriteAllBytes(exe, new byte[10]);
        var files = new DataObject();
        files.SetData(DataFormats.FileDrop, new[] { png, txt });
        bool pastedFiles = Paste(files);
        Check($"파일 2개 붙여넣기 → 첨부 {list.Count}개 (3 기대)", pastedFiles && list.Count == 3);
        var noteText = System.Text.Encoding.UTF8.GetString(((ReportAttachment)list[2]!).Data);
        Check($"붙여 넣은 텍스트도 가림 ({noteText})", !noteText.Contains("melon"));
        bool fourth = Paste(PngData(100, 100));
        Check($"넷째 그림 → 개수 제한 안내 ({err.Text})", fourth && list.Count == 3 && err.Visibility == Visibility.Visible);
        list.Clear();
        var exeDrop = new DataObject();
        exeDrop.SetData(DataFormats.FileDrop, new[] { exe });
        err.Visibility = Visibility.Collapsed;
        bool exeHandled = Paste(exeDrop);
        Check($"허용 안 되는 파일 → 안내 ({err.Text})", exeHandled && list.Count == 0 && err.Visibility == Visibility.Visible && err.Text.Contains("mp4"));
        // 캡처 때 다른 몽독 창도 숨김 (#17): 작업 표시줄에 뜨는 일반 창만, 독·상단바 같은 창(ShowInTaskbar=false)은 그대로
        var other = new Window { Left = -20000, Top = -20000, Width = 200, Height = 100, ShowInTaskbar = true, ShowActivated = false, Title = "설정 흉내" };
        var bar = new Window { Left = -20000, Top = -19000, Width = 200, Height = 30, ShowInTaskbar = false, ShowActivated = false, Title = "상단바 흉내" };
        other.Show();
        bar.Show();
        var hidden = (List<Window>)typeof(ReportWindow).GetMethod("HideForCapture", Any)!.Invoke(w, null)!;
        Check($"캡처 때 숨김: 신고 창 {!w.IsVisible}, 일반 창 {!other.IsVisible}, 상단바 같은 창은 그대로 {bar.IsVisible}", !w.IsVisible && !other.IsVisible && bar.IsVisible && hidden.Count == 1);
        typeof(ReportWindow).GetMethod("RestoreAfterCapture", Any)!.Invoke(null, new object[] { hidden });
        w.Show();
        Check($"캡처 뒤 되돌림: 일반 창 {other.IsVisible}, 신고 창 {w.IsVisible}", other.IsVisible && w.IsVisible);
        other.Close();
        bar.Close();
        w.Close();
        Directory.Delete(tmp, true);

        // 3) 영역 캡처 흐름 (가짜 도구·클립보드)
        var sc = typeof(ScreenClip);
        object? Get(string f) => sc.GetField(f, Any)!.GetValue(null);
        var saved = new[] { "Launch", "Sequence", "OverlayVisible", "ReadClipboard" }.ToDictionary(f => f, Get);
        void Set(string f, object v) => sc.GetField(f, Any)!.SetValue(null, v);
        var shot = new ReportAttachment("clip-test.png", "image/png", Png(50, 50));
        (ReportAttachment?, ScreenClipOutcome, int gone, long ms) Run(Func<long, bool> overlay, long imageAt, bool launchOk = true, int timeout = 8000, int stopAt = -1)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Set("Launch", new Func<bool>(() => launchOk));
            Set("Sequence", new Func<uint>(() => imageAt >= 0 && clock.ElapsedMilliseconds >= imageAt ? 2u : 1u));
            Set("OverlayVisible", new Func<bool>(() => overlay(clock.ElapsedMilliseconds)));
            Set("ReadClipboard", new Func<ReportAttachment?>(() => shot));
            int gone = 0;
            using var cts = new CancellationTokenSource();
            if (stopAt >= 0) cts.CancelAfter(stopAt);
            var t = ScreenClip.CaptureAsync(() => gone++, cts.Token, timeout);
            while (!t.IsCompleted) Thread.Sleep(20);
            var (a, o) = t.Result;
            return (a, o, gone, clock.ElapsedMilliseconds);
        }
        try
        {
            var r = Run(ms => ms > 300 && ms < 1500, 1400);
            Check($"도구 화면 → 그림 들어옴: {r.Item2}, 신고 창 다시 띄움 {r.gone}번, {r.ms}ms", r.Item2 == ScreenClipOutcome.Captured && r.Item1 == shot && r.gone == 1);
            r = Run(ms => ms > 300 && ms < 1200, -1);
            Check($"그림 없이 도구 닫음 → {r.Item2} ({r.ms}ms, 1.2초 + 1.5초쯤)", r.Item2 == ScreenClipOutcome.Cancelled && r.gone == 1 && r.ms is > 2500 and < 3600);
            r = Run(_ => false, 4500);
            Check($"도구 화면 못 찾음 → 3초에 신고 창 다시, 그 뒤 그림 받음: {r.Item2} ({r.ms}ms, 다시 띄움 {r.gone}번)", r.Item2 == ScreenClipOutcome.Captured && r.gone == 1 && r.ms > 4400);
            r = Run(_ => false, -1, timeout: 4000);
            Check($"도구 화면도 그림도 없음 → {r.Item2} ({r.ms}ms)", r.Item2 == ScreenClipOutcome.NoTool && r.gone == 1);
            r = Run(_ => true, -1, launchOk: false);
            Check($"도구 열기 실패 → {r.Item2}", r.Item2 == ScreenClipOutcome.LaunchFailed && r.gone == 1);
            r = Run(_ => true, -1, stopAt: 800);
            Check($"[취소] → {r.Item2} ({r.ms}ms)", r.Item2 == ScreenClipOutcome.Stopped && r.gone == 1 && r.ms < 1500);
            Set("ReadClipboard", new Func<ReportAttachment?>(() => null));
            var clock2 = System.Diagnostics.Stopwatch.StartNew();
            Set("Launch", new Func<bool>(() => true));
            Set("Sequence", new Func<uint>(() => clock2.ElapsedMilliseconds > 500 ? 2u : 1u));
            Set("OverlayVisible", new Func<bool>(() => clock2.ElapsedMilliseconds < 1000));
            var t2 = ScreenClip.CaptureAsync(null, CancellationToken.None, 8000);
            while (!t2.IsCompleted) Thread.Sleep(20);
            Check($"글자가 복사됨(그림 아님) → 계속 기다리다 도구 닫힘으로 끝: {t2.Result.Outcome}", t2.Result.Outcome == ScreenClipOutcome.Cancelled);
        }
        finally
        {
            foreach (var (f, v) in saved) Set(f, v!);
        }
        Console.WriteLine($"  정보  이 PC 캡처 도구 있음(버튼 보임): {ScreenClip.Available()}");
        Console.WriteLine(bad == 0 ? "영역 캡처·붙여넣기 점검: 모두 통과" : $"영역 캡처·붙여넣기 점검: 실패 {bad}개");
        return bad == 0 ? 0 : 1;
    }

    private static int ProfileCheck()
    {
        var asm = typeof(Mongdock.App).Assembly;
        int bad = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  {(ok ? "통과" : "실패")}  {what}"); if (!ok) bad++; }
        var rp = asm.GetType("Mongdock.Services.RoutineProfiles")!;
        var profType = asm.GetType("Mongdock.Services.ChromiumProfiles+Profile")!;
        var listT = typeof(List<>).MakeGenericType(profType);
        var example = (System.Collections.IList)Activator.CreateInstance(listT)!;
        example.Add(Activator.CreateInstance(profType, "Default", "개인", null));
        example.Add(Activator.CreateInstance(profType, "Profile 1", "회사", null));
        var funcT = typeof(Func<,>).MakeGenericType(typeof(string), typeof(IReadOnlyList<>).MakeGenericType(profType));
        var del = Delegate.CreateDelegate(funcT, new ExampleProfiles(example), typeof(ExampleProfiles).GetMethod("Get")!.MakeGenericMethod(profType));
        rp.GetProperty("ProfilesForTest", Any)!.SetValue(null, del);
        try
        {
            string? Arg(RoutineItem i) => (string?)rp.GetMethod("Arg", Any)!.Invoke(null, new object[] { i });
            var chrome = new RoutineItem { Kind = RoutineItemKind.App, Target = @"C:\Program Files\Google\Chrome\Application\chrome.exe", Profile = "Profile 1" };
            Check($"고른 계정 → {Arg(chrome)}", Arg(chrome) == "--profile-directory=\"Profile 1\"");
            chrome.Profile = "Profile 9";
            Check($"없어진 계정 → 기본 ({Arg(chrome) ?? "인자 없음"})", Arg(chrome) is null);
            chrome.Profile = null;
            Check("안 고름 → 인자 없음", Arg(chrome) is null);
            var store = new RoutineItem { Kind = RoutineItemKind.App, Aumid = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", Profile = "Profile 1" };
            Check("스토어 앱(AUMID) → 계정 없음", Arg(store) is null);
            // 새 창 (0.6.1 제보: 다른 데스크톱·다른 계정 창이 있어도 크롬 탭으로 들어감) — 브라우저는 켜져 있든 말든 늘 새 창
            var rs = asm.GetType("Mongdock.Services.RoutineService")!;
            string Args(RoutineItem i, bool running) => (string)rs.GetMethod("AppArgs", Any)!.Invoke(null, new object[] { i, running })!;
            var web = new RoutineItem { Kind = RoutineItemKind.App, Target = @"C:\Program Files\Google\Chrome\Application\chrome.exe", Open = "https://mail.company.com", Profile = "Profile 1" };
            Check($"크롬 (안 켜진 줄 앎) → {Args(web, false)}", Args(web, false) == "--profile-directory=\"Profile 1\" --new-window https://mail.company.com");
            web.Args = "--new-window";
            Check($"실행 옵션에 이미 --new-window → 한 번만 ({Args(web, true)})", Args(web, true) == "--profile-directory=\"Profile 1\" --new-window https://mail.company.com");
            var code = new RoutineItem { Kind = RoutineItemKind.App, Target = @"C:\x\Code.exe", Open = @"C:\dev\mongdock" };
            Check($"VS Code 안 켜짐 → -n 없음 ({Args(code, false)})", Args(code, false) == @"C:\dev\mongdock");
            Check($"VS Code 켜짐 → -n ({Args(code, true)})", Args(code, true) == @"-n C:\dev\mongdock");
        }
        finally { rp.GetProperty("ProfilesForTest", Any)!.SetValue(null, null); }
        // 저장 형식: 없으면 안 씀, 있으면 "profile", 옛 판(모르는 키 무시)과 같은 방식
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        string none = System.Text.Json.JsonSerializer.Serialize(new RoutineItem { Kind = RoutineItemKind.App, Target = "a.exe" }, opts);
        string with = System.Text.Json.JsonSerializer.Serialize(new RoutineItem { Kind = RoutineItemKind.App, Target = "a.exe", Profile = "Profile 1" }, opts);
        Check($"계정 없으면 저장 안 함 ({!none.Contains("profile")})", !none.Contains("profile"));
        Check($"계정 저장 ({with.Contains("\"profile\":\"Profile 1\"")})", with.Contains("\"profile\":\"Profile 1\""));
        var back = System.Text.Json.JsonSerializer.Deserialize<RoutineItem>(with, opts)!;
        Check($"다시 읽기 ({back.Profile})", back.Profile == "Profile 1");
        // 창 명령줄에서 계정
        var cp = asm.GetType("Mongdock.Services.ChromiumProfiles")!;
        string? Parse(string c) => (string?)cp.GetMethod("ParseProfileDir", Any)!.Invoke(null, new object[] { c });
        Check("명령줄 따옴표 계정", Parse("\"C:\\x\\chrome.exe\" --profile-directory=\"Profile 3\"") == "Profile 3");
        Check("명령줄 따옴표 없는 계정", Parse("chrome.exe --profile-directory=Default") == "Default");
        // 링크 칸에 여러 주소 붙여 넣기 (0.6.1): 줄·공백·쉼표로 나눔, 빈 줄·중복·주소 아닌 글은 뺌
        var ed = asm.GetType("Mongdock.Views.RoutineEditorWindow")!;
        List<string> Split(string t) => (List<string>)ed.GetMethod("SplitLinks", Any)!.Invoke(null, new object[] { t })!;
        var sp = Split("https://a.com/x\r\n\r\nhttps://b.com, notion.so/team  안녕하세요\nhttps://a.com/x\nwww.c.org");
        Check($"여러 주소 나누기: {string.Join(" | ", sp)}", sp.SequenceEqual(new[] { "https://a.com/x", "https://b.com", "notion.so/team", "www.c.org" }));
        Check("주소 하나는 하나", Split("https://a.com").Count == 1);
        Console.WriteLine(bad == 0 ? "계정 점검: 모두 통과" : $"계정 점검: 실패 {bad}개");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>--hotkeycheck: 단축키 조합 글자·막는 키·옛 설정 옮기기·키 표·훅 처리(가짜 키 — 진짜 훅·입력 없음).</summary>
    private static int HotkeyCheck()
    {
        var asm = typeof(Mongdock.App).Assembly;
        int bad = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  {(ok ? "통과" : "실패")}  {what}"); if (!ok) bad++; }
        // 글자 ↔ 조합
        foreach (var t in new[] { "Ctrl+Alt+K", "Win+Space", "Ctrl+Shift+F5", "Alt+Win+0", "F13", "Ctrl+Alt+Num+", "Ctrl+Alt+;" })
        {
            var c = HotkeyChord.Parse(t);
            Check($"글자 {t} → {c?.ToString() ?? "없음"} → 다시 읽기 같음", c is { } cc && HotkeyChord.Parse(cc.ToString()) == cc);
        }
        Check("순서·대소문자 무관: alt+ctrl+k = Ctrl+Alt+K", HotkeyChord.Parse("alt+ctrl+k") == HotkeyChord.Parse("Ctrl+Alt+K"));
        Check("auto·빈 글자·모르는 키 → 없음", HotkeyChord.Parse("auto") is null && HotkeyChord.Parse("") is null && HotkeyChord.Parse("Ctrl+Alt+Nope") is null);
        Check("화면 글자: Ctrl + Alt + K", HotkeyChord.Parse("Ctrl+Alt+K")!.Value.Display == "Ctrl + Alt + K");
        // 규칙
        HotkeyRules.Verdict V(string t, bool search = false) => HotkeyRules.Check(HotkeyChord.Parse(t)!.Value, search).Verdict;
        var ok = HotkeyRules.Verdict.Ok;
        var warn = HotkeyRules.Verdict.Warn;
        var block = HotkeyRules.Verdict.Block;
        foreach (var (t, want, search) in new (string, HotkeyRules.Verdict, bool)[]
        {
            ("Ctrl+Alt+K", ok, false), ("Win+Alt+J", ok, false), ("Ctrl+Shift+Alt+M", ok, false), ("F13", ok, false), ("Ctrl+F5", ok, false),
            ("Alt+K", warn, false), ("Alt+1", warn, false),
            ("Ctrl+C", block, false), ("Ctrl+V", block, false), ("Ctrl+S", block, false), ("Ctrl+Shift+T", block, false), ("Ctrl+Tab", block, false),
            ("Win+E", block, false), ("Win+D", block, false), ("Win+1", block, false), ("Win+Shift+S", block, false), ("Win+Ctrl+D", block, false),
            ("Win+Alt+R", block, false), ("Alt+Tab", block, false), ("Alt+F4", block, false), ("Ctrl+Shift+Esc", block, false), ("Ctrl+Alt+Delete", block, false),
            ("K", block, false), ("Shift+K", block, false), ("F5", block, false), ("F1", block, false),
            ("Win+Space", block, false), ("Win+Space", ok, true), ("Alt+Space", ok, true), ("Ctrl+Space", ok, true), ("Alt+Space", block, false),
        })
            Check($"규칙 {t}{(search ? " (검색 칸)" : "")}: {V(t, search)} ({want} 기대)", V(t, search) == want);
        // 옛 설정 옮기기 (topBar.spotlightHotkey → hotkeys.search) + 되돌림 값
        var migrate = typeof(SettingsService).GetMethod("Migrate", Any)!;
        HotkeyBinding Migrated(string json, SpotlightHotkey old)
        {
            var s = new Settings();
            s.TopBar.SpotlightHotkey = old;
            migrate.Invoke(null, new object[] { json, s });
            return s.Hotkeys.Search;
        }
        var m1 = Migrated("{\"settingsVersion\":5,\"topBar\":{\"spotlightHotkey\":\"altSpace\"}}", SpotlightHotkey.AltSpace);
        Check($"옮기기: Alt+Space → 검색 켬 {m1.Key}", m1.On && m1.Key == "Alt+Space");
        var m2 = Migrated("{\"settingsVersion\":5,\"topBar\":{\"spotlightHotkey\":\"none\"}}", SpotlightHotkey.None);
        Check($"옮기기: 사용 안 함 → 검색 꺼짐 ({m2.On})", !m2.On);
        var m3 = Migrated("{\"settingsVersion\":5}", SpotlightHotkey.Auto);
        Check($"옮기기: 자동 → 검색 켬 auto ({m3.Key})", m3.On && m3.Key == HotkeyBinding.Auto);
        var kept = new Settings();
        kept.Hotkeys.Search = new HotkeyBinding { On = true, Key = "Ctrl+Alt+S" };
        migrate.Invoke(null, new object[] { "{\"settingsVersion\":5,\"hotkeys\":{}}", kept });
        Check("이미 hotkeys 가 있으면 그대로", kept.Hotkeys.Search.Key == "Ctrl+Alt+S");
        Check("되돌림 값: Win+Space → WinSpace, 직접 키 → Auto, 꺼짐 → None",
            HotkeySettings.ToLegacy(new() { On = true, Key = "Win+Space" }) == SpotlightHotkey.WinSpace
            && HotkeySettings.ToLegacy(new() { On = true, Key = "Ctrl+Alt+S" }) == SpotlightHotkey.Auto
            && HotkeySettings.ToLegacy(new() { On = false, Key = "auto" }) == SpotlightHotkey.None);
        var d = new Settings();
        Check("기본값: 검색만 켬, 나머지 꺼짐·키 없음", d.Hotkeys.Search.On && !d.Hotkeys.AllApps.On && d.Hotkeys.AllApps.Key is null && !d.Hotkeys.ControlCenter.On && !d.Hotkeys.Notifications.On);
        // 키 표 (켜고 키 있는 것만, 루틴 포함)
        var ctl = asm.GetType("Mongdock.Views.HotkeyController")!;
        var st = new Settings();
        st.Hotkeys.AllApps = new HotkeyBinding { On = true, Key = "Ctrl+Alt+A" };
        st.Hotkeys.ControlCenter = new HotkeyBinding { On = false, Key = "Ctrl+Alt+C" };
        st.Hotkeys.Notifications = new HotkeyBinding { On = true, Key = null };
        st.Routines.Add(new RoutineDef { Id = "r1", Name = "업무", Hotkey = new HotkeyBinding { On = true, Key = "Ctrl+Alt+1" } });
        var table = (Dictionary<HotkeyChord, string>)ctl.GetMethod("Table", Any)!.Invoke(null, new object[] { st })!;
        Check($"키 표: {string.Join(", ", table.Select(kv => $"{kv.Key}→{kv.Value}"))}",
            table.Count(kv => !kv.Value.StartsWith("volume:")) == 3 && table[HotkeyChord.Parse("Ctrl+Alt+A")!.Value] == "allApps" // 볼륨 키 3개는 기본으로 따로 (#21) && table[HotkeyChord.Parse("Ctrl+Alt+1")!.Value] == "routine:r1"
            && table.ContainsValue("search") && !table.ContainsValue("controlCenter") && !table.ContainsValue("notifications"));
        // 훅 처리 (가짜 키, 진짜 훅 없음)
        var keyState = typeof(HotkeyService).GetProperty("KeyStateForTest", Any)!;
        var down = new HashSet<int>();
        keyState.SetValue(null, (Func<int, bool>)(vk => down.Contains(vk)));
        try
        {
            using var svc = new HotkeyService();
            var sim = typeof(HotkeyService).GetMethod("SimulateForTest", Any)!;
            bool Sim(int vk, bool isDown) => (bool)sim.Invoke(svc, new object[] { vk, isDown })!;
            var pressed = new List<string>();
            svc.Pressed += a => pressed.Add(a);
            svc.Apply(new Dictionary<HotkeyChord, string> { [HotkeyChord.Parse("Ctrl+Alt+K")!.Value] = "allApps" });
            down.UnionWith(new[] { 0x11, 0x12 }); // Ctrl, Alt
            bool s1 = Sim('K', true), s2 = Sim('K', true), s3 = Sim('K', false);
            PumpFor(200);
            Check($"Ctrl+Alt+K: 삼킴 {s1}, 반복 삼킴 {s2}, 뗌 삼킴 {s3}, 한 번만 실행 ({string.Join(",", pressed)})", s1 && s2 && s3 && pressed.Count == 1 && pressed[0] == "allApps");
            down.Add(0x10); // + Shift
            bool s4 = Sim('K', true);
            Sim('K', false);
            Check($"Ctrl+Alt+Shift+K 는 통과 (삼킴 {s4})", !s4);
            down.Clear();
            bool s5 = Sim('K', true);
            Sim('K', false);
            Check($"K 만 누름은 통과 (삼킴 {s5})", !s5);
            (HotkeyService.RecordKind Kind, HotkeyChord Chord)? got = null;
            svc.Record((k, c) => got = (k, c));
            down.UnionWith(new[] { 0x11, 0x12 });
            bool r1 = Sim('K', true);
            Sim('K', false);
            PumpFor(200);
            Check($"키 받기: Ctrl+Alt+K 받음 {got?.Chord} (삼킴 {r1}), 실행 안 함 ({pressed.Count}번)", r1 && got is { Kind: HotkeyService.RecordKind.Chord } g && g.Chord == HotkeyChord.Parse("Ctrl+Alt+K") && pressed.Count == 1);
            down.Clear();
            got = null;
            svc.Record((k, c) => got = (k, c));
            Sim(0x1B, true);
            Sim(0x1B, false);
            PumpFor(200);
            Check($"키 받기: Esc = 취소 ({got?.Kind})", got?.Kind == HotkeyService.RecordKind.Cancel);
            got = null;
            svc.Record((k, c) => got = (k, c));
            Sim(0x08, true);
            Sim(0x08, false);
            PumpFor(200);
            Check($"키 받기: Backspace = 지우기 ({got?.Kind})", got?.Kind == HotkeyService.RecordKind.Clear);
        }
        finally { keyState.SetValue(null, null); }
        Console.WriteLine(bad == 0 ? "단축키 점검: 모두 통과" : $"단축키 점검: 실패 {bad}개");
        return bad == 0 ? 0 : 1;
    }

    private static void PumpFor(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static int ReviewCheck()
    {
        var asm = typeof(Mongdock.App).Assembly;
        int bad = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  {(ok ? "통과" : "실패")}  {what}"); if (!ok) bad++; }
        var unused = asm.GetType("Mongdock.Services.UnusedApps")!;
        bool In(string exe, string loc) => (bool)unused.GetMethod("InLocation", Any)!.Invoke(null, new object[] { exe, loc })!;
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Check("Program Files 자체는 연결 안 함", !In($@"{pf}\Tool\tool.exe", pf));
        Check("Common Files 자체는 연결 안 함", !In($@"{common}\Vendor\x.exe", common));
        Check("AppData/Local 자체는 연결 안 함", !In($@"{local}\App\app.exe", local));
        Check("드라이브 루트는 연결 안 함", !In(@"C:\Tools\a.exe", @"C:\"));
        Check("회사 폴더(Program Files/회사): 다른 제품 하위 폴더 앱은 연결 안 함", !In($@"{pf}\Vendor\ProductB\b.exe", $@"{pf}\Vendor"));
        Check("Program Files/앱: 바로 안의 exe 는 연결", In($@"{pf}\Notepad++\notepad++.exe", $@"{pf}\Notepad++"));
        Check("깊은 앱 폴더: 하위 exe 도 연결", In($@"{pf}\Vendor\Product\bin\p.exe", $@"{pf}\Vendor\Product"));
        Check("다른 폴더는 연결 안 함", !In(@"D:\Apps\x.exe", $@"{pf}\Vendor\Product"));
        var panel = asm.GetType("Mongdock.Views.AllAppsPanel")!;
        bool Same(string u, params string[] names) => (bool)panel.GetMethod("SameProduct", Any)!.Invoke(null, new object[] { u, names })!;
        Check("이름 비교: 같은 제품", Same("Notepad++ (64-bit x64)", "Notepad++"));
        Check("이름 비교: 다른 제품이면 주의", !Same("Contoso Suite 2024", "작은 도구 A"));
        var un = asm.GetType("Mongdock.Services.AppUninstall")!;
        object? Split(string c) => un.GetMethod("Split", Any)!.Invoke(null, new object[] { c });
        Check("따옴표 없고 exe 못 찾음 → 첫 공백에서 자르지 않음", Split(@"C:\Program Files\Nope Tool\gone uninstall /S") is null);
        Check("따옴표 경로는 그대로", Split("\"C:\\X Y\\u.exe\" /S") is not null);
        Check("msiexec 는 그대로", Split("MsiExec.exe /I{12345678-1234-1234-1234-123456789012}") is not null);
        var links = asm.GetType("Mongdock.Services.RoutineLinks")!;
        bool SteamOk(string u) => (bool)links.GetMethod("SteamOk", Any)!.Invoke(null, new object[] { new Uri(u) })!;
        Check("스팀: rungameid 허용", SteamOk("steam://rungameid/570"));
        Check("스팀: uninstall 막음", !SteamOk("steam://uninstall/570"));
        Check("스팀: install 막음", !SteamOk("steam://install/570"));
        var rs = asm.GetType("Mongdock.Services.RoutineService")!;
        bool Quick(string pth) => (bool)rs.GetMethod("QuickExists", Any)!.Invoke(null, new object[] { pth, true })!;
        Check("UNC 경로는 확인 안 함(없다고 봄)", !Quick(@"\\server\share\file.txt"));
        Check("로컬 경로는 확인", Quick(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Console.WriteLine(bad == 0 ? "검토 수정 점검: 모두 통과" : $"검토 수정 점검: {bad}개 실패");
        return bad == 0 ? 0 : 1;
    }

    private static int RenderTour(string? dir, bool checkOnly)
    {
        if (dir is not null) Directory.CreateDirectory(dir);
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var asm = typeof(Mongdock.App).Assembly;
        var any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        var scenes = asm.GetType("Mongdock.Views.TourScenes")!;
        var cardType = asm.GetType("Mongdock.Views.TourCard")!;
        var stepType = asm.GetType("Mongdock.Views.TourStep")!;
        var coach = asm.GetType("Mongdock.Views.CoachMarks")!;
        int problems = 0;
        void Save(BitmapSource bmp, string name)
        {
            if (dir is null) return;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, name));
            enc.Save(fs);
        }
        BitmapSource Snap(FrameworkElement el, double scale = 1)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = el.DesiredSize;
            el.Arrange(new Rect(size));
            el.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(el);
            rtb.Freeze();
            return rtb;
        }
        foreach (var theme in new[] { "light", "dark" })
        {
            var settings = new SettingsService();
            settings.Current.Dock.Theme = theme == "dark" ? DockTheme.Dark : DockTheme.Light;
            settings.Current.HideWindowsTaskbar = true;
            Mongdock.ViewModels.UiFonts.Apply(settings.Current);
            Mongdock.ViewModels.UiTheme.Apply(settings.Current);
            var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
            var tracker = new WindowTracker();
            var launcher = new AppLauncher(tracker);
            var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
                new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
                new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
            // 점검: 모든 장
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var list = (List<string>)scenes.GetMethod("Check", any)!.Invoke(null, new object?[] { services, palette, null })!;
            Console.WriteLine($"  둘러보기 장면 점검 ({_lang}, {theme}): {(list.Count == 0 ? "통과" : $"문제 {list.Count}개")} ({sw.ElapsedMilliseconds}ms)");
            foreach (var x in list) Console.WriteLine($"    - {x}");
            problems += list.Count;
            problems += CardFit(services, palette, theme);
            if (checkOnly) { settings.Dispose(); continue; }

            // 장 저장
            ImageSource? Frame(string key, int i) => (ImageSource?)scenes.GetMethod("Frame", any)!.Invoke(null, new object[] { services, key, i, palette });
            int Count(string? key) => (int)scenes.GetMethod("Count", any)!.Invoke(null, new object?[] { key })!;
            // 카드 미리보기 (장면 장마다)
            coach.GetMethod("Init", any)!.Invoke(null, new object?[] { services, (Func<Mongdock.Services.CoachAnchor, (Rect, MonitorInfo)?>)(_ => null), null });
            List<object> Pages(string flow) => flow == "tour"
                ? ((System.Collections.IEnumerable)coach.GetMethod("BuildTourPages", any)!.Invoke(null, null)!).Cast<object>().ToList()
                : ((System.Collections.IEnumerable)coach.GetMethod("BuildWhatsNew", any)!.Invoke(null, (flow == "catchup" ? new object[] { new Version(0, 2, 0), Mongdock.Services.WhatsNew.Current } : flow == "update08" ? new object[] { new Version(0, 6, 5), new Version(0, 8, 0) } : flow == "update062" ? new object[] { new Version(0, 6, 1), new Version(0, 6, 2) } : flow == "update061" ? new object[] { new Version(0, 6, 0), new Version(0, 6, 1) } : flow == "update06" ? new object[] { new Version(0, 5, 0), new Version(0, 6, 0) } : new object[] { new Version(0, 4, 2), new Version(0, 5, 0) }))!).Cast<object>().ToList();
            foreach (var flow in new[] { "tour", "update", "update06", "update061", "update062", "update08", "catchup" })
            {
                var pages = Pages(flow);
                var rows = new List<BitmapSource>();
                for (int pi = 0; pi < pages.Count; pi++)
                {
                    var pg = pages[pi];
                    T? P<T>(string n) => (T?)pg.GetType().GetProperty(n)!.GetValue(pg);
                    string? key = P<string>("Pictures");
                    var frames = new List<ImageSource>();
                    for (int i = 0; i < Count(key); i++) if (Frame(key!, i) is { } img) { frames.Add(img); if (img is BitmapSource b) Save(b, $"{flow}-{pi + 1}-{key}-{i + 1}-{theme}.png"); }
                    var step = Activator.CreateInstance(stepType, P<string>("Title"), P<string>("Body"), frames)!;
                    if (P<object>("Groups") is { } groups) stepType.GetProperty("Groups")!.SetValue(step, groups);
                    stepType.GetProperty("More")!.SetValue(step, P<int>("More"));
                    if (P<bool>("ChangelogLink")) stepType.GetProperty("Link")!.SetValue(step, P<string>("LinkText"));
                    if (P<object>("Action") is { } act) stepType.GetProperty("Action")!.SetValue(step, act);
                    stepType.GetProperty("ShowLogo")!.SetValue(step, P<bool>("ShowLogo"));
                    if (P<object>("Inline") is { } inl) stepType.GetProperty("InlineText")!.SetValue(step, (string)inl.GetType().GetProperty("Label")!.GetValue(inl)!);
                    var card = (FrameworkElement)Activator.CreateInstance(cardType, any, null, new object[] { palette }, null)!;
                    cardType.GetMethod("Show", any)!.Invoke(card, new object[] { step, pi, pages.Count });
                    var host = new System.Windows.Controls.Border { Padding = new Thickness(24), Background = new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x24, 0x24, 0x2C) : Color.FromRgb(0xE4, 0xE6, 0xEE)), Child = card };
                    var cardShot = Snap(host);
                    // 한 줄: 카드 + 장들 (작게)
                    var line = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = Brushes.White };
                    line.Children.Add(new System.Windows.Controls.Image { Source = cardShot, Width = cardShot.PixelWidth * 0.8, Height = cardShot.PixelHeight * 0.8 });
                    foreach (var fimg in frames) line.Children.Add(new System.Windows.Controls.Border { Margin = new Thickness(14, 24, 0, 0), VerticalAlignment = VerticalAlignment.Top, Child = new System.Windows.Controls.Image { Source = fimg, Width = 360, Height = 225 } });
                    rows.Add(Snap(line));
                }
                // 시트
                var sheet = new System.Windows.Controls.StackPanel { Background = Brushes.White };
                sheet.Children.Add(new System.Windows.Controls.TextBlock { Text = (flow == "tour" ? "첫 설치 둘러보기" : flow == "update06" ? "0.5.x → 0.6.0 새 기능" : flow == "update061" ? "0.6.0 → 0.6.1 새 기능" : flow == "update08" ? "0.6.5 → 0.8.0 새 기능" : flow == "update062" ? "0.6.1 → 0.6.2 새 기능" : flow == "catchup" ? "0.2.0 → 지금 (오래된 버전)" : "0.4.x → 0.5.0 새 기능") + $" · {_lang} · {theme}", FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(16, 12, 0, 8) });
                foreach (var r in rows) sheet.Children.Add(new System.Windows.Controls.Image { Source = r, Width = r.PixelWidth, Height = r.PixelHeight, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) });
                Save(Snap(sheet, 0.6), $"sheet-{flow}-{_lang}-{theme}.png");
                Console.WriteLine($"  저장  sheet-{flow}-{_lang}-{theme}.png ({pages.Count}장)");
            }
            // 버튼을 누른 뒤 모습: 둘러보기 카드 결과 줄 + [되돌리기], 질문 카드 결과 (창은 띄우지 않고 내용만 그림)
            {
                var step = Activator.CreateInstance(stepType, Mongdock.Loc.T("독의 폴더와 휴지통"), "", new List<ImageSource>())!;
                stepType.GetProperty("ResultText")!.SetValue(step, Mongdock.Loc.T("독 끝에 넣었어요 — 눌러서 펼쳐 보세요"));
                stepType.GetProperty("CanUndo")!.SetValue(step, true);
                stepType.GetProperty("FramesPending")!.SetValue(step, true);
                var card = (FrameworkElement)Activator.CreateInstance(cardType, any, null, new object[] { palette }, null)!;
                cardType.GetMethod("Show", any)!.Invoke(card, new object[] { step, 2, 5 });
                Save(Snap(new System.Windows.Controls.Border { Padding = new Thickness(24), Background = new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x24, 0x24, 0x2C) : Color.FromRgb(0xE4, 0xE6, 0xEE)), Child = card }), $"result-tourcard-{_lang}-{theme}.png");
                var confirm = asm.GetType("Mongdock.Views.ConfirmCardWindow")!;
                foreach (var (name, title, msg) in new[]
                {
                    ("startup", Mongdock.Loc.T("자동 실행을 켰어요"), Mongdock.Loc.T("컴퓨터를 켜면 몽독도 켜져요. 설정 → 일반에서도 바꿀 수 있어요.")),
                    ("taskbar", Mongdock.Loc.T("작업 표시줄을 숨겼어요"), Mongdock.Loc.T("설정 → 일반 → '윈도우 작업 표시줄 숨기기'에서도 바꿀 수 있어요.")),
                })
                {
                    var w = (Window)confirm.GetConstructors(any)[0].Invoke(new object?[] { services, palette, title, msg, Mongdock.Loc.T("확인"), (Action)(() => { }), Mongdock.Loc.T("되돌리기"), null, null, true });
                    var content = (FrameworkElement)w.Content;
                    w.Content = null;
                    Save(Snap(new System.Windows.Controls.Border { Background = new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x24, 0x24, 0x2C) : Color.FromRgb(0xE4, 0xE6, 0xEE)), Child = content }), $"result-{name}-{_lang}-{theme}.png");
                    w.Close();
                }
            }
            settings.Dispose();
        }
        GC.KeepAlive(app);
        return problems == 0 ? 0 : 1;
    }

    private static void RenderAllAppsPanel(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var asm = typeof(Mongdock.App).Assembly;
        var catalog = asm.GetType("Mongdock.Services.AllAppsCatalog")!;
        var apps = (System.Collections.ICollection)catalog.GetMethod("Apps")!.Invoke(null, new object[] { false })!;
        Console.WriteLine($"  앱 {apps.Count}개");
        var panelType = asm.GetType("Mongdock.Views.AllAppsPanel")!;
        object appsForFolders() => catalog.GetMethod("Apps")!.Invoke(null, new object[] { false })!;
        panelType.GetProperty("LoadIconsNow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, true);
        foreach (var theme in new[] { "light", "dark" })
        {
            var settings = new SettingsService(); // 읽기만 (저장하지 않음 — 이 셸의 %APPDATA% 는 가상화된 사본)
            settings.Current.Dock.Theme = theme == "dark" ? Mongdock.Models.DockTheme.Dark : Mongdock.Models.DockTheme.Light;
            Mongdock.ViewModels.UiFonts.Apply(settings.Current);
            var tracker = new WindowTracker();
            var launcher = new AppLauncher(tracker);
            var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
                new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
                new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
            var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
            var monitor = Monitors.GetPrimary();
            // 화면 크기별 (작업 영역 비율): 1920x1080 · 2560x1440 · 세로 1080x1920 — 높이 제한 그대로
            var rectType = typeof(Mongdock.App).Assembly.GetType("Mongdock.Native.RECT")!;
            var monCtor = typeof(MonitorInfo).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
            var sizes = theme == "light" ? new[] { (1920, 1080), (2560, 1440), (1080, 1920) } : Array.Empty<(int, int)>();
            foreach (var (sw, sh) in sizes)
            {
                object bounds = Activator.CreateInstance(rectType, 0, 0, sw, sh)!;
                object work = Activator.CreateInstance(rectType, 0, 32, sw, sh - 48)!;
                var fake = (MonitorInfo)monCtor.Invoke(new object[] { @"\.\DISPLAY9", bounds, work, 1.0, true, 9 });
                var w = (Window)panelType.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)[0]
                    .Invoke(new object[] { services, palette, new Rect(800, 1000, 52, 52), Mongdock.Models.DockEdge.Bottom, fake });
                panelType.GetMethod("Rebuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
                var content = (FrameworkElement)w.Content;
                content.Opacity = 1;
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = content.DesiredSize;
                content.Arrange(new Rect(size));
                content.UpdateLayout();
                // 화면 크기 판 위에 판을 가운데 놓은 그림 (화면은 1/2 로 줄여서)
                const double k = 0.5;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB8)), null, new Rect(0, 0, sw * k, sh * k));
                    dc.PushTransform(new ScaleTransform(k, k));
                    dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), Stretch = Stretch.None }, null,
                        new Rect((sw - size.Width) / 2, 32 + (sh - 48 - size.Height) / 2, size.Width, size.Height));
                    dc.Pop();
                }
                var rtb = new RenderTargetBitmap((int)(sw * k), (int)(sh * k), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                string spath = Path.Combine(dir, $"allapps-screen-{sw}x{sh}.png");
                var senc = new PngBitmapEncoder();
                senc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(spath)) senc.Save(fs);
                Console.WriteLine($"  저장  {spath} (판 {size.Width:0}x{size.Height:0})");
                w.Close();
            }
            // 즐겨찾기 재기획 그림: 0개 카드 / 즐겨찾기 + 추천 칸(첫 칸 마우스 올림) — 이 PC 앱에서 이름으로 골라 가짜 추천
            var appList = ((System.Collections.IEnumerable)catalog.GetMethod("Apps")!.Invoke(null, new object[] { false })!).Cast<object>().ToList();
            var entryType = asm.GetType("Mongdock.Services.AppEntry")!;
            string NameOf(object a) => (string)entryType.GetProperty("Name")!.GetValue(a)!;
            string KeyOf(object a) => (string)entryType.GetProperty("Key")!.GetValue(a)!;
            string IdOf(object a) => (string)catalog.GetMethod("Identity", new[] { entryType })!.Invoke(null, new[] { a })!;
            object? Pick(string n) => appList.FirstOrDefault(a => NameOf(a).Contains(n, StringComparison.OrdinalIgnoreCase));
            var suggestIds = new[] { "Visual Studio Code", "Excel", "Steam", "메모장", "Discord", "Spotify", "계산기", "Word" }
                .Select(Pick).OfType<object>().Select(IdOf).ToList();
            panelType.GetProperty("SuggestionsOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null, (Func<List<string>>)(() => suggestIds));
            var savedFavs = settings.Current.AllApps.Favorites.ToList();
            var savedPins = settings.Current.Pins.ToList();
            settings.Current.Pins.RemoveAll(pin => pin.Kind is Mongdock.Models.PinKind.Exe or Mongdock.Models.PinKind.Aumid); // 독 핀은 추천에서 빠지므로 그림에선 비움
            settings.Current.AllApps.ShowSuggestions = true;
            settings.Current.AllApps.DismissedSuggestions.Clear();
            foreach (var mode in new[] { "favcard", "favsuggest" })
            {
                settings.Current.AllApps.Favorites.Clear();
                if (mode == "favsuggest")
                    foreach (var n in new[] { "카카오톡", "Chrome", "Notion" }) if (Pick(n) is { } a) settings.Current.AllApps.Favorites.Add(KeyOf(a));
                var w = (Window)panelType.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)[0]
                    .Invoke(new object[] { services, palette, new Rect(800, 1000, 52, 52), Mongdock.Models.DockEdge.Bottom, monitor });
                panelType.GetProperty("ShowSuggestionHoverForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, mode == "favsuggest");
                panelType.GetMethod("Rebuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
                var content = (FrameworkElement)w.Content;
                content.Opacity = 1; // 열릴 때 0.5→1 페이드 시작값
                foreach (var sv in Descendants(content).OfType<System.Windows.Controls.ScrollViewer>()) { sv.MaxHeight = double.PositiveInfinity; sv.Height = double.NaN; }
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = new Size(content.DesiredSize.Width, Math.Min(content.DesiredSize.Height, 420)); // 위쪽(즐겨찾기·묶음 첫 줄)만
                content.Arrange(new Rect(content.DesiredSize));
                content.UpdateLayout();
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x14, 0x16, 0x1C) : Color.FromRgb(0xE9, 0xEC, 0xF4)), null, new Rect(size));
                    dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), Stretch = Stretch.None }, null, new Rect(size));
                }
                var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                string fpath = Path.Combine(dir, $"allapps-{theme}-{mode}.png");
                var fenc = new PngBitmapEncoder();
                fenc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(fpath)) fenc.Save(fs);
                Console.WriteLine($"  저장  {fpath}");
                w.Close();
            }
            settings.Current.AllApps.Favorites.Clear();
            settings.Current.AllApps.Favorites.AddRange(savedFavs);
            settings.Current.Pins.Clear();
            settings.Current.Pins.AddRange(savedPins);
            panelType.GetProperty("SuggestionsOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);

            // 쓰는 앱만 담는 폴더·정리는 실행 기록이 있어야 — 이 셸의(가상화된) 데이터에 윈도우 실행 기록 씨앗을 넣고 자동 폴더 계산
            asm.GetType("Mongdock.Services.AppUsage")!.GetMethod("SeedFromUserAssist")!.Invoke(null, new object?[] { null });
            settings.Current.AllApps.UsageSeededAt = DateTime.Now.AddDays(-60);
            settings.Current.AllApps.CleanupPromptMonth = null;
            asm.GetType("Mongdock.Services.AppFolders")!.GetMethod("RefreshAuto")!.Invoke(null, new object[] { settings.Current, appsForFolders(), true });
            var modes = _typesOnly ? new[] { "type-A", "type-B", "type-C" } : new[] { "home", "group", "feedback", "search", "edit", "cleanup", "unused", "remove-confirm", "remove-running", "size-old", "size-now", "size-new" };
            if (_typesOnly && theme == "dark") continue;
            foreach (var mode in modes)
            {
                // 글자 배율 안 비교 (2026-10-13 사용자 고르기): A 아이콘과 같이 / B 설정 창 크기 / C 절반만 — 앱·폴더 이름은 모두 아이콘과 같이
                if (mode.StartsWith("type-")) panelType.GetProperty("TextMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, mode[5..]);
                // 크기 비교 (2026-10-13 사용자 "커진 건지 모르겠다"): 1536x838 DIP 화면에서 예전 60%·75% / 72%·82% / 새 80%·88%
                panelType.GetProperty("SizeForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, mode switch
                {
                    "size-old" => (922.0, 628.0),
                    "size-now" => (1106.0, 687.0),
                    "size-new" or "type-A" or "type-B" or "type-C" => (1536 * 0.80, 838 * 0.88),
                    _ => ((double, double)?)null,
                });
                // 처음 화면엔 정리 띠가 보이게 (이 셸 기록엔 두 달 넘은 앱이 없어 기준을 줄임)
                panelType.GetProperty("StaleDaysForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, mode == "home" ? 3 : null);
                var w = (Window)panelType.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)[0]
                    .Invoke(new object[] { services, palette, new Rect(800, 1000, 52, 52), Mongdock.Models.DockEdge.Bottom, monitor });
                if (mode is "group" or "feedback") panelType.GetField("_expanded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, "tools");
                // 올림·누름 그림: 추천 칸 첫째 올림·둘째 누름 (연한 판 위), 폴더 칸 하나 올림, 펼친 폴더 앱 하나 누름
                panelType.GetProperty("FeedbackForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, mode == "feedback" ? "hover,press,-,-,hover,-,-,-,-,-,-,press" : null);
                if (mode == "edit")
                {
                    panelType.GetField("_editing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, true);
                    panelType.GetField("_expanded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, "dev");
                }
                if (mode == "cleanup")
                {
                    panelType.GetProperty("StaleDaysForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, 3);
                    panelType.GetMethod("OpenCleanup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
                    panelType.GetProperty("StaleDaysForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
                }
                panelType.GetProperty("RemoveForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, mode.StartsWith("remove-") ? mode : null);
                if (mode == "unused" || mode.StartsWith("remove-")) panelType.GetMethod("ShowUnusedForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
                if (mode.StartsWith("remove-"))
                {
                    // 잠자는 앱 목록을 한 번 그려 센 다음, 지우기 확인 카드 / 진행 화면으로
                    panelType.GetMethod("Rebuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
                    panelType.GetMethod("ShowRemoveForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, new object[] { mode == "remove-running" });
                }
                if (mode == "search") ((System.Windows.Controls.TextBox)panelType.GetField("_search", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!).Text = "ch";
                panelType.GetMethod("Rebuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
                var content = (FrameworkElement)w.Content;
                content.Opacity = 1;
                // 그림에는 판 전체가 보이게 (스크롤 높이 제한 풀기 — 펼친 폴더 그림은 본문 높이도 풀어 [+] 칸까지)
                foreach (var sv in Descendants(content).OfType<System.Windows.Controls.ScrollViewer>()) { sv.MaxHeight = double.PositiveInfinity; if (mode is "group" or "unused" or "feedback") sv.Height = double.NaN; }
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = content.DesiredSize;
                content.Arrange(new Rect(size));
                content.UpdateLayout();
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x14, 0x16, 0x1C) : Color.FromRgb(0xE9, 0xEC, 0xF4)), null, new Rect(size));
                    dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), Stretch = Stretch.None }, null, new Rect(size));
                }
                var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                string path = Path.Combine(dir, $"allapps-{theme}-{mode}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                Console.WriteLine($"  저장  {path}");
                if (mode == "unused") // 선택 막대 그림: 판 아래쪽만 (막대·목록 끝)
                {
                    int ch = Math.Min(rtb.PixelHeight, 520);
                    var crop = new CroppedBitmap(rtb, new Int32Rect(0, rtb.PixelHeight - ch, rtb.PixelWidth, ch));
                    var cenc = new PngBitmapEncoder();
                    cenc.Frames.Add(BitmapFrame.Create(crop));
                    string cpath = Path.Combine(dir, $"allapps-{theme}-unused-bar.png");
                    using (var fs = File.Create(cpath)) cenc.Save(fs);
                    Console.WriteLine($"  저장  {cpath}");
                }
                w.Close();
            }
            panelType.GetProperty("SizeForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
            settings.Dispose();
        }
        GC.KeepAlive(app);
    }

    // ───────────────────────── 상단바 상태 판 (제어 센터·소리·달력) — 톤·대비 확인 ─────────────────────────

    private static void RenderStatusPanels(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var asm = typeof(Mongdock.App).Assembly;
        var kindType = asm.GetType("Mongdock.Views.StatusPanelKind")!;
        var panelType = asm.GetType("Mongdock.Views.StatusPanelWindow")!;
        foreach (var theme in new[] { "light", "dark" })
        {
            var settings = new SettingsService();
            settings.Current.Dock.Theme = theme == "dark" ? DockTheme.Dark : DockTheme.Light;
            Mongdock.ViewModels.UiTheme.Apply(settings.Current);
            Mongdock.ViewModels.UiFonts.Apply(settings.Current);
            var tracker = new WindowTracker();
            var launcher = new AppLauncher(tracker);
            var status = new StatusService();
            var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
                new ShellActions(), new ImeService(), status, new MediaService(), new AppMenuService(settings), new StartupService(),
                new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
            var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
            foreach (var kind in new[] { "ControlCenter", "Volume", "Calendar" })
            {
                try
                {
                    var w = (Window)panelType.GetConstructors(Any)[0].Invoke(new object[] { services, Enum.Parse(kindType, kind), palette, 0 });
                    var content = (FrameworkElement)w.Content;
                    content.Opacity = 1;
                    content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var size = content.DesiredSize;
                    content.Arrange(new Rect(size));
                    content.UpdateLayout();
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x14, 0x16, 0x1C) : Color.FromRgb(0xE9, 0xEC, 0xF4)), null, new Rect(size));
                        dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), Stretch = Stretch.None }, null, new Rect(size));
                    }
                    var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    string path = Path.Combine(dir, $"panel-{theme}-{kind.ToLowerInvariant()}.png");
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(path)) enc.Save(fs);
                    Console.WriteLine($"  저장  {path}");
                    w.Close();
                }
                catch (Exception ex) { Console.WriteLine($"  {kind} 실패: {ex.GetBaseException().Message}"); }
            }
            settings.Dispose();
        }
        GC.KeepAlive(app);
    }

    // ───────────────────────── 앱 모음 판 나타나기 (A 가운데 / B 아이콘에서) 시연 프레임 ─────────────────────────

    /// <summary>--intro DIR: 1920x1080 화면(1/2)에 독과 앱 모음 아이콘, 판이 나타나는 0·30·60·100% 프레임을 가로로 (A center, B icon).</summary>
    private static void RenderIntroFrames(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var asm = typeof(Mongdock.App).Assembly;
        asm.GetType("Mongdock.Services.AllAppsCatalog")!.GetMethod("Apps")!.Invoke(null, new object[] { false });
        var settings = new SettingsService();
        settings.Current.Dock.Theme = DockTheme.Light;
        Mongdock.ViewModels.UiFonts.Apply(settings.Current);
        var tracker = new WindowTracker();
        var launcher = new AppLauncher(tracker);
        var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
            new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
            new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
        var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
        var panelType = asm.GetType("Mongdock.Views.AllAppsPanel")!;
        panelType.GetProperty("LoadIconsNow", Any)!.SetValue(null, true);
        const int sw = 1920, sh = 1080;
        var rectType = asm.GetType("Mongdock.Native.RECT")!;
        var monCtor = typeof(MonitorInfo).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        var fake = (MonitorInfo)monCtor.Invoke(new object[] { @"\.\DISPLAY9", Activator.CreateInstance(rectType, 0, 0, sw, sh)!, Activator.CreateInstance(rectType, 0, 0, sw, sh - 80)!, 1.0, true, 9 });
        var icon = new Rect(sw / 2 - 26 - 3 * 62, sh - 70, 52, 52); // 독 가운데 줄의 앱 모음 아이콘
        var w = (Window)panelType.GetConstructors(Any)[0].Invoke(new object[] { services, palette, icon, DockEdge.Bottom, fake });
        panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
        var content = (FrameworkElement)w.Content;
        foreach (var sv in Descendants(content).OfType<System.Windows.Controls.ScrollViewer>()) sv.MaxHeight = sh * 0.75 - 120;
        content.Opacity = 1;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        var picture = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var pv = new DrawingVisual();
        using (var dc = pv.RenderOpen())
        {
            dc.DrawRectangle(palette.CardBackground, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
        }
        picture.Render(pv);
        var final = new Rect((sw - size.Width) / 2, (sh - 80 - size.Height) / 2, size.Width, size.Height);
        var intro = asm.GetType("Mongdock.Views.PanelIntro")!;
        var sample = intro.GetMethod("Sample", Any)!;
        var appsIcon = new IconService().GetIcon(new PinItem { Kind = PinKind.Special, Target = "launchpad" }, IconStyle.Mac);
        const double k = 0.32;
        var ts = new[] { 0.0, 0.3, 0.6, 1.0 };
        foreach (var mode in new[] { "center", "icon" })
        {
            double fw = sw * k, fh = sh * k;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                for (int i = 0; i < ts.Length; i++)
                {
                    dc.PushTransform(new TranslateTransform(i * (fw + 16), 0));
                    dc.PushClip(new RectangleGeometry(new Rect(0, 0, fw, fh)));
                    dc.PushTransform(new ScaleTransform(k, k));
                    dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x9A, 0xA9, 0xC4), Color.FromRgb(0xB9, 0xB2, 0xD6), 90), null, new Rect(0, 0, sw, sh));
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), null, new Rect(sw / 2 - 360, sh - 80, 720, 72), 20, 20);
                    for (int j = -5; j <= 5; j++)
                    {
                        var r = new Rect(sw / 2 - 26 + j * 62, sh - 70, 52, 52);
                        if (j == -3) dc.DrawImage(appsIcon, r);
                        else dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x55, 0x60, 0x70, 0xA0)), null, r, 12, 12);
                    }
                    var res = sample.Invoke(null, new object[] { mode, ts[i], final, icon })!;
                    var rect = (Rect)res.GetType().GetField("Item1")!.GetValue(res)!;
                    double op = (double)res.GetType().GetField("Item2")!.GetValue(res)!;
                    dc.PushOpacity(op);
                    double rad = 8 * rect.Width / final.Width;
                    dc.DrawRoundedRectangle(new ImageBrush(picture), new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1), rect, rad, rad);
                    dc.Pop();
                    dc.Pop();
                    dc.Pop();
                    dc.Pop();
                    var label = new FormattedText($"{(int)(ts[i] * (mode == "icon" ? 200 : 160))}ms", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 13, Brushes.Black, 1.0);
                    dc.DrawText(label, new Point(i * (fw + 16) + 6, fh + 4));
                }
            }
            var rtb = new RenderTargetBitmap((int)(ts.Length * (fw + 16)), (int)fh + 24, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            string path = Path.Combine(dir, $"intro-{(mode == "center" ? "A-center" : "B-icon")}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
            Console.WriteLine($"  저장  {path}");
        }
        w.Close();
        settings.Dispose();
        GC.KeepAlive(app);
    }

    // ───────────────────────── 루틴 (#24-A) 화면 그림 ─────────────────────────

    private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;

    /// <summary>
    /// --routines DIR: 지금 화면 저장 카드, 편집 창(크게 / 세부 펼침 + 고급), 앱 모음 판 루틴 줄·0개 설명 카드, 독 루틴 아이콘(자동 2x2·기호, 툴팁·점),
    /// 끝내기 확인 카드("데스크톱도 닫기"), 다른 데스크톱 안내 카드 — 라이트/다크. 가짜 항목은 이 PC 에 있는 exe 로.
    /// </summary>
    private static void RenderRoutines(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new Mongdock.App();
        Mongdock.Loc.Init(_lang);
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var asm = typeof(Mongdock.App).Assembly;
        Type T(string name) => asm.GetType(name)!;
        string Exe(params string[] candidates) => candidates.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists) ?? @"C:\Windows\System32\notepad.exe";
        string chrome = Exe(@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
        string code = Exe(@"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe", @"C:\Program Files\Microsoft VS Code\Code.exe");
        string edge = Exe(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
        string notepad = Exe(@"C:\Windows\System32\notepad.exe");
        RoutineItem App(string exe, string name, RoutinePlacementMode place, int monitor = 1, string? open = null) => new()
        {
            Kind = RoutineItemKind.App, Target = exe, Name = name, Open = open,
            Monitor = new RoutineMonitor { Mode = RoutineMonitorMode.Index, Index = monitor },
            Placement = new RoutinePlacement { Mode = place },
        };
        List<RoutineItem> Work() => new()
        {
            App(edge, "Outlook (웹)", RoutinePlacementMode.Max, 1, "https://outlook.office.com"),
            App(chrome, "Chrome", RoutinePlacementMode.Left, 2, "https://mail.company.com"),
            App(code, "VS Code", RoutinePlacementMode.Right, 1, @"C:\dev\mongdock"),
            new() { Kind = RoutineItemKind.Path, Target = @"C:\Windows", Name = "Windows", Placement = new RoutinePlacement { Mode = RoutinePlacementMode.Saved, Rect = new[] { 0.55, 0.3, 0.35, 0.5 } } },
        };

        foreach (var theme in new[] { "light", "dark" })
        {
            var settings = new SettingsService(); // 읽기만 (저장하지 않음)
            settings.Current.Dock.Theme = theme == "dark" ? DockTheme.Dark : DockTheme.Light;
            Mongdock.ViewModels.UiFonts.Apply(settings.Current);
            var tracker = new WindowTracker();
            var launcher = new AppLauncher(tracker);
            var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
                new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
                new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
            var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
            T("Mongdock.Services.RoutineService").GetMethod("Init", Any)!.Invoke(null, new object[] { services });
            var bg = theme == "dark" ? Color.FromRgb(0x14, 0x16, 0x1C) : Color.FromRgb(0xE9, 0xEC, 0xF4);
            void Save(FrameworkElement content, string name, double maxHeight = double.PositiveInfinity)
            {
                content.Opacity = 1;
                foreach (var sv in Descendants(content).OfType<System.Windows.Controls.ScrollViewer>()) sv.MaxHeight = double.PositiveInfinity;
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var full = content.DesiredSize;
                content.Arrange(new Rect(full));
                content.UpdateLayout();
                var size = new Size(full.Width, Math.Min(full.Height, maxHeight));
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(size));
                    dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), Stretch = Stretch.None }, null, new Rect(size));
                }
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                string path = Path.Combine(dir, $"routine-{theme}-{name}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                Console.WriteLine($"  저장  {path}");
            }
            Window New(string type, params object?[] args) => (Window)T(type).GetConstructors(Any)[0].Invoke(args);

            // 1) 지금 화면 저장 카드 (가짜로 읽은 창 4개, 카카오톡 줄은 체크 풀림)
            var captured = Work();
            captured.Insert(3, App(Exe(@"C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe", notepad), "카카오톡", RoutinePlacementMode.Saved, 2));
            var save = New("Mongdock.Views.RoutineSaveWindow", services, false, captured);
            var rows = (System.Collections.IList)save.GetType().GetField("_rows", Any)!.GetValue(save)!;
            var last = rows[3]!;
            ((System.Windows.Controls.CheckBox)last.GetType().GetField("Item2")!.GetValue(last)!).IsChecked = false;
            Save((FrameworkElement)save.Content, "save");
            save.Close();

            // 1-b) 지금 화면으로 맞추기 카드: 그대로 2 · 새 앱 1 · 없음 1 · 웹·파일 1
            {
                var items = Work();
                var planType = T("Mongdock.Services.RoutineService+SyncPlan");
                var kept = new List<(RoutineItem, IntPtr)> { (items[0], IntPtr.Zero), (items[1], IntPtr.Zero) };
                var added = new List<(RoutineItem, IntPtr)> { (captured[3], IntPtr.Zero) };
                var missing = new List<RoutineItem> { items[2] };
                var plan = Activator.CreateInstance(planType, kept, missing, added, 1)!;
                var syncType = T("Mongdock.Views.RoutineSyncWindow");
                var sync = (Window)Activator.CreateInstance(syncType, Any, null, new object[] { services, plan, items.Count, new Action<List<RoutineItem>, List<RoutineItem>>((_, _) => { }) }, null)!;
                Save((FrameworkElement)sync.Content, "sync");
                sync.Close();
            }

            // 1-c) 긴 알림 펼침 (#25 §5): 제목만 긴 것 / 본문이 긴 것 — 펼칠 것 판정 + 접힘·펼침(제목 여러 줄) 그림
            {
                var ui = T("Mongdock.Views.NotificationUi");
                var pal = T("Mongdock.ViewModels.UiTheme").GetMethod("Palette", Any)!.Invoke(null, new object[] { services.Settings.Current })!;
                var nbg = (Brush)pal.GetType().GetProperty("CardBackground")!.GetValue(pal)!;
                var cases = new[]
                {
                    ("title", new Mongdock.Services.NotificationItem(1, "Claude_pzs8sxrjxfjjc!Claude", "Claude", "몽독 개발 세션이 작업을 마쳤어요 — 독 넘침 칸과 긴 알림 펼치기, 설정 창 색을 고쳤습니다", new[] { "확인해 주세요" }, DateTime.Now, null, null, false, null)),
                    ("body", new Mongdock.Services.NotificationItem(2, "Claude_pzs8sxrjxfjjc!Claude", "Claude", "작업 완료", new[] { "독 넘침 칸과 긴 알림 펼치기를 고쳤습니다. 오른쪽 세로 독에서 위·아래 끝 아이콘이 확대돼도 화면 안에 보이게 했고, 아이콘이 많으면 ⋯ 칸으로 모읍니다. 설정 창은 사이드바와 스위치·슬라이더를 몽독 톤으로 맞췄습니다." }, DateTime.Now, null, null, false, null)),
                    ("short", new Mongdock.Services.NotificationItem(3, "Claude_pzs8sxrjxfjjc!Claude", "Claude", "짧은 알림", new[] { "한 줄" }, DateTime.Now, null, null, false, null)),
                };
                foreach (var (name, item) in cases)
                {
                    var ncard = (System.Windows.Controls.Border)ui.GetMethod("Card", Any)!.Invoke(null, new object?[] { services, pal, item, nbg, 0, null })!;
                    var host = new System.Windows.Controls.Border { Width = 340, Child = ncard, Background = nbg };
                    host.Measure(new Size(340, double.PositiveInfinity));
                    host.Arrange(new Rect(host.DesiredSize));
                    host.UpdateLayout();
                    bool can = (bool)ui.GetMethod("CanExpand", Any)!.Invoke(null, new object[] { ncard })!;
                    Console.WriteLine($"  알림 펼침 판정 {name}: {(can ? "펼칠 것 있음" : "없음")}");
                    Save(host, $"notify-{name}-folded");
                    ui.GetMethod("ExpandCard", Any)!.Invoke(null, new object[] { ncard, true });
                    foreach (var tb in Descendants(ncard).OfType<System.Windows.Controls.TextBlock>().Where(t => t.MaxHeight < 100)) { tb.BeginAnimation(FrameworkElement.MaxHeightProperty, null); tb.MaxHeight = 136; } // 화면 밖에선 애니메이션이 돌지 않아 끝 상태로
                    host.Measure(new Size(340, double.PositiveInfinity));
                    host.Arrange(new Rect(host.DesiredSize));
                    host.UpdateLayout();
                    Save(host, $"notify-{name}-expanded");
                }
            }

            // 1-d) 아이콘 고르기 카드 (다크: 미리 보기·판 색 칸 70%)
            {
                var allAppsIcon = (ImageSource)T("Mongdock.Services.MacIconRenderer").GetMethod("AllApps", Any)!.Invoke(null, null)!;
                var picker = New("Mongdock.Views.IconPickerWindow", services, new PinIcon { Mode = PinIconMode.Glyph, Glyph = "", Color = "mongdock" }, false, "자동",
                    new Func<PinIcon?, ImageSource?>(_ => allAppsIcon), new Action<PinIcon?>(_ => { }), null);
                Save((FrameworkElement)picker.Content, "iconpicker");
                picker.Close();
            }

            // 1-e) "⋯ 나머지 앱" 판 (오른쪽 세로 독 기준) — 실행 중 앱 예시 아이콘
            {
                var entryType = T("Mongdock.Views.OverflowStackPanel+Entry");
                var listType = typeof(List<>).MakeGenericType(entryType);
                var entries = (System.Collections.IList)Activator.CreateInstance(listType)!;
                var macR = T("Mongdock.Services.MacIconRenderer");
                var sample = (ImageSource)macR.GetMethod("AllApps", Any)!.Invoke(null, null)!;
                foreach (var n in new[] { "Discord", "Steam", "Netflix", "Notion", "Visual Studio Code", "카카오톡" })
                    entries.Add(Activator.CreateInstance(entryType, n, sample, new Action(() => { }), new Func<System.Windows.Controls.ContextMenu>(() => new System.Windows.Controls.ContextMenu()), null));
                var mon = Monitors.GetPrimary();
                var ov = New("Mongdock.Views.OverflowStackPanel", services, T("Mongdock.ViewModels.UiTheme").GetMethod("Palette", Any)!.Invoke(null, new object[] { services.Settings.Current })!, entries,
                    new Rect(mon.WorkArea.Right - 60, mon.WorkArea.Bottom - 120, 40, 40), DockEdge.Right, mon);
                Save((FrameworkElement)ov.Content, "overflow-panel");
                ov.Close();
            }

            // 0.6.1 브라우저 계정: 이 PC 의 실제 계정(이름·사진) 대신 예시 둘 — Chrome 항목은 "회사" 계정, 링크 항목은 기본
            {
                var profType = T("Mongdock.Services.ChromiumProfiles+Profile");
                var listT = typeof(List<>).MakeGenericType(profType);
                (string a, string b) names = _lang switch { "ko" => ("개인", "회사"), "de" => ("Privat", "Arbeit"), _ => ("Personal", "Work") };
                var example = (System.Collections.IList)Activator.CreateInstance(listT)!;
                example.Add(Activator.CreateInstance(profType, "Default", names.a, null));
                example.Add(Activator.CreateInstance(profType, "Profile 1", names.b, null));
                var funcT = typeof(Func<,>).MakeGenericType(typeof(string), typeof(IReadOnlyList<>).MakeGenericType(profType));
                var holder = new ExampleProfiles(example);
                var del = Delegate.CreateDelegate(funcT, holder, typeof(ExampleProfiles).GetMethod("Get")!.MakeGenericMethod(profType));
                T("Mongdock.Services.RoutineProfiles").GetProperty("ProfilesForTest", Any)!.SetValue(null, del);
            }
            // 2) 편집 창: 크게 / Chrome 세부 펼침(+ 고급)
            var work = new RoutineDef { Id = "r-test1", Name = "업무 시작", Items = Work() };
            if (work.Items.Count > 1 && work.Items[1].Kind == RoutineItemKind.App) work.Items[1].Profile = "Profile 1";
            foreach (var expand in new[] { -1, 1 })
            {
                var editor = New("Mongdock.Views.RoutineEditorWindow", services, work, work, false);
                if (expand >= 0) editor.GetType().GetMethod("ExpandForTest", Any)!.Invoke(editor, new object[] { expand, true });
                Save((FrameworkElement)editor.Content, expand < 0 ? "editor" : "editor-detail");
                editor.Close();
            }

            // 2-a) 루틴 추천 [만들기] 편집 창 + 브라우저 페이지 힌트 (§15-1)
            {
                var sug = new RoutineDef { Name = "평일 아침 루틴", Items = new() { App(edge, "Outlook", RoutinePlacementMode.Keep), App(chrome, "Chrome", RoutinePlacementMode.Keep), App(code, "VS Code", RoutinePlacementMode.Keep) } };
                sug.More = new RoutineMore();
                sug.More.Start.Add(new RoutineStart { Kind = RoutineStartKind.Time, Time = "09:00", Days = new() { 1, 2, 3, 4, 5 } });
                var se = New("Mongdock.Views.RoutineEditorWindow", services, null, sug, true);
                se.GetType().GetMethod("ShowPageHint", Any)!.Invoke(se, new object[] { "Chrome", new List<string> { "Gmail", "Notion", "Jira 보드" } });
                Save((FrameworkElement)se.Content, "editor-suggest");
                se.Close();
            }

            // 2-b) 링크 항목 세부: 웹 주소 3개(탭) + 앱 링크 항목
            {
                var links = new RoutineDef { Id = "r-links", Name = "업무 링크", Items = new()
                {
                    new() { Kind = RoutineItemKind.Url, Target = "https://mail.company.com", Tabs = new() { "https://calendar.company.com", "https://notion.so/team" } },
                    new() { Kind = RoutineItemKind.Url, Target = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M" },
                } };
                var le = New("Mongdock.Views.RoutineEditorWindow", services, links, links, false);
                le.GetType().GetMethod("ExpandForTest", Any)!.Invoke(le, new object[] { 0, false });
                Save((FrameworkElement)le.Content, "editor-links");
                le.Close();
            }

            // 2-1) 편집 창 "▸ 더 보기" 펼침 (시작 조건 2개·끝 조건·함께 바꿀 것·예외 앱·물어보기·머문 시간)
            {
                var more = new RoutineDef
                {
                    Id = "r-test3",
                    Name = "게임",
                    Items = { App(notepad, "Steam", RoutinePlacementMode.Max), App(chrome, "Discord", RoutinePlacementMode.Right, 2) },
                    More = new RoutineMore
                    {
                        Start =
                        {
                            new RoutineStart { Kind = RoutineStartKind.Audio, Device = "dev-headset", Name = "헤드셋 (Galaxy Buds)" },
                            new RoutineStart { Kind = RoutineStartKind.Time, Time = "21:00", Days = new List<int> { 5, 6 } },
                        },
                        End = new RoutineEnd { AudioRemoved = "dev-headset", AudioName = "헤드셋 (Galaxy Buds)", AllAppsClosed = true },
                        Change = new RoutineChange { Dnd = true, DockHide = true, OutputDevice = "dev-headset", OutputName = "헤드셋 (Galaxy Buds)", Volume = 40 },
                        DndExceptions = { "slack", "kakaotalk" },
                        AskSimilar = true,
                        ShowTime = true,
                    },
                };
                var editor = New("Mongdock.Views.RoutineEditorWindow", services, more, more, false);
                editor.GetType().GetMethod("OpenMoreForTest", Any)!.Invoke(editor, null);
                Save((FrameworkElement)editor.Content, "editor-more");
                editor.Close();
            }

            // 2-2) 묻기 배너: 시작 조건 / 끝 조건 / 비슷하게 열면
            {
                var bannerType = T("Mongdock.Views.NotificationBannerWindow");
                var buttonType = bannerType.GetNestedType("BannerButton", Any)!;
                object Btn(string text, bool primary = false, bool small = false) => Activator.CreateInstance(buttonType, text, (Action)(() => { }), primary, small)!;
                var listType = typeof(List<>).MakeGenericType(buttonType);
                var icons2 = T("Mongdock.Services.RoutineIcons");
                var gameR = new RoutineDef { Name = "게임", Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "blue", Glyph = "\uE7FC" } };
                var gameIcon = (ImageSource)icons2.GetMethod("Icon", Any)!.Invoke(null, new object[] { services, gameR, settings.Current.Dock.IconStyle })!;
                foreach (var (name, title, body, buttons) in new[]
                {
                    ("ask", "게임 루틴을 열까요?", "헤드셋 (Galaxy Buds)를 연결하면", new[] { Btn("열기", true), Btn("오늘은 안 함"), Btn("다시 묻지 않기", small: true) }),
                    ("askend", "게임 루틴을 끝낼까요?", "헤드셋 (Galaxy Buds) 연결이 끊겼어요.", new[] { Btn("끝내기", true), Btn("계속") }),
                    ("similar", "업무 시작 루틴인가요?", "맞으면 함께 바꿀 것을 적용하고 끝내기도 쓸 수 있어요.", new[] { Btn("맞아요", true), Btn("아니요") }),
                    ("group", "헤드셋 (Galaxy Buds) 연결됐어요", "어떤 루틴을 열까요?", new[] { Btn("게임", true), Btn("음악"), Btn("안 열기"), Btn("다시 묻지 않기 ▸", small: true) }), // 같은 사건에 루틴 여럿 (§14-1)
                    ("suggest", "루틴으로 만들어 둘까요?", (string)T("Mongdock.Services.RoutineSuggest").GetMethod("AskBody", Any)!.Invoke(null, new object[] { "평일 아침", "Outlook·Chrome·한글" })!, new[] { Btn("만들기", true), Btn("괜찮아요") }), // 루틴 추천 (§15)
                })
                {
                    var banner = (Window)bannerType.GetConstructors(Any)[0].Invoke(new object[] { services, palette });
                    var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
                    foreach (var b in buttons) list.Add(b);
                    var item = new NotificationItem(0, "", "mongdock", Mongdock.Loc.T(title), new[] { Mongdock.Loc.T(body) }, DateTime.Now, null, null, false, null);
                    var bannerIcon = name == "suggest" ? (ImageSource)T("Mongdock.Services.MacIconRenderer").GetMethod("AllApps", Any)!.Invoke(null, null)! : gameIcon;
                    bannerType.GetMethod("Add", Any)!.Invoke(banner, new object?[] { item, bannerIcon, null, list, TimeSpan.FromSeconds(10), null });
                    var root = (FrameworkElement)banner.Content;
                    foreach (var fe in Descendants(root).OfType<UIElement>()) { fe.BeginAnimation(UIElement.OpacityProperty, null); fe.Opacity = fe.Opacity < 0.99 && fe is not System.Windows.Controls.TextBlock ? 1 : fe.Opacity; if (fe.RenderTransform is TranslateTransform tt) { tt.BeginAnimation(TranslateTransform.XProperty, null); tt.BeginAnimation(TranslateTransform.YProperty, null); tt.X = 0; tt.Y = 0; } }
                    Save(root, "banner-" + name);
                    banner.Close();
                }
            }

            // 2-3) 상단바 데스크톱 표시 (모형): "게임 · 2/3 · 1:24 ☾"
            {
                var dv = new DrawingVisual();
                var size = new Size(420, 44);
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x1E, 0x1E, 0x22) : Color.FromRgb(0xF4, 0xF4, 0xF7)), null, new Rect(size));
                    var ft = new FormattedText("‹   게임 · 2/3 · 1:24 \u263E   ›", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Variable Text, Malgun Gothic"), 13, theme == "dark" ? Brushes.White : Brushes.Black, 1.0);
                    dc.DrawRoundedRectangle(new SolidColorBrush(theme == "dark" ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0, 0, 0)), null, new Rect((size.Width - ft.Width) / 2 - 12, 9, ft.Width + 24, 26), 13, 13);
                    dc.DrawText(ft, new Point((size.Width - ft.Width) / 2, 13));
                }
                var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(dir, $"routine-{theme}-topbar.png"))) enc.Save(fs);
            }

            // 3) 앱 모음 판 루틴 줄 (루틴 2개 · NEW 없이) / 0개 설명 카드
            var panelType = T("Mongdock.Views.AllAppsPanel");
            panelType.GetProperty("LoadIconsNow", Any)!.SetValue(null, true);
            T("Mongdock.Services.AllAppsCatalog").GetMethod("Apps")!.Invoke(null, new object[] { false }); // 판이 앱 목록을 바로 쓰게
            var savedRoutines = settings.Current.Routines.ToList();
            // 실행 중 점 그림용: RoutineService.Runs 에 "여는 중" 상태를 넣음 (창 없이 IsOpening = true)
            var runsField = T("Mongdock.Services.RoutineService").GetField("Runs", Any)!;
            var runs = (System.Collections.IDictionary)runsField.GetValue(null)!;
            void FakeRunning(string id)
            {
                var state = Activator.CreateInstance(runsField.FieldType.GetGenericArguments()[1], nonPublic: true)!;
                state.GetType().GetField("Running")!.SetValue(state, true);
                runs[id] = state;
            }
            foreach (var mode in new[] { "panel", "panel-running", "panel-empty" })
            {
                settings.Current.Routines.Clear();
                if (mode != "panel-empty")
                {
                    settings.Current.Routines.Add(work);
                    settings.Current.Routines.Add(new RoutineDef { Id = "r-test2", Name = "게임", Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "blue", Glyph = "\uE7FC" }, Items = { App(notepad, "Steam", RoutinePlacementMode.Max) } });
                }
                if (mode == "panel-running") // 짧은 이름·긴 이름(잘림) 실행 중 + 꺼진 루틴 하나
                {
                    string longName = _lang == "de" ? "Wochenendabend Spielen und Entspannen" : _lang == "ko" ? "주말 저녁 게임하고 쉬는 시간" : "Weekend evening games and chill";
                    settings.Current.Routines.Add(new RoutineDef { Id = "r-test-long", Name = longName, Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "green", Text = "W" }, Items = { App(notepad, "Steam", RoutinePlacementMode.Max) } });
                    FakeRunning(work.Id);
                    FakeRunning("r-test-long");
                }
                var w = New("Mongdock.Views.AllAppsPanel", services, palette, new Rect(800, 1000, 52, 52), DockEdge.Bottom, Monitors.GetPrimary());
                panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
                Save((FrameworkElement)w.Content, mode, 360);
                w.Close();
                runs.Clear();
            }
            settings.Current.Routines.Clear();
            settings.Current.Routines.AddRange(savedRoutines);

            // 4) 독 루틴 아이콘: 자동 2x2 · 기호 — 독 판 위에, 첫 아이콘은 실행 중 점 + 툴팁
            var icons = T("Mongdock.Services.RoutineIcons");
            ImageSource Icon(RoutineDef r) => (ImageSource)icons.GetMethod("Icon", Any)!.Invoke(null, new object[] { services, r, settings.Current.Dock.IconStyle })!;
            string Tip(RoutineDef r) => (string)icons.GetMethod("Tooltip", Any)!.Invoke(null, new object[] { r })!;
            var game = new RoutineDef { Name = "게임", Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "blue", Glyph = "\uE7FC" }, Items = { App(notepad, "Steam", RoutinePlacementMode.Max) } };
            var study = new RoutineDef { Name = "공부", Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "green", Text = "공" } };
            var two = new RoutineDef { Name = "메모", Items = { App(notepad, "메모장", RoutinePlacementMode.Left), App(chrome, "Chrome", RoutinePlacementMode.Right) } };
            {
                var dv = new DrawingVisual();
                var size = new Size(420, 150);
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(size));
                    dc.DrawRoundedRectangle(new SolidColorBrush(theme == "dark" ? Color.FromArgb(0xCC, 0x30, 0x33, 0x40) : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), null, new Rect(40, 70, 340, 72), 18, 18);
                    var list = new[] { work, two, game, study };
                    for (int i = 0; i < list.Length; i++)
                    {
                        double x = 60 + i * 80;
                        dc.DrawImage(Icon(list[i]), new Rect(x - (i == 0 ? 6 : 0), 76 - (i == 0 ? 10 : 0), i == 0 ? 68 : 56, i == 0 ? 68 : 56));
                        if (i == 0) dc.DrawEllipse(new SolidColorBrush(theme == "dark" ? Colors.White : Color.FromRgb(0x20, 0x20, 0x28)), null, new Point(x + 28, 138), 2.2, 2.2);
                    }
                    var text = new FormattedText(Tip(work), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text, Malgun Gothic"), 12,
                        theme == "dark" ? Brushes.White : Brushes.Black, 1.0);
                    dc.DrawRoundedRectangle(new SolidColorBrush(theme == "dark" ? Color.FromRgb(0x2A, 0x2C, 0x36) : Color.FromRgb(0xFA, 0xFA, 0xFC)), new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 0.5),
                        new Rect(30, 18, text.Width + 20, 26), 7, 7);
                    dc.DrawText(text, new Point(40, 23));
                }
                var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                string path = Path.Combine(dir, $"routine-{theme}-dock.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                Console.WriteLine($"  저장  {path}");
            }

            // 5) 끝내기 확인 카드 / 6) 다른 데스크톱 안내 카드
            var card = T("Mongdock.Views.ConfirmCardWindow");
            Window Card(string title, string message, string ok, string? cancel, string? check) =>
                (Window)card.GetConstructors(Any)[0].Invoke(new object?[] { services, palette, title, message, ok, (Action)(() => { }), cancel, null, check, true });
            var end = Card(Mongdock.Loc.F($"{work.Name} — 창 {4}개를 닫을까요?"), Mongdock.Loc.T("저장 안 한 작업은 각 앱이 물어봐요."), Mongdock.Loc.T("닫기"), null, Mongdock.Loc.T("데스크톱도 닫기"));
            Save((FrameworkElement)end.Content, "end");
            end.Close();
            var elsewhere = Card(Mongdock.Loc.F($"이미 켜져 있는 앱 {3}개는 원래 데스크톱에 있어요"), "Chrome · 카카오톡 · Notion", Mongdock.Loc.T("그 데스크톱으로 가기"), Mongdock.Loc.T("닫기"), null);
            Save((FrameworkElement)elsewhere.Content, "elsewhere");
            elsewhere.Close();

            // 7) 0.6.2 로고 메뉴 전원 줄 (업데이트가 다시 시작을 기다릴 때 / 아닐 때) + 「업데이트 후 다시 시작」 확인 카드
            {
                var tb = T("Mongdock.Views.TopBarWindow");
                var dm = T("Mongdock.Views.DockMenus");
                foreach (bool pending in new[] { true, false })
                {
                    var power = ((System.Collections.IEnumerable)tb.GetMethod("PowerItems", Any)!.Invoke(null, new object[] { pending })!).Cast<object>().ToList();
                    string P(object o, string n) => (string)o.GetType().GetProperty(n)!.GetValue(o)!;
                    var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(5) };
                    System.Windows.Controls.MenuItem Mi(string label) => (System.Windows.Controls.MenuItem)dm.GetMethod("Item", Any)!.Invoke(null, new object?[] { label, (Action)(() => { }), true, null })!;
                    stack.Children.Add(Mi(Mongdock.Loc.T("작업 관리자")));
                    stack.Children.Add(new System.Windows.Controls.Separator());
                    stack.Children.Add(Mi(Mongdock.Loc.T("절전")));
                    foreach (var pw in power) stack.Children.Add(Mi(P(pw, "Label")));
                    stack.Children.Add(new System.Windows.Controls.Separator());
                    stack.Children.Add(Mi(Mongdock.Loc.T("화면 잠금")));
                    var box = new System.Windows.Controls.Border { Child = stack, CornerRadius = new CornerRadius(10), Width = 280 };
                    box.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Menu.Background");
                    var host = new System.Windows.Controls.Border { Padding = new Thickness(12), Child = box, Background = (Brush)palette.GetType().GetProperty("WindowBackground")!.GetValue(palette)! };
                    host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    host.Arrange(new Rect(host.DesiredSize));
                    host.UpdateLayout();
                    Save(host, pending ? "logomenu-update" : "logomenu-normal");
                    if (pending)
                    {
                        var c = Card(P(power[0], "Title"), P(power[0], "Body"), P(power[0], "Button"), null, null);
                        Save((FrameworkElement)c.Content, "confirm-update-restart");
                        c.Close();
                    }
                }
            }
            settings.Dispose();
        }
        GC.KeepAlive(app);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static void Save(Window w, string path)
    {
        var content = (FrameworkElement)w.Content;
        double width = content.ActualWidth, height = content.ActualHeight;
        const double scale = 1.5;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            // 여백까지 그대로 (VisualBrush 기본값은 내용 경계에 맞춰 늘림)
            var brush = new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.None };
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }
        var rtb = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
