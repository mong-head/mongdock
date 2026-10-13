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
        int types = Array.FindIndex(args, a => a == "--types");
        if (types >= 0 && types + 1 < args.Length) { _typesOnly = true; RenderAllAppsPanel(args[types + 1]); return 0; }
        int allApps = Array.FindIndex(args, a => a == "--allapps");
        if (allApps >= 0 && allApps + 1 < args.Length) RenderAllAppsPanel(args[allApps + 1]);
        if (Array.IndexOf(args, "--dockfit") >= 0) { DockFitReport(); return 0; }
        if (Array.IndexOf(args, "--clipdemo") >= 0) { ClipDemo(); return 0; }
        if (Array.IndexOf(args, "--suggest") >= 0) return SuggestTable();
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
                        ("위 띠 가운데", new Point(ww / 2, 18), 2), ("최대화 동그라미", new Point(14 + 2 * 20 + 6, 18), 9),
                        ("닫기 동그라미", new Point(14 + 6, 18), 1), ("본문", new Point(ww / 2, wh / 2), 1),
                        ("오른쪽 끝", new Point(ww - 2, wh / 2), 11), ("오른쪽 아래", new Point(ww - 2, wh - 2), 17),
                    };
                    foreach (var (n, at, want) in checks)
                    {
                        int got = Hit(at);
                        Console.WriteLine($"  {(got == want ? "통과" : "실패")}  제목 줄 판정 {n}: {got} (기대 {want}) — 배율 {dpiS.DpiScaleX:0.##}");
                    }
                }
                // 제목 줄 동그라미 3가지 상태 (활성 / 비활성 / 호버) — 왼쪽 위만 크게
                var preview = typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.MongTitleBar")!.GetMethod("Preview", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                foreach (var (state, act, hov) in new[] { ("active", true, false), ("inactive", false, false), ("hover", true, true) })
                {
                    preview.Invoke(null, new object[] { w, act, hov });
                    whole.UpdateLayout();
                    const double z = 3;
                    var crop = new RenderTargetBitmap((int)(260 * z), (int)(60 * z), 96 * z, 96 * z, PixelFormats.Pbgra32);
                    var cv = new DrawingVisual();
                    using (var dc = cv.RenderOpen())
                    {
                        dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, 260, 60));
                        dc.DrawRectangle(new VisualBrush(whole) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 260, 60) }, null, new Rect(0, 0, 260, 60));
                    }
                    crop.Render(cv);
                    var enc3 = new PngBitmapEncoder();
                    enc3.Frames.Add(BitmapFrame.Create(crop));
                    string cpath = Path.Combine(dir, $"titlebar-{theme}-{state}.png");
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
            var modes = _typesOnly ? new[] { "type-A", "type-B", "type-C" } : new[] { "home", "group", "feedback", "search", "edit", "cleanup", "unused", "size-old", "size-now", "size-new" };
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
                if (mode == "unused") panelType.GetMethod("ShowUnusedForTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
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

            // 2) 편집 창: 크게 / Chrome 세부 펼침(+ 고급)
            var work = new RoutineDef { Id = "r-test1", Name = "업무 시작", Items = Work() };
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
            foreach (var mode in new[] { "panel", "panel-empty" })
            {
                settings.Current.Routines.Clear();
                if (mode == "panel")
                {
                    settings.Current.Routines.Add(work);
                    settings.Current.Routines.Add(new RoutineDef { Id = "r-test2", Name = "게임", Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "blue", Glyph = "\uE7FC" }, Items = { App(notepad, "Steam", RoutinePlacementMode.Max) } });
                }
                var w = New("Mongdock.Views.AllAppsPanel", services, palette, new Rect(800, 1000, 52, 52), DockEdge.Bottom, Monitors.GetPrimary());
                panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
                Save((FrameworkElement)w.Content, mode, 360);
                w.Close();
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
