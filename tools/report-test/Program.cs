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
            string path = Path.Combine(dir, $"settings-{_lang}-{page.ToString()!.ToLowerInvariant()}.png");
            using (var fs = File.Create(path)) enc.Save(fs);
            Console.WriteLine($"  저장  {path}");
        }
        w.Close();
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
