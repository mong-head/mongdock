using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;

namespace LiveTest;

/// <summary>
/// live-test OUT [--idle 초] — 앱 모음 판 실제 창 시험 (화면에 진짜 판이 뜸 — 사용자가 PC 를 쓰지 않을 때만).
/// 시험용 데이터 폴더(임시)로 몽독 설정·로그를 따로 씀. 설치된 몽독은 건드리지 않음.
/// 시나리오: 열기·닫기 5번(가운데·아이콘) / 열려 있는 동안 다른 창 활성화 / 연타 10번 / 나타나는 도중 닫기(Esc) / (--idle) 오래 쉰 뒤 열기.
/// 열 때마다 판 자리를 16ms 간격으로 찍어 "판이 보이는 정도"가 0→1 로 한 번만 오르는지(사라졌다 다시 뜨지 않는지) 보고, 프레임 그림을 OUT 에 남김.
/// </summary>
internal static partial class Program
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly List<string> Results = new();
    private static int _fail;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("live-test OUT [--idle 초] [--new] | live-test --demo [횟수] [간격초]"); return 2; }
        if (args[0] == "--testwindow") return TestWindowMain(); // --minimize 의 시험 창 (다른 프로세스)
        if (args[0] == "--demo") return Demo(args.Length > 1 ? int.Parse(args[1]) : 3, args.Length > 2 ? int.Parse(args[2]) : 12);
        string outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        int idleAt = Array.IndexOf(args, "--idle");
        int idle = idleAt >= 0 && idleAt + 1 < args.Length ? int.Parse(args[idleAt + 1]) : 0;
        string data = Path.Combine(Path.GetTempPath(), "mongdock-live-" + Guid.NewGuid().ToString("N")[..8]);
        if (Array.IndexOf(args, "--realdata") >= 0)
        {
            // 사용자 설정·기록을 시험 폴더로 복사만 (읽기 전용 — 원본은 건드리지 않음, 로그는 뺌): 실제 폴더 구성으로 시험
            string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mongdock");
            foreach (var f in Directory.EnumerateFiles(real, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(real, f);
                if (rel.StartsWith("logs", StringComparison.OrdinalIgnoreCase)) continue;
                string to = Path.Combine(data, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                try { File.Copy(f, to, true); } catch (IOException) { /* 쓰는 중인 파일은 건너뜀 */ }
            }
        }
        Environment.SetEnvironmentVariable("MONGDOCK_DATA_DIR", data); // AppInfo 를 건드리기 전에
        Console.WriteLine($"시험 데이터 폴더: {data}");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var name in new[] { "Themes/Controls.xaml", "Themes/Menus.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/mongdock;component/{name}") });
        app.Resources["MenuRowMinHeight"] = 26.0;
        Mongdock.Loc.Init("ko");
        var settings = new SettingsService();
        Mongdock.ViewModels.UiFonts.Apply(settings.Current);
        Mongdock.ViewModels.UiTheme.Apply(settings.Current);
        var tracker = new WindowTracker();
        var launcher = new AppLauncher(tracker);
        var services = new AppServices(settings, tracker, launcher, new IconService(), new DesktopWindowService(), new VirtualDesktopService(),
            new ShellActions(), new ImeService(), new StatusService(), new MediaService(), new AppMenuService(settings), new StartupService(),
            new NotificationService(tracker, launcher), new TrayIconService(), new CalendarFeedService(settings));
        var asm = typeof(Mongdock.App).Assembly;
        var panelType = asm.GetType("Mongdock.Views.AllAppsPanel")!;
        var introType = asm.GetType("Mongdock.Views.PanelIntro")!;
        asm.GetType("Mongdock.Services.AllAppsCatalog")!.GetMethod("Apps")!.Invoke(null, new object[] { false });
        // 몽독 시작 뒤와 같게: 앱 모음 판 미리 준비(아이콘·그림 창·판 미리 만들어 보기)를 다 끝낸 뒤 시험
        panelType.GetMethod("Warm", Any)!.Invoke(null, new object[] { services });
        Pump(8000);

        var monitor = Monitors.GetPrimary();
        var work = monitor.WorkArea;
        var icon = new Rect(work.Left + work.Width / 2 - 26 - 3 * 62, work.Bottom - 62, 52, 52); // 독 가운데 줄의 앱 모음 자리
        var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);

        Window Open() => (Window)panelType.GetConstructors(Any)[0].Invoke(new object[] { services, palette, icon, DockEdge.Bottom, monitor });
        bool GhostVisible() => introType.GetField("_ghost", Any)!.GetValue(null) is Window { IsVisible: true };
        void Close(Window w) => w.GetType().GetMethod("CloseAnimated", Any)!.Invoke(w, null);

        panelType.GetProperty("FollowRealCursor", Any)!.SetValue(null, false); // 끌기는 스크립트 좌표로 — 진짜 커서는 안 움직임
        if (Array.IndexOf(args, "--dragpic") >= 0)
        {
            panelType.GetProperty("EdgeScrollEnabled", Any)!.SetValue(null, false);
            DragPicture(panelType, Open, Close, outDir);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--minimize") >= 0)
        {
            MinimizeRun(services, settings, work, outDir);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--tour") >= 0)
        {
            TourRun(services, work, outDir);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--panelreopen") >= 0)
        {
            PanelReopen(services, panelType, work);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--introzoom") >= 0)
        {
            IntroZoom(services, panelType, settings, work, outDir);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--memscenario") >= 0)
        {
            MemScenario(services, panelType, Open, Close, work, outDir);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--tourflicker") >= 0)
        {
            TourFlicker(services, work, outDir);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--folderorder") >= 0)
        {
            panelType.GetProperty("EdgeScrollEnabled", Any)!.SetValue(null, false);
            FolderOrder(panelType, Open, Close, settings);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--dragfolders") >= 0)
        {
            panelType.GetProperty("EdgeScrollEnabled", Any)!.SetValue(null, false);
            DragFollowFolders(panelType, Open, Close);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--dragfollow") >= 0)
        {
            panelType.GetProperty("EdgeScrollEnabled", Any)!.SetValue(null, false);
            DragFollow(panelType, Open, Close, settings);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            return _fail == 0 ? 0 : 1;
        }
        if (Array.IndexOf(args, "--new") >= 0)
        {
            panelType.GetProperty("EdgeScrollEnabled", Any)!.SetValue(null, false); // 끌기는 스크립트로 — 진짜 커서 자리로 판이 스크롤되지 않게
            NewFeatures(services, settings, panelType, Open, Close, outDir, work, tracker);
            File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
            Console.WriteLine(_fail == 0 ? "실제 창 시험: 모두 통과" : $"실제 창 시험: {_fail}개 실패");
            try { File.Copy(Path.Combine(data, "logs", "mongdock.log"), Path.Combine(outDir, "mongdock.log"), true); } catch { /* 로그 없음 */ }
            return _fail == 0 ? 0 : 1;
        }

        foreach (var mode in new[] { "center", "icon" })
        {
            settings.Current.AllApps.OpenAnimation = mode;
            for (int i = 1; i <= 5; i++) OpenAndWatch($"{mode}-{i}", Open, Close, GhostVisible, outDir, work);
        }
        settings.Current.AllApps.OpenAnimation = "center";

        // 다른 창이 포커스를 가져감 → 판은 그대로여야 함 (예전엔 "닫음 — 비활성화")
        {
            var w = Open();
            w.Show();
            Pump(500);
            var other = new Window { Width = 200, Height = 120, Left = work.Left + 20, Top = work.Top + 20, Title = "live-test other", Topmost = true, ShowInTaskbar = false };
            other.Show();
            other.Activate();
            Pump(700);
            Check("다른 창 활성화 뒤에도 판이 열려 있음", w.IsVisible);
            other.Close();
            Close(w);
            Pump(300);
        }

        // 연타: 열고 80ms 뒤 닫기 × 10
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                var w = Open();
                w.Show();
                Pump(80);
                Close(w);
                Pump(40);
            }
            Pump(500);
            Check($"연타 10번 뒤 그림 창이 남지 않음 ({sw.ElapsedMilliseconds}ms)", !GhostVisible());
            Check("연타 10번 뒤 열린 판 없음", Application.Current.Windows.OfType<Window>().All(x => x.GetType() != panelType || !x.IsVisible));
        }

        // 나타나는 도중 닫기 (Esc 와 같은 경로)
        foreach (var at in new[] { 30, 90, 150 })
        {
            var w = Open();
            w.Show();
            Pump(at);
            Close(w);
            Pump(400);
            Check($"나타나는 {at}ms 에 닫음 → 그림 창 없음", !GhostVisible());
        }

        if (idle > 0)
        {
            Console.WriteLine($"{idle}초 쉼 (오래 쉰 뒤 첫 열기)…");
            GC.Collect();
            Pump(idle * 1000);
            OpenAndWatch("idle", Open, Close, GhostVisible, outDir, work);
        }

        File.WriteAllLines(Path.Combine(outDir, "results.txt"), Results);
        Console.WriteLine(_fail == 0 ? "실제 창 시험: 모두 통과" : $"실제 창 시험: {_fail}개 실패");
        try { File.Copy(Path.Combine(data, "logs", "mongdock.log"), Path.Combine(outDir, "mongdock.log"), true); } catch { /* 로그 없음 */ }
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// --new: 이번 묶음 실제 창 시험 (2026-10-13 PM 요청 — 사용자 앱은 건드리지 않음, 시험 데이터 폴더라 설정도 그대로).
    /// A) 앱 모음 [안 쓰는 앱] 보기: 이 PC 앱으로 실제 계산 → 목록이 뜨는지·걸린 시간, 판 그림.
    /// B) 알림 튀기기 애니메이션: 실제 창에 독 아이콘 하나를 띄워 NotifyBounce → 16ms 마다 이동량을 읽어 2번·35%·1초 안에 제자리인지.
    /// C) 작업 표시줄 깜빡임 신호: 시험 창(live-test)을 FlashWindowEx → 이 시험의 창 추적이 깜빡임을 받는지.
    ///    설치된 몽독 독에도 이 시험 창이 실행 중 앱으로 보이므로 실제 독이 튀는지는 몽독 로그("독 아이콘 튀기기 (깜빡임")로 따로 확인.
    /// </summary>
    /// <summary>
    /// 끌기 그림이 커서에 붙어 다니는지 (2026-10-13 사용자: 폴더 안에서 끌면 무겁게, 거리를 두고 늦게 따라옴):
    /// 즐겨찾기·모든 앱·펼친 폴더 안 칸을 스크립트로 끌며 매 프레임 그림 창 자리와 커서 자리 거리(≤ 2px)와 UpdateDrag 시간을 잼.
    /// </summary>
    private static void DragFollow(Type panelType, Func<Window> open, Action<Window> close, SettingsService settings)
    {
        // 시험 데이터 폴더는 즐겨찾기가 비어 있음 — 앱 셋을 즐겨찾기로 (시험 폴더 안에서만)
        var catalog = (System.Collections.IEnumerable)typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.AllAppsCatalog")!.GetProperty("Cached")!.GetValue(null)!;
        foreach (var a in catalog.Cast<object>().Take(3)) settings.Current.AllApps.Favorites.Add((string)a.GetType().GetProperty("Key")!.GetValue(a)!);
        var w = open();
        panelType.GetField("_allOpen", Any)!.SetValue(w, true);
        var folders = (System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!;
        if (folders.Count > 0) panelType.GetField("_expanded", Any)!.SetValue(w, (string)folders[0]!.GetType().GetProperty("Id")!.GetValue(folders[0])!);
        w.Show();
        Pump(1200);
        panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
        Pump(500);
        var dropTag = panelType.GetNestedType("DropTag", BindingFlags.NonPublic)!;
        var dragItem = panelType.GetNestedType("DragItem", BindingFlags.NonPublic)!;
        var favCells = ((System.Collections.IEnumerable)panelType.GetField("_favCells", Any)!.GetValue(w)!).Cast<System.Windows.Controls.Border>().ToList();
        var groupCells = ((System.Collections.IEnumerable)panelType.GetField("_groupCells", Any)!.GetValue(w)!).Cast<System.Windows.Controls.Border>().ToList();
        string Kind(System.Windows.Controls.Border b) => b.Tag is { } t && t.GetType() == dropTag ? (string)dropTag.GetProperty("Kind")!.GetValue(t)! : "";
        string KeyOf(System.Windows.Controls.Border b) => (string)dropTag.GetProperty("Key")!.GetValue(b.Tag)!;
        var allCells = Descendants((DependencyObject)w.Content).OfType<System.Windows.Controls.Border>()
            .Where(b => Kind(b) == "app" && !favCells.Contains(b) && !groupCells.Contains(b) && b.IsVisible && b.ActualWidth > 0).ToList();
        // 펼친 폴더가 보이게 스크롤
        if (groupCells.Count > 0 && panelType.GetField("_bodyScroll", Any)!.GetValue(w) is System.Windows.Controls.ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset + groupCells[0].TranslatePoint(new Point(0, 0), sv).Y - 60);
            Pump(400);
        }
        var cases = new List<(string Name, System.Windows.Controls.Border? Cell)>
        {
            ("즐겨찾기", favCells.FirstOrDefault(c => Kind(c) is "app" or "favapp" && c.IsVisible)),
            ("모든 앱", allCells.FirstOrDefault(c => c.TranslatePoint(new Point(0, 0), w).Y > 80 && c.TranslatePoint(new Point(0, 0), w).Y < w.ActualHeight - 200)),
            ("폴더 안", groupCells.FirstOrDefault(c => c.IsVisible)),
        };
        var dpi = VisualTreeHelper.GetDpi(w);
        foreach (var (name, cell) in cases)
        {
            if (cell is null) { Check($"끌기 따라오기({name}): 끌 칸을 찾지 못함", false); continue; }
            var grab = new Point(cell.ActualWidth * 0.3, cell.ActualHeight * 0.4);
            var start = cell.TranslatePoint(grab, w);
            panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { cell, Activator.CreateInstance(dragItem, false, KeyOf(cell), false), null, grab });
            var drag = panelType.GetField("_drag", Any)!.GetValue(w)!;
            var gh = new WindowInteropHelper((Window)drag.GetType().GetField("Ghost")!.GetValue(drag)!).Handle;
            var grabPx = (Point)drag.GetType().GetField("Grab")!.GetValue(drag)!;
            double worst = 0, worstMs = 0, sumMs = 0;
            int steps = 40;
            for (int i = 1; i <= steps; i++)
            {
                // 오른쪽 아래로 갔다가 돌아오는 길 (한 프레임 7px — 보통 끄는 빠르기)
                double t = i <= steps / 2 ? i : steps - i;
                var p = new Point(start.X + t * 7, start.Y + t * 3);
                var sw = Stopwatch.StartNew();
                panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { p });
                double ms = sw.Elapsed.TotalMilliseconds;
                worstMs = Math.Max(worstMs, ms);
                sumMs += ms;
                Pump(16);
                if (panelType.GetField("_drag", Any)!.GetValue(w) is null) break;
                GetWindowRect(gh, out var r);
                var cursorPx = w.PointToScreen(p);
                double cx = r.Left + (12 + grabPx.X) * dpi.DpiScaleX, cy = r.Top + (12 + grabPx.Y) * dpi.DpiScaleY;
                worst = Math.Max(worst, Math.Max(Math.Abs(cx - cursorPx.X), Math.Abs(cy - cursorPx.Y)) / dpi.DpiScaleX);
            }
            // 실제 끌기 경로: 커서만 움직이고 (UpdateDrag 없이) 프레임마다 그림이 커서 자리로 오는지 — 커서는 시험용 자리
            panelType.GetProperty("FollowRealCursor", Any)!.SetValue(null, true);
            panelType.GetMethod("StartFollow", Any)!.Invoke(w, null);
            var fake = w.PointToScreen(start);
            panelType.GetProperty("CursorForTest", Any)!.SetValue(null, new Func<Point>(() => fake));
            double worstFollow = 0;
            int frames = 0;
            EventHandler count = (_, _) => frames++;
            CompositionTarget.Rendering += count;
            for (int i = 1; i <= 30; i++)
            {
                fake = w.PointToScreen(new Point(start.X + (i <= 15 ? i : 30 - i) * 9, start.Y - (i <= 15 ? i : 30 - i) * 2));
                // 다음 프레임 한 번이 지나간 뒤 봄 (프레임 콜백 순서상 같은 프레임 안이면 판 쪽이 먼저 돌았을 수 있어 두 번)
                int seen = frames;
                var waited = Stopwatch.StartNew();
                while (frames < seen + 2 && waited.ElapsedMilliseconds < 200) Pump(1);
                GetWindowRect(gh, out var r);
                double cx = r.Left + (12 + grabPx.X) * dpi.DpiScaleX, cy = r.Top + (12 + grabPx.Y) * dpi.DpiScaleY;
                worstFollow = Math.Max(worstFollow, Math.Max(Math.Abs(cx - fake.X), Math.Abs(cy - fake.Y)) / dpi.DpiScaleX);
            }
            CompositionTarget.Rendering -= count;
            panelType.GetProperty("CursorForTest", Any)!.SetValue(null, null);
            panelType.GetProperty("FollowRealCursor", Any)!.SetValue(null, false);
            Check($"끌기 따라오기({name}, 프레임마다 커서 자리): 다음 프레임에 그림과 커서 거리 ≤ 2px (가장 먼 {worstFollow:0.0}px)", worstFollow <= 2);
            Check($"끌기 따라오기({name}): 프레임마다 그림과 커서 거리 ≤ 2px (가장 먼 {worst:0.0}px, 한 번 옮기기 평균 {sumMs / steps:0.0}ms·최대 {worstMs:0.0}ms)", worst <= 2);
            panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { false });
            Pump(500);
        }
        close(w);
        Pump(400);
    }

    /// <summary>
    /// 끌기 모양 그림 (2026-10-13 사용자: 카드+이름 덩어리·두 겹 강조가 어색함): 펼친 폴더 안 앱을 끌어
    /// ① 다른 폴더 칸 위(폴더 그림만 1.08배 + 연보라 테두리) ② 자기 폴더 칸 위(강조 없음)에 둔 화면을 찍음.
    /// </summary>
    private static void DragPicture(Type panelType, Func<Window> open, Action<Window> close, string outDir)
    {
        var w = open();
        var folders = (System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!;
        if (folders.Count < 2) { Check("끌기 그림: 폴더가 둘 이상 필요", false); close(w); return; }
        string Id(int i) => (string)folders[i]!.GetType().GetProperty("Id")!.GetValue(folders[i])!;
        panelType.GetField("_expanded", Any)!.SetValue(w, Id(0));
        w.Show();
        Pump(1200);
        panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
        Pump(500);
        var dropTag = panelType.GetNestedType("DropTag", BindingFlags.NonPublic)!;
        var dragItem = panelType.GetNestedType("DragItem", BindingFlags.NonPublic)!;
        var groupCells = ((System.Collections.IEnumerable)panelType.GetField("_groupCells", Any)!.GetValue(w)!).Cast<System.Windows.Controls.Border>().ToList();
        FrameworkElement? Tile(string id) => Descendants((DependencyObject)w.Content).OfType<FrameworkElement>().FirstOrDefault(e => e.Uid == "tile:" + id);
        if (groupCells.Count == 0 || Tile(Id(0)) is null || Tile(Id(1)) is null) { Check("끌기 그림: 펼친 폴더 칸·폴더 그림을 찾지 못함", false); close(w); return; }
        // 폴더 줄이 위쪽에 오게
        if (panelType.GetField("_bodyScroll", Any)!.GetValue(w) is System.Windows.Controls.ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset + Tile(Id(0))!.TranslatePoint(new Point(0, 0), sv).Y - 40);
            Pump(400);
        }
        var cell = groupCells[0];
        string key = (string)dropTag.GetProperty("Key")!.GetValue(cell.Tag)!;
        var grab = new Point(cell.ActualWidth * 0.5, cell.ActualHeight * 0.35);
        panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { cell, Activator.CreateInstance(dragItem, false, key, false), null, grab });
        var dpiScale = Monitors.GetPrimary().Scale;
        foreach (var (name, id) in new[] { ("other-folder", Id(1)), ("own-folder", Id(0)) })
        {
            var tile = Tile(id)!;
            var over = tile.TranslatePoint(new Point(tile.ActualWidth * 0.55, tile.ActualHeight * 0.45), w);
            panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { over });
            Pump(400);
            var drag = panelType.GetField("_drag", Any)!.GetValue(w)!;
            var target = drag.GetType().GetField("Target")!.GetValue(drag);
            double scale = tile.RenderTransform is ScaleTransform st ? st.ScaleX : 1;
            if (name == "other-folder")
                Check($"끌기 그림: 다른 폴더 칸 위 → 폴더 그림만 1.08배 ({scale:0.00})", Math.Abs(scale - 1.08) < 0.01);
            else
                Check($"끌기 그림: 자기 폴더 칸 위 → 강조 없음 (배율 {scale:0.00}, 놓을 곳 {(target is null ? "없음" : "있음")})", Math.Abs(scale - 1) < 0.01 && target is null);
            // 폴더 줄 둘레를 찍음 (판 창 안만)
            var topLeft = w.PointToScreen(new Point(0, 0));
            var origin = tile.TranslatePoint(new Point(0, 0), w);
            var rect = new Rect(topLeft.X / dpiScale + Math.Max(0, origin.X - 260), topLeft.Y / dpiScale + Math.Max(0, origin.Y - 60), 640, 300);
            var px = Grab(rect);
            int pw = (int)(rect.Width * dpiScale), ph = (int)(rect.Height * dpiScale);
            var bmp = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, px, pw * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(Path.Combine(outDir, $"drag-{name}.png"))) enc.Save(fs);
        }
        panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { false });
        Pump(500);
        close(w);
        Pump(400);
    }

    /// <summary>
    /// 폴더마다 끌기 따라오기 (2026-10-13 사용자: "업무" 폴더 안 아이콘만 거리를 두고 따라오는 것 같음):
    /// 폴더를 하나씩 펼치고 첫 앱 칸을 끌어 ① 잡는 순간 첫 프레임 오프셋 ② 스크립트 좌표로 옮길 때 ③ 프레임마다 커서 자리로 따라올 때 거리를 잼.
    /// 칸·아이콘 크기, 칸과 조상의 변환, 스크롤 자리도 같이 적음.
    /// </summary>
    /// <summary>
    /// 폴더 안 순서 바꾸기 (2026-10-14 사용자: 폴더 안에서 앱 순서가 안 바뀜) — 스크립트 끌기만 (진짜 마우스 없음, 시험 데이터 폴더):
    /// 첫 앱을 셋째·넷째 사이로 끌면 둘째·셋째가 한 칸 앞으로 비켜나고, 놓으면 그 순서로 저장·폴더 칸 미리보기도 같은 순서, 손댄 폴더가 됨.
    /// 취소하면 순서 그대로·비켜난 칸 원래대로.
    /// </summary>
    /// <summary>
    /// 둘러보기 새 방식 실제 창 (시험 데이터, 가짜 자리 — 독·상단바는 이 시험이 정한 사각형): 단계마다
    /// 카드 창이 강조 자리를 가리지 않는지, 그림 단계는 그림이 실렸는지, 넘기기·이전·닫기가 되는지. 진짜 입력 없음 (메서드 호출).
    /// </summary>
    private static void TourRun(AppServices services, Rect work, string outDir)
    {
        var asm = typeof(Mongdock.App).Assembly;
        var coach = asm.GetType("Mongdock.Views.CoachMarks")!;
        var monitor = Monitors.GetPrimary();
        var dock = new Rect(work.Left + work.Width / 2 - 220, work.Bottom - 78, 440, 70);
        var bar = new Rect(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, 32);
        var allApps = new Rect(dock.Left + 70, dock.Top + 9, 52, 52);
        Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> resolve = a => a switch
        {
            CoachAnchor.Dock => (dock, monitor),
            CoachAnchor.TopBar => (bar, monitor),
            CoachAnchor.AllApps => (allApps, monitor),
            _ => null, // 독 폴더·휴지통 없음 → 카드만 가운데
        };
        coach.GetMethod("Init")!.Invoke(null, new object?[] { services, resolve, null });
        services.Settings.Current.HideWindowsTaskbar = true;
        var build = coach.GetMethod("BuildTourPages")!;
        var pages = (System.Collections.IList)build.Invoke(null, null)!;
        string Title(object pg) => (string)pg.GetType().GetProperty("Title")!.GetValue(pg)!;
        string? Pics(object pg) => (string?)pg.GetType().GetProperty("Pictures")!.GetValue(pg);
        Check($"둘러보기: 단계 {pages.Count}장, 그림 단계 {pages.Cast<object>().Count(x => Pics(x) is not null)}개 (7 기대 — 0.5.1 검색), 작업 표시줄 카드 없음", pages.Cast<object>().Count(x => Pics(x) is not null) == 7 && pages.Cast<object>().All(x => !Title(x).Contains("작업 표시줄")));
        bool ended = false;
        object? reason = null;
        var listType = typeof(List<>).MakeGenericType(asm.GetType("Mongdock.Views.CoachPage")!);
        var playFunc = typeof(Func<>).MakeGenericType(listType);
        // Play(Func<List<CoachPage>>, Action<CoachEndReason>, bool) — 리플렉션으로 람다를 만들기 번거로워 BuildTourPages 를 그대로 넘김
        var endType = asm.GetType("Mongdock.Views.CoachEndReason")!;
        var onEnded = typeof(Action<>).MakeGenericType(endType);
        var endHandler = Delegate.CreateDelegate(onEnded, new EndBox(r => { ended = true; reason = r; }), typeof(EndBox).GetMethod("Invoke")!.MakeGenericMethod(endType));
        var buildDel = Delegate.CreateDelegate(playFunc, build);
        bool started = (bool)coach.GetMethod("Play")!.Invoke(null, new object?[] { buildDel, endHandler, true })!;
        Check("둘러보기: 시작됨", started);
        if (!started) return;
        Pump(800);
        var session = coach.GetField("_session", Any)!.GetValue(null)!;
        var st = session.GetType();
        Check($"둘러보기: 새 방식 세션 ({st.Name})", st.Name == "TourSession");
        int count = pages.Count;
        int firstPicture = pages.Cast<object>().ToList().FindIndex(x => Pics(x) is not null);
        var heights = new List<double>();
        FlickerDir = outDir;
        var flicker = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var card = (Window)st.GetField("_card", Any)!.GetValue(session)!;
            var hole = (Rect?)st.GetField("_hole", Any)!.GetValue(session);
            double pad = (double)card.GetType().GetProperty("ShadowPad")!.GetValue(card)!;
            CardPad = pad;
            var cardRect = new Rect(card.Left + pad, card.Top + pad, card.ActualWidth - pad * 2, card.ActualHeight - pad * 2);
            var tourCard = card.GetType().GetProperty("Card")!.GetValue(card)!;
            var frames = (System.Collections.ICollection)tourCard.GetType().GetField("_frames", Any)!.GetValue(tourCard)!;
            string title = Title(pages[i]!);
            bool pictureStep = Pics(pages[i]!) is not null;
            heights.Add(((FrameworkElement)tourCard).ActualHeight);
            Check($"둘러보기 {i + 1}/{count} \"{title}\": 카드가 강조 자리를 안 가림 (자리 {(hole is null ? "없음" : $"{hole.Value.Top:0}~{hole.Value.Bottom:0}")}, 카드 {cardRect.Top:0}~{cardRect.Bottom:0}), 그림 {frames.Count}장",
                (hole is null || !cardRect.IntersectsWith(hole.Value)) && (!pictureStep || frames.Count >= 2) && cardRect.Top >= work.Top - 1 && cardRect.Bottom <= work.Bottom + 1);
            if (i == firstPicture)
            {
                // 장 넘김: 1.8초, 마지막 장 2.5초, 직접 넘기면 이 단계는 멈춤, 다음 단계에서 다시 자동
                var ct = tourCard.GetType();
                T Get<T>(string name) => (T)ct.GetProperty(name, Any)!.GetValue(tourCard)!;
                Check($"장 넘김: 첫 장 간격 {Get<TimeSpan>("SlideIntervalForTest").TotalMilliseconds}ms (1400 기대), 자동 {Get<bool>("SlidingForTest")}", Get<TimeSpan>("SlideIntervalForTest").TotalMilliseconds == 1400 && Get<bool>("SlidingForTest"));
                ct.GetMethod("ShowFrameForTest", Any)!.Invoke(tourCard, new object[] { frames.Count - 1 });
                ct.GetMethod("StartSlides", Any)!.Invoke(tourCard, null);
                Check($"장 넘김: 마지막 장 간격 {Get<TimeSpan>("SlideIntervalForTest").TotalMilliseconds}ms (2000 기대)", Get<TimeSpan>("SlideIntervalForTest").TotalMilliseconds == 2000);
                ct.GetField("_slide", Any)!.GetValue(tourCard)!.GetType().GetMethod("Stop")!.Invoke(ct.GetField("_slide", Any)!.GetValue(tourCard), null);
                // 마지막 장 다음 = 처음 (자동 넘김 틱과 같은 코드)
                ct.GetMethod("ShowFrame", Any)!.Invoke(tourCard, new object[] { frames.Count, false });
                Check($"장 넘김: 마지막 다음은 처음 ({Get<int>("FrameForTest")})", Get<int>("FrameForTest") == 0);
                ct.GetMethod("StartSlides", Any)!.Invoke(tourCard, null);
                Check($"장 점: {Get<int>("FrameDotCountForTest")}개 (장 {frames.Count}개)", Get<int>("FrameDotCountForTest") == frames.Count);
                ct.GetMethod("ClickFrameDotForTest", Any)!.Invoke(tourCard, new object[] { 2 });
                Pump(300);
                Check($"장 점 누름: 3번째 장 ({Get<int>("FrameForTest") + 1}), 직접 넘김 {Get<bool>("ManualForTest")}, 자동 멈춤 {!Get<bool>("SlidingForTest")}", Get<int>("FrameForTest") == 2 && Get<bool>("ManualForTest") && !Get<bool>("SlidingForTest"));
                ct.GetMethod("StepFrame", Any)!.Invoke(tourCard, new object[] { -1 });
                Pump(300);
                Check($"‹: 2번째 장 ({Get<int>("FrameForTest") + 1})", Get<int>("FrameForTest") == 1);
                Pump(400);
                flicker.Add(Flicker(card, "장 넘김 2→3", () => ct.GetMethod("StepFrame", Any)!.Invoke(tourCard, new object[] { +1 })));
                flicker.Add(Flicker(card, "장 넘김 3→1", () => ct.GetMethod("StepFrame", Any)!.Invoke(tourCard, new object[] { +1 })));
                // 가만히 두고(자동 넘김 1.4초) 8초 동안 카드가 한 프레임 비는지 (카드 글자 영역 흰 바탕이 갑자기 어두워짐)
                st.GetMethod("Next", Any)!.Invoke(session, null);
                Pump(500);
                st.GetMethod("Prev", Any)!.Invoke(session, null);
                Pump(500);
                flicker.Add(CardBlank(card, 8000, "가만히 8초 (자동 넘김)"));
                flicker.Add(CardBlank(card, 9000, "직접 넘김 12번", () => { for (int r = 0; r < 12; r++) { ct.GetMethod("StepFrame", Any)!.Invoke(tourCard, new object[] { +1 }); Pump(700); } }));
                st.GetMethod("Next", Any)!.Invoke(session, null);
                Pump(500);
                Check($"다음 단계: 다시 자동 (직접 {Get<bool>("ManualForTest")}, 자동 {Get<bool>("SlidingForTest")})", !Get<bool>("ManualForTest") && Get<bool>("SlidingForTest"));
                st.GetMethod("Prev", Any)!.Invoke(session, null);
                Pump(500);
            }
            if (i == 2)
            {
                // 이전 → 다시 다음
                st.GetMethod("Prev", Any)!.Invoke(session, null);
                Pump(350);
                st.GetMethod("Next", Any)!.Invoke(session, null);
                Pump(350);
            }
            if (i < count - 1) flicker.Add(Flicker(card, $"단계 {i + 1}→{i + 2}", () => st.GetMethod("Next", Any)!.Invoke(session, null)));
        }
        Check($"카드 높이: 모든 단계 같음 ({string.Join(", ", heights.Select(h => h.ToString("0")))})", heights.Distinct().Count() == 1);
        foreach (var f in flicker) Check(f, !f.Contains("튐"));
        // 마지막에서 다음 = 끝
        st.GetMethod("Next", Any)!.Invoke(session, null);
        Pump(500);
        Check($"둘러보기: 마지막 [시작하기]로 끝남 ({reason})", ended && reason?.ToString() == "Completed");
        Check("둘러보기: 창이 남지 않음", Application.Current.Windows.OfType<Window>().All(w => !w.IsVisible || !(w.Title?.StartsWith("mongdock Tour") ?? false)));
        var dockState = asm.GetType("Mongdock.Services.DockState") ?? asm.GetType("Mongdock.ViewModels.DockState") ?? asm.GetTypes().First(t => t.Name == "DockState");
        var pinned = dockState.GetProperty("CoachPinned", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        Check($"둘러보기: 독 고정 풀림 ({pinned ?? "속성 없음"})", pinned is null or false);
    }

    /// <summary>
    /// --tourflicker (0.5.1): 가리키는 자리가 움직일 때 카드를 다시 만들지 않는지 + 카드 창이 한 프레임 비는지 오래 재기.
    /// 독 확대·아이콘 수 변화는 가짜 자리(resolve)의 사각형을 바꿔 흉내 (진짜 마우스·독 없음).
    /// 다시 만듦 = TourCard._frames 목록이 새 것으로 바뀜 (ShowCurrent → Card.Show).
    /// </summary>
    /// <summary>
    /// --panelreopen (#26): 실제 열기 길(Acquire)로 앱 모음 판을 3번 열고 닫으며 열 때마다 판 본문에 앱 칸이 그려졌는지.
    /// 두 번째부터는 숨겨 둔 판을 다시 씀(#14) — 0.6.4 에서 다시 쓴 판이 비어 보였음.
    /// </summary>
    private static void PanelReopen(AppServices services, Type panelType, Rect work)
    {
        var acquire = panelType.GetMethod("Acquire", Any)!;
        var body = panelType.GetField("_body", Any)!;
        for (int i = 0; i < 3; i++)
        {
            var args = new object?[] { services, Mongdock.ViewModels.UiTheme.Palette(services.Settings.Current), new Rect(work.Left + work.Width / 2 - 26, work.Bottom - 62, 52, 52), DockEdge.Bottom, Monitors.GetPrimary(), null };
            var w = (Window)acquire.Invoke(null, args)!;
            bool reused = (bool)args[5]!;
            if (!reused) w.Show();
            Pump(1500);
            var content = body.GetValue(w) as System.Windows.Controls.ContentControl;
            var root = content?.Content as FrameworkElement;
            int cells = root is null ? 0 : CountCells(root);
            Check($"앱 모음 판 {i + 1}번째 열기{(reused ? " (다시 씀)" : "")}: 본문 {(root is null ? "없음" : "있음")}, 칸 {cells}개", root is not null && cells > 0);
            w.GetType().GetMethod("CloseAnimated", Any)!.Invoke(w, null);
            Pump(800);
        }
        panelType.GetMethod("DropSpare", Any)!.Invoke(null, null);
    }

    private static int CountCells(DependencyObject root)
    {
        int n = 0;
        int c = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < c; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is System.Windows.Controls.Image) n++;
            n += CountCells(ch);
        }
        return n;
    }

    /// <summary>
    /// --introzoom (#14 반 해상도 나타나기 그림 확인): 진짜 앱 모음 판(라이트·다크)을 띄워 그림을 원래·반 해상도로 뜨고, 가운데에서 커지는
    /// 움직임의 55%·85% 크기로 그린 장면을 배율 125%(이 PC)·100%(흉내)로 만들어 왼쪽 위(검색 칸·첫 줄 아이콘)를 2배 확대해 나란히.
    /// 화면 배율은 바꾸지 않음(100% 는 그림 배율로 흉내). intro-{theme}-{dpi}.png.
    /// </summary>
    private static void IntroZoom(AppServices services, Type panelType, SettingsService settings, Rect work, string outDir)
    {
        var asm = typeof(Mongdock.App).Assembly;
        var dsp = asm.GetType("Mongdock.Views.DockStackPanel")!;
        var snap = dsp.GetMethod("SnapshotOf", Any)!;
        var sample = asm.GetType("Mongdock.Views.PanelIntro")!.GetMethod("Sample")!;
        var monitor = Monitors.GetPrimary();
        var icon = new Rect(work.Left + work.Width / 2 - 26, work.Bottom - 62, 52, 52);
        foreach (var theme in new[] { DockTheme.Light, DockTheme.Dark })
        {
            settings.Current.Dock.Theme = theme;
            Mongdock.ViewModels.UiTheme.Apply(settings.Current);
            var palette = Mongdock.ViewModels.UiTheme.Palette(settings.Current);
            var w = (Window)Activator.CreateInstance(panelType, Any, null, new object[] { services, palette, icon, DockEdge.Bottom, monitor }, null)!;
            w.Show();
            Pump(1500);
            var root = (FrameworkElement)w.Content;
            var final = new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
            foreach (double dpi in new[] { 1.25, 1.0 })
            {
                var full = (BitmapSource)snap.Invoke(null, new object?[] { root, w.Background, dpi, dpi })!;
                var half = (BitmapSource)snap.Invoke(null, new object?[] { root, w.Background, dpi * 0.5, dpi * 0.5 })!;
                const int cropW = 340, cropH = 200, zoom = 2;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 2 * cropW * zoom + 30, 3 * (cropH * zoom + 26)));
                    var ts = new[] { 0.5, 0.85, 1.0 };
                    for (int row = 0; row < ts.Length; row++)
                    {
                        var (r, _) = ((Rect, double))sample.Invoke(null, new object[] { "center", ts[row], final, icon })!;
                        for (int col = 0; col < 2; col++)
                        {
                            var pic = col == 0 ? full : half;
                            // 그림 창이 하는 대로: 그림을 r 크기로 늘려 그림 → 장치 픽셀로
                            int pw = (int)Math.Ceiling(r.Width * dpi), ph = (int)Math.Ceiling(r.Height * dpi);
                            var frame = new DrawingVisual();
                            using (var fdc = frame.RenderOpen())
                                fdc.DrawRectangle(new ImageBrush(pic) { Stretch = Stretch.Fill }, null, new Rect(0, 0, r.Width, r.Height));
                            var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                            rtb.Render(frame);
                            var crop = new CroppedBitmap(rtb, new Int32Rect(0, 0, Math.Min(cropW, pw), Math.Min(cropH, ph)));
                            double x = col * (cropW * zoom + 30), y = row * (cropH * zoom + 26);
                            dc.DrawText(new FormattedText($"{(col == 0 ? "원래 해상도" : "반 해상도")} · 움직임 {ts[row] * 100:0}% (판 {r.Width / final.Width * 100:0}% 크기) · 배율 {dpi * 100:0}%",
                                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1.0), new Point(x, y + 2));
                            var brush = new ImageBrush(crop) { Stretch = Stretch.Fill };
                            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
                            dc.DrawRectangle(brush, null, new Rect(x, y + 20, crop.PixelWidth * zoom, crop.PixelHeight * zoom));
                        }
                    }
                }
                var outBmp = new RenderTargetBitmap(2 * cropW * zoom + 30, 3 * (cropH * zoom + 26), 96, 96, PixelFormats.Pbgra32);
                outBmp.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(outBmp));
                string name = $"intro-{theme.ToString().ToLowerInvariant()}-{dpi * 100:0}.png";
                using (var fs = File.Create(Path.Combine(outDir, name))) enc.Save(fs);
                Check($"나타나기 그림 비교 {name}: 원래 {full.PixelWidth}×{full.PixelHeight} ({full.PixelWidth * full.PixelHeight * 4 / 1048576.0:0.0}MB) / 반 {half.PixelWidth}×{half.PixelHeight} ({half.PixelWidth * half.PixelHeight * 4 / 1048576.0:0.0}MB)", true);
            }
            w.Close();
            Pump(500);
        }
    }

    /// <summary>
    /// --memscenario (#14 메모리 C 전후 비교): 늘 같은 순서로 투명 창들을 띄우고 닫으며 단계마다 전용·그래픽 드라이버 MB 를 적음.
    /// 앱 모음 판 열고 닫기 5번 → 검색 창에 글자 하나씩(세 낱말) → 상단바 판 5가지 → 둘러보기 끝까지 → 20초 쉼.
    /// 실제 입력 없음(반사·코드로 조작). 결과는 memscenario.txt (고치기 전·후 같은 PC 에서 비교).
    /// </summary>
    private static void MemScenario(AppServices services, Type panelType, Func<Window> open, Action<Window> close, Rect work, string outDir)
    {
        var asm = typeof(Mongdock.App).Assembly;
        Mongdock.Views.LayeredWindowStats.Install();
        var lines = new List<string>();
        long startPriv = 0;
        void M(string what)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Pump(600);
            using var p = Process.GetCurrentProcess();
            long mb = 1024 * 1024, priv = p.PrivateMemorySize64;
            if (startPriv == 0) startPriv = priv;
            string line = $"{what,-34} 전용 {priv / mb,4}MB (시작 +{(priv - startPriv) / mb,3}) | 그래픽 드라이버 {Mongdock.Views.LayeredWindowStats.DriverMemoryBytes() / mb,4}MB | 작업 집합 {p.WorkingSet64 / mb,4}MB";
            lines.Add(line);
            Console.WriteLine("  " + line);
        }
        M("준비 끝 (판 미리 준비 뒤)");

        // 1) 앱 모음 판 열고 닫기 5번 (--panelreuse: 한 판을 숨겼다 다시 보이기 5번 — 다시 쓰기의 효과 미리 보기, #14)
        if (Environment.GetCommandLineArgs().Contains("--panelreuse"))
        {
            var w = open();
            w.Show();
            Pump(1200);
            for (int i = 0; i < 5; i++)
            {
                w.Hide();
                Pump(800);
                w.Show();
                Pump(1200);
            }
            close(w);
            Pump(800);
            M("앱 모음 판 한 창 숨김·보임 5번");
        }
        else
        {
            // 실제 열기 길(Acquire — 숨겨 둔 판이 있으면 다시 씀, #14). 첫/두 번째 열기 시간도 적음
            var acquire = panelType.GetMethod("Acquire", Any);
            for (int i = 0; i < 5; i++)
            {
                var clock = Stopwatch.StartNew();
                Window w;
                bool reused = false;
                if (acquire is not null)
                {
                    var args = new object?[] { services, Mongdock.ViewModels.UiTheme.Palette(services.Settings.Current), new Rect(work.Left + work.Width / 2 - 26 - 3 * 62, work.Bottom - 62, 52, 52), DockEdge.Bottom, Monitors.GetPrimary(), null };
                    w = (Window)acquire.Invoke(null, args)!;
                    reused = (bool)args[5]!;
                }
                else w = open();
                long frame = -1;
                if (w.GetType().GetEvent("FirstFrame") is { } ev) ev.AddEventHandler(w, new Action(() => frame = clock.ElapsedMilliseconds));
                if (!reused) w.Show();
                for (int k = 0; k < 60 && frame < 0; k++) Pump(25);
                lines.Add($"  앱 모음 판 {i + 1}번째 열기{(reused ? " (다시 씀)" : "")}: 첫 프레임 {frame}ms");
                Pump(1200);
                close(w);
                Pump(800);
            }
            M("앱 모음 판 열고 닫기 5번");
        }

        // 2) 검색 창: 글자를 하나씩 (결과 수가 바뀌며 창 높이가 바뀜)
        var spot = asm.GetType("Mongdock.Views.SpotlightWindow")!;
        spot.GetMethod("Toggle")!.Invoke(null, new object[] { services });
        Pump(800);
        var cur = spot.GetField("_current", Any)!.GetValue(null);
        if (cur is not null)
        {
            var box = (System.Windows.Controls.TextBox)spot.GetField("_box", Any)!.GetValue(cur)!;
            foreach (var word in new[] { "chrome", "note", "set" })
            {
                for (int k = 1; k <= word.Length; k++) { box.Text = word[..k]; Pump(350); }
                if (word == "chrome" && cur is Window sw)
                {
                    // 검색 창 모양 확인 (#14 높이 고정): 창 높이·결과가 잘리지 않는지 그림으로
                    var r = new Rect(sw.Left, sw.Top, sw.ActualWidth, sw.ActualHeight);
                    lines.Add($"  검색 창 {sw.ActualWidth:0}×{sw.ActualHeight:0} DIP, 크기 고정 {sw.SizeToContent == SizeToContent.Manual}");
                    SaveStrip(Path.Combine(outDir, "search-chrome.png"), new List<(long, byte[])> { (0, Grab(r)) }, (int)r.Width, (int)r.Height);
                }
                box.Text = "";
                Pump(300);
            }
            spot.GetMethod("CloseIfOpen")!.Invoke(null, null);
            Pump(800);
        }
        else Check("메모리 시나리오: 검색 창이 안 열림", false);
        M("검색 창 글자 입력 3낱말");

        // 3) 상단바 판 (제어 센터·달력·소리·Wi-Fi·블루투스)
        var monitor = Monitors.GetPrimary();
        var palette = Mongdock.ViewModels.UiTheme.Palette(services.Settings.Current);
        var sp = asm.GetType("Mongdock.Views.StatusPanelWindow")!;
        var kindT = asm.GetType("Mongdock.Views.StatusPanelKind")!;
        double barBottom = monitor.WorkArea.Top;
        foreach (var name in new[] { "ControlCenter", "Calendar", "Volume", "Wifi", "Bluetooth" })
        {
            var panel = (Window)Activator.CreateInstance(sp, Any, null, new object[] { services, Enum.Parse(kindT, name), palette, 0 }, null)!;
            var anchor = new Rect(monitor.WorkArea.Right - 200, monitor.WorkArea.Top - 28, 28, 24);
            sp.GetMethod("ShowBelow")!.Invoke(panel, new object[] { anchor, barBottom, monitor, true });
            Pump(1200);
            if (name == "ControlCenter")
            {
                var r = new Rect(panel.Left, panel.Top, panel.ActualWidth, panel.ActualHeight);
                lines.Add($"  제어 센터 창 {panel.ActualWidth:0}×{panel.ActualHeight:0} DIP");
                SaveStrip(Path.Combine(outDir, "controlcenter.png"), new List<(long, byte[])> { (0, Grab(r)) }, (int)r.Width, (int)r.Height);
            }
            sp.GetMethod("CloseImmediately")!.Invoke(panel, null);
            Pump(400);
        }
        M("상단바 판 5가지");

        // 4) 둘러보기 끝까지
        var coach = asm.GetType("Mongdock.Views.CoachMarks")!;
        var dockRect = new Rect(work.Left + work.Width / 2 - 220, work.Bottom - 78, 440, 70);
        var bar = new Rect(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, 32);
        Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> resolve = a => a switch
        {
            CoachAnchor.Dock => (dockRect, monitor),
            CoachAnchor.TopBar => (bar, monitor),
            CoachAnchor.AllApps => (new Rect(dockRect.Left + 70, dockRect.Top + 9, 52, 52), monitor),
            _ => null,
        };
        coach.GetMethod("Init")!.Invoke(null, new object?[] { services, resolve, null });
        var listType = typeof(List<>).MakeGenericType(asm.GetType("Mongdock.Views.CoachPage")!);
        var buildDel = Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(listType), coach.GetMethod("BuildTourPages")!);
        if ((bool)coach.GetMethod("Play")!.Invoke(null, new object?[] { buildDel, null, true })!)
        {
            Pump(2500);
            var session = coach.GetField("_session", Any)!.GetValue(null)!;
            var st = session.GetType();
            for (int i = 0; i < 12 && coach.GetField("_session", Any)!.GetValue(null) is not null; i++)
            {
                st.GetMethod("Next", Any)!.Invoke(session, null);
                Pump(1500);
            }
            if (coach.GetField("_session", Any)!.GetValue(null) is not null)
                st.GetMethod("Close", Any)!.Invoke(session, new object[] { false, Enum.Parse(asm.GetType("Mongdock.Views.CoachEndReason")!, "Completed") });
            Pump(4000); // 둘러보기 그림 정리(3초 뒤)
        }
        else Check("메모리 시나리오: 둘러보기 시작 안 됨", false);
        M("둘러보기 끝까지 (+그림 정리)");

        Pump(20000);
        M("20초 쉼");
        lines.Add(Mongdock.Views.LayeredWindowStats.Summary());
        Console.WriteLine("  " + Mongdock.Views.LayeredWindowStats.Summary());
        File.WriteAllLines(Path.Combine(outDir, "memscenario.txt"), lines);
        Check("메모리 시나리오: 끝까지 돎", true);
    }

    private static void TourFlicker(AppServices services, Rect work, string outDir)
    {
        var asm = typeof(Mongdock.App).Assembly;
        var coach = asm.GetType("Mongdock.Views.CoachMarks")!;
        var monitor = Monitors.GetPrimary();
        var baseDock = new Rect(work.Left + work.Width / 2 - 220, work.Bottom - 78, 440, 70);
        Rect? dock = baseDock;
        var bar = new Rect(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, 32);
        Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> resolve = a => a switch
        {
            CoachAnchor.Dock => dock is { } d ? (d, monitor) : null,
            CoachAnchor.TopBar => (bar, monitor),
            CoachAnchor.AllApps => dock is { } d2 ? (new Rect(d2.Left + 70, d2.Top + 9, 52, 52), monitor) : null,
            _ => null,
        };
        coach.GetMethod("Init")!.Invoke(null, new object?[] { services, resolve, null });
        var build = coach.GetMethod("BuildTourPages")!;
        var listType = typeof(List<>).MakeGenericType(asm.GetType("Mongdock.Views.CoachPage")!);
        var buildDel = Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(listType), build);
        bool started = (bool)coach.GetMethod("Play")!.Invoke(null, new object?[] { buildDel, null, true })!;
        Check("깜빡임 측정: 둘러보기 시작", started);
        if (!started) return;
        Pump(2500); // 독 단계(첫 장) + 나머지 장 채움
        var session = coach.GetField("_session", Any)!.GetValue(null)!;
        var st = session.GetType();
        var card = (Window)st.GetField("_card", Any)!.GetValue(session)!;
        var tourCard = card.GetType().GetProperty("Card")!.GetValue(card)!;
        var framesField = tourCard.GetType().GetField("_frames", Any)!;
        object Frames() => framesField.GetValue(tourCard)!;
        Rect? Hole() => (Rect?)st.GetField("_hole", Any)!.GetValue(session);
        FlickerDir = outDir;
        Check($"깜빡임 측정: 독 단계 그림 {((System.Collections.ICollection)Frames()).Count}장", ((System.Collections.ICollection)Frames()).Count >= 2);
        // 카드 창 방식 (#9): 일반 창(MONGDOCK_TOUR_CARD=plain)이면 그림자 여백 0
        CardPad = (double)card.GetType().GetProperty("ShadowPad")!.GetValue(card)!;
        Console.WriteLine($"  카드 창: {(CardPad == 0 ? "일반 창 (plain)" : "투명 창 (layered)")}, 그림자 여백 {CardPad}");
        {
            var shot = new Rect(card.Left - 24, card.Top - 24, card.ActualWidth + 48, card.ActualHeight + 48);
            SaveStrip(Path.Combine(outDir, $"tourcard-{(CardPad == 0 ? "plain" : "layered")}.png"), new List<(long, byte[])> { (0, Grab(shot)) }, (int)shot.Width, (int)shot.Height);
        }

        // A) 가만히 25초 (자동 장 넘김 1.4초) — 카드 창이 한 프레임 비는지
        var r = CardBlank(card, 25000, "가만히 25초");
        Check(r, !r.Contains("튐"));

        // B) 독 확대 흉내 8초: 0.3초마다 독 폭이 ±40 바뀜 (마우스를 독 위로 오가는 것)
        var before = Frames();
        int rebuilds = 0;
        r = CardBlank(card, 8000, "독 확대 흉내 8초", () =>
        {
            var last = Frames();
            for (int k = 0; k < 26; k++)
            {
                double grow = k % 2 == 0 ? 40 : 0;
                dock = new Rect(baseDock.Left - grow, baseDock.Top - grow / 2, baseDock.Width + grow * 2, baseDock.Height + grow / 2);
                Pump(300);
                if (!ReferenceEquals(Frames(), last)) { rebuilds++; last = Frames(); }
            }
        });
        Check(r, !r.Contains("튐"));
        Check($"독 확대 흉내: 카드 다시 만듦 {rebuilds}번 (0 기대), 구멍이 따라감 ({Hole()?.Width:0} = {dock?.Width:0})", rebuilds == 0 && Hole() is { } h1 && Math.Abs(h1.Width - dock!.Value.Width) < 1);

        // C) 아이콘 수 변화: 독 폭 +62 한 번
        before = Frames();
        dock = new Rect(baseDock.Left - 31, baseDock.Top, baseDock.Width + 62, baseDock.Height);
        Pump(900);
        Check($"아이콘 수 변화: 카드 그대로 {ReferenceEquals(before, Frames())}, 구멍 폭 {Hole()?.Width:0} (기대 {dock?.Width:0})", ReferenceEquals(before, Frames()) && Hole() is { } h2 && Math.Abs(h2.Width - dock!.Value.Width) < 1);

        // D) 자리가 없어짐(독 숨음) → 이때만 카드 다시 (구멍 없음·가운데)
        before = Frames();
        dock = null;
        Pump(900);
        Check($"자리 없어짐: 카드 다시 만듦 {!ReferenceEquals(before, Frames())}, 구멍 없음 {Hole() is null}", !ReferenceEquals(before, Frames()) && Hole() is null);
        dock = baseDock;
        Pump(900);

        // E) 단계 넘김 깜빡임 몇 번 (고친 빌드 기준)
        for (int i = 0; i < 3; i++)
        {
            var f = Flicker(card, $"측정 단계 {i + 1}→{i + 2}", () => st.GetMethod("Next", Any)!.Invoke(session, null));
            Check(f, !f.Contains("튐"));
            Pump(600);
        }
        st.GetMethod("Close", Any)!.Invoke(session, new object[] { false, Enum.Parse(asm.GetType("Mongdock.Views.CoachEndReason")!, "Completed") });
        Pump(500);
        Check("깜빡임 측정: 둘러보기 창이 남지 않음", Application.Current.Windows.OfType<Window>().All(w => !w.IsVisible || !(w.Title?.StartsWith("mongdock Tour") ?? false)));
    }

        /// <summary>
    /// 넘기는 동안 화면을 프레임마다 받아(Desktop Duplication — DWM 이 합성한 모든 프레임) 깜빡임 점검.
    /// 넘기기 전 300ms + 뒤 700ms. 어둡게 덮인 곳(카드 왼쪽·오른쪽 바깥 40px 상자 둘)은 전·후 값 범위에서 2 넘게 벗어나면 "튐",
    /// 그림 가운데(60px 상자)는 교차 흐림이라 전·후 사이 값은 정상, 범위에서 3 넘게 벗어나면 "튐".
    /// </summary>
    private static string FlickerDir = Path.GetTempPath();
    private static double CardPad = 28;
    private static bool FlickerDump;

    private static string Flicker(Window card, string label, Action act)
    {
        (int, int, int, int) Box(Point dip, int size)
        {
            var px = card.PointToScreen(dip);
            return ((int)px.X - size / 2, (int)px.Y - size / 2, size, size);
        }
        var boxes = new[]
        {
            Box(new Point(-160, card.ActualHeight / 2), 40),
            Box(new Point(card.ActualWidth + 160, card.ActualHeight / 2), 40),
            Box(new Point(card.ActualWidth / 2, CardPad + 90), 60), // 그림 영역 위·가운데·아래 (그림자 여백 CardPad)
            Box(new Point(card.ActualWidth / 2, CardPad + 175), 60),
            Box(new Point(card.ActualWidth * 0.4, CardPad + 290), 60),
        };
        var frames = new List<(long T, double[] V)>();
        string? error = null;
        bool stop = false, ready = false;
        var sw = Stopwatch.StartNew();
        var th = new System.Threading.Thread(() =>
        {
            try
            {
                using var dup = new DesktopDup();
                System.Threading.Volatile.Write(ref ready, true);
                while (!System.Threading.Volatile.Read(ref stop))
                    if (dup.Next(50, boxes) is { } v) lock (frames) frames.Add((sw.ElapsedMilliseconds, v));
            }
            catch (Exception ex) { error = ex.Message; System.Threading.Volatile.Write(ref ready, true); }
        }) { IsBackground = true };
        th.Start();
        while (!System.Threading.Volatile.Read(ref ready)) Pump(20);
        Pump(300);
        long at = sw.ElapsedMilliseconds;
        act();
        Pump(700);
        System.Threading.Volatile.Write(ref stop, true);
        th.Join();
        if (error is not null) return $"깜빡임 {label}: 화면 받기 실패 ({error}) 튐?";
        var before = frames.Where(x => x.T < at).ToList();
        var during = frames.Where(x => x.T >= at).ToList();
        if (before.Count == 0) return $"깜빡임 {label}: 넘기기 전 프레임이 없음 튐?";
        var last = during.Count > 0 ? during[^1].V : before[^1].V;
        double Worst(int k) => during.Count == 0 ? 0 : during.Max(x => Math.Max(x.V[k] - Math.Max(before[^1].V[k], last[k]), Math.Min(before[^1].V[k], last[k]) - x.V[k]));
        double dim = Math.Max(Worst(0), Worst(1)), pic = Math.Max(Worst(2), Math.Max(Worst(3), Worst(4)));
        double dimShift = Math.Max(Math.Abs(last[0] - before[^1].V[0]), Math.Abs(last[1] - before[^1].V[1]));
        bool bad = dim > 2 || pic > 3 || dimShift > 2;
        double picShift = new[] { 2, 3, 4 }.Max(k => Math.Abs(last[k] - before[^1].V[k]));
        if (bad || FlickerDump)
            File.WriteAllLines(Path.Combine(FlickerDir, $"flicker-{string.Concat(label.Where(char.IsLetterOrDigit))}.csv"),
                frames.Select(x => $"{x.T - at},{string.Join(",", x.V.Select(v => v.ToString("0.0")))}"));
        return $"깜빡임 {label}: 프레임 {during.Count}개, 어두운 곳 벗어남 {dim:0.0} (전후 차 {dimShift:0.0}), 그림 벗어남 {pic:0.0} (전후 차 {picShift:0.0}){(bad ? " — 튐" : "")}";
    }

    /// <summary>
    /// 카드 창이 한 프레임 비는(투명하게 그려지는) 횟수: 카드 안 빈 여백 세 곳(오른쪽 가장자리 안쪽, 아래 가장자리 안쪽, 왼쪽 아래)이
    /// 같은 프레임에 모두 중앙값에서 15 넘게 벗어나면 빈 프레임. (예전엔 한 곳만 봤는데 장마다 바뀌는 글자에 걸려 장 넘김마다 튐으로 잡힘 — #9)
    /// </summary>
    private static string CardBlank(Window card, int ms, string label, Action? during = null)
    {
        if (PresentationSource.FromVisual(card) is null) return $"카드 빔 {label}: 카드 창이 닫혀 다시 만들어짐 — 측정 못 함 튐?";
        // 창이 아니라 카드 요소 기준 (창 크기·그림자 여백과 상관없이 카드 안 여백)
        var inner = (FrameworkElement)card.GetType().GetProperty("Card")!.GetValue(card)!;
        (int, int, int, int) Box(double x, double y) { var px = inner.PointToScreen(new Point(x, y)); return ((int)px.X - 3, (int)px.Y - 3, 6, 6); }
        double w = inner.ActualWidth, h = inner.ActualHeight;
        var boxes = new[] { Box(w - 8, h / 2 + 40), Box(w / 2 + 40, h - 7), Box(48, h - 7) };
        Console.WriteLine($"    카드 요소 {w:0}x{h:0}, 창 {card.ActualWidth:0}x{card.ActualHeight:0}, 상자 {string.Join(" ", boxes.Select(b => $"({b.Item1},{b.Item2})"))}");
        var frames = new List<(long T, double[] V)>();
        bool stop = false;
        string? error = null;
        var sw = Stopwatch.StartNew();
        var th = new System.Threading.Thread(() =>
        {
            try
            {
                using var dup = new DesktopDup();
                while (!System.Threading.Volatile.Read(ref stop))
                    if (dup.Next(50, boxes) is { } v) lock (frames) frames.Add((sw.ElapsedMilliseconds, v));
            }
            catch (Exception ex) { error = ex.Message; }
        }) { IsBackground = true };
        th.Start();
        if (during is not null) { Pump(200); during(); } else Pump(ms);
        System.Threading.Volatile.Write(ref stop, true);
        th.Join();
        if (error is not null) return $"카드 빔 {label}: 화면 받기 실패 ({error}) 튐?";
        double Median(int k) { var o = frames.Select(x => x.V[k]).OrderBy(x => x).ToList(); return o.Count > 0 ? o[o.Count / 2] : 0; }
        var med = Enumerable.Range(0, boxes.Length).Select(Median).ToArray();
        var bad = frames.Where(x => Enumerable.Range(0, boxes.Length).All(k => Math.Abs(x.V[k] - med[k]) > 15)).ToList();
        int some = frames.Count(x => Enumerable.Range(0, boxes.Length).Any(k => Math.Abs(x.V[k] - med[k]) > 15));
        File.WriteAllLines(Path.Combine(FlickerDir, $"cardblank-{string.Concat(label.Where(char.IsLetterOrDigit))}.csv"), frames.Select(x => $"{x.T},{string.Join(",", x.V.Select(v => v.ToString("0.0")))}"));
        return $"카드 빔 {label}: 프레임 {frames.Count}개, 바탕 {string.Join("/", med.Select(m => m.ToString("0")))}, 빈 프레임 {bad.Count}개 ({string.Join(" ", bad.Select(x => $"{x.T}ms={string.Join("/", x.V.Select(v => v.ToString("0")))}"))}), 한 곳만 벗어남 {some - bad.Count}개{(bad.Count > 0 ? " — 튐" : "")}";
    }

    private sealed class EndBox
    {
        private readonly Action<object?> _done;
        public EndBox(Action<object?> done) => _done = done;
        public void Invoke<T>(T reason) => _done(reason);
    }

    private static void FolderOrder(Type panelType, Func<Window> open, Action<Window> close, SettingsService settings)
    {
        var w = open();
        w.Show();
        Pump(1200);
        var folders = ((System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!).Cast<object>().ToList();
        string Id(object f) => (string)f.GetType().GetProperty("Id")!.GetValue(f)!;
        List<string> Keys(object f) => ((System.Collections.IEnumerable)f.GetType().GetProperty("Apps")!.GetValue(f)!).Cast<object>().Select(a => (string)a.GetType().GetProperty("Key")!.GetValue(a)!).ToList();
        var folder = folders.FirstOrDefault(f => Keys(f).Count >= 4);
        if (folder is null) { Check("폴더 순서: 앱 4개 이상인 폴더가 없음", false); close(w); return; }
        string id = Id(folder);
        var before = Keys(folder);
        panelType.GetField("_expanded", Any)!.SetValue(w, id);
        panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
        Pump(600);
        var cells = ((System.Collections.IEnumerable)panelType.GetField("_groupCells", Any)!.GetValue(w)!).Cast<System.Windows.Controls.Border>().ToList();
        if (panelType.GetField("_bodyScroll", Any)!.GetValue(w) is System.Windows.Controls.ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset + cells[0].TranslatePoint(new Point(0, 0), sv).Y - 120);
            Pump(400);
        }
        var dragItem = panelType.GetNestedType("DragItem", BindingFlags.NonPublic)!;
        double appCell = (double)panelType.GetField("AppCell", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        double X(System.Windows.Controls.Border c) => c.RenderTransform is TranslateTransform t ? t.X : 0;
        void Drag(bool drop)
        {
            var c0 = cells[0];
            var grab = new Point(c0.ActualWidth / 2, c0.ActualHeight * 0.3);
            panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { c0, Activator.CreateInstance(dragItem, false, before[0], false), null, grab });
            // 셋째 칸 자리(가운데)에 올림 → 끄는 앱이 셋째 자리로, 둘째·셋째는 한 칸 앞으로
            var target = cells[2].TranslatePoint(new Point(cells[2].ActualWidth / 2, cells[2].ActualHeight / 2), w);
            panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { target });
            Pump(300);
            if (drop)
            {
                double x1 = X(cells[1]), x2 = X(cells[2]), x3 = X(cells[3]);
                Check($"폴더 순서: 끄는 동안 둘째·셋째가 한 칸 앞으로, 넷째는 그대로 (x {x1:0}, {x2:0}, {x3:0} / 칸 {appCell:0})",
                    Math.Abs(x1 + appCell) < 2 && Math.Abs(x2 + appCell) < 2 && Math.Abs(x3) < 2);
            }
            panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { drop });
            Pump(700);
        }
        // 취소: 순서 그대로, 비켜난 칸 원래대로
        Drag(drop: false);
        var afterCancel = Keys(((System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!).Cast<object>().First(f => Id(f) == id));
        Check($"폴더 순서: 취소하면 순서 그대로·칸 원래대로 (칸 x {X(cells[1]):0}, {X(cells[2]):0})", afterCancel.SequenceEqual(before) && Math.Abs(X(cells[1])) < 1 && Math.Abs(X(cells[2])) < 1);
        // 놓기: 첫 앱이 셋째 자리로
        Drag(drop: true);
        var expect = before.Skip(1).Take(2).Append(before[0]).Concat(before.Skip(3)).ToList();
        var now = Keys(((System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!).Cast<object>().First(f => Id(f) == id));
        Check($"폴더 순서: 놓으면 첫 앱이 셋째 자리로 (앞 4개 {string.Join(",", now.Take(4).Select(k => before.IndexOf(k)))} — 기대 1,2,0,3)", now.SequenceEqual(expect));
        var def = settings.Current.AllApps.Groups.FirstOrDefault(g => g.Id == id);
        Check($"폴더 순서: 설정에 저장·손댄 폴더 (Touched {def?.Touched}, 저장된 앞 3개 순서 맞음 {def?.Apps?.Take(3).SequenceEqual(expect.Take(3))})", def is { Touched: true } && def.Apps!.Take(expect.Count).SequenceEqual(expect));
        // 폴더 칸 미리보기 아이콘도 같은 순서 (폴더 칸 안 작은 아이콘의 그림 = 앱 순서대로 들어감 — 폴더 정의 순서로 확인)
        var tileKeys = Keys(((System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!).Cast<object>().First(f => Id(f) == id)).Take(9).ToList();
        Check("폴더 순서: 폴더 칸 미리보기도 새 순서", tileKeys.SequenceEqual(expect.Take(9)));
        // 자동 분류 새로 고침(하루 한 번)을 돌려도 순서 그대로
        var catalog = typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.AllAppsCatalog")!;
        var apps = catalog.GetMethod("Apps")!.Invoke(null, new object[] { false })!;
        typeof(Mongdock.App).Assembly.GetType("Mongdock.Services.AppFolders")!.GetMethod("RefreshAuto")!.Invoke(null, new object[] { settings.Current, apps, true });
        panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
        Pump(300);
        var afterAuto = Keys(((System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!).Cast<object>().First(f => Id(f) == id));
        Check("폴더 순서: 자동 분류를 다시 돌려도 순서 그대로", afterAuto.SequenceEqual(expect));

        // 폴더 줄 (같은 미리 비켜 주기 엔진): 첫 폴더를 셋째 폴더 자리로 끌면 둘째·셋째가 한 칸 앞으로 비켜나고, 강조는 없음, 놓으면 순서 바뀜
        panelType.GetField("_expanded", Any)!.SetValue(w, null);
        panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
        Pump(500);
        var dropTag = panelType.GetNestedType("DropTag", BindingFlags.NonPublic)!;
        string Kind(FrameworkElement e) => e.Tag is { } t && t.GetType() == dropTag ? (string)dropTag.GetProperty("Kind")!.GetValue(t)! : "";
        string TagKey(FrameworkElement e) => (string)dropTag.GetProperty("Key")!.GetValue(e.Tag)!;
        var tiles = Descendants((DependencyObject)w.Content).OfType<System.Windows.Controls.Border>().Where(b => Kind(b) == "group" && TagKey(b) != "other").ToList();
        var order0 = ((System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!).Cast<object>().Select(Id).ToList();
        if (tiles.Count >= 4)
        {
            if (panelType.GetField("_bodyScroll", Any)!.GetValue(w) is System.Windows.Controls.ScrollViewer sv2)
            {
                sv2.ScrollToVerticalOffset(sv2.VerticalOffset + tiles[0].TranslatePoint(new Point(0, 0), sv2).Y - 120);
                Pump(400);
            }
            string k0 = TagKey(tiles[0]);
            double slot = tiles[1].TranslatePoint(new Point(0, 0), tiles[0]).X; // 폴더 칸 한 자리 폭
            var at = tiles[2].TranslatePoint(new Point(tiles[2].ActualWidth / 2, tiles[2].ActualHeight * 0.4), w);
            panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { tiles[0], Activator.CreateInstance(dragItem, true, k0, false), null, new Point(tiles[0].ActualWidth / 2, 30) });
            panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { at });
            Pump(300);
            double t1 = X(tiles[1]), t2 = X(tiles[2]), t3 = X(tiles[3]);
            var drag = panelType.GetField("_drag", Any)!.GetValue(w)!;
            bool highlighted = drag.GetType().GetField("Highlight")!.GetValue(drag) is not null;
            Check($"폴더 줄: 끄는 동안 둘째·셋째 폴더가 한 자리 앞으로, 넷째 그대로, 강조 없음 (x {t1:0}, {t2:0}, {t3:0} / 자리 {slot:0}, 강조 {highlighted})",
                Math.Abs(t1 + slot) < 2 && Math.Abs(t2 + slot) < 2 && Math.Abs(t3) < 2 && !highlighted);
            Check($"폴더 줄: 원래 자리는 비움 (끄는 칸 불투명도 {tiles[0].Opacity:0.00})", tiles[0].Opacity < 0.01);
            panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { true });
            Pump(700);
            var order1 = ((System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!).Cast<object>().Select(Id).ToList();
            var want = order0.Skip(1).Take(2).Append(order0[0]).Concat(order0.Skip(3)).ToList();
            Check($"폴더 줄: 놓으면 첫 폴더가 셋째 자리로 (앞 4개 {string.Join(",", order1.Take(4).Select(x => order0.IndexOf(x)))} — 기대 1,2,0,3)", order1.Take(4).SequenceEqual(want.Take(4)));
        }
        else Check("폴더 줄: 폴더가 4개 이상 필요", false);

        // 즐겨찾기 (같은 엔진): 첫 앱을 셋째 자리로
        var catalogApps = ((System.Collections.IEnumerable)catalog.GetProperty("Cached")!.GetValue(null)!).Cast<object>().Take(4).Select(a => (string)a.GetType().GetProperty("Key")!.GetValue(a)!).ToList();
        settings.Current.AllApps.Favorites.Clear();
        settings.Current.AllApps.Favorites.AddRange(catalogApps);
        panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
        Pump(500);
        var favCells = ((System.Collections.IEnumerable)panelType.GetField("_favCells", Any)!.GetValue(w)!).Cast<System.Windows.Controls.Border>().ToList();
        if (favCells.Count >= 4)
        {
            if (panelType.GetField("_bodyScroll", Any)!.GetValue(w) is System.Windows.Controls.ScrollViewer sv3)
            {
                sv3.ScrollToVerticalOffset(sv3.VerticalOffset + favCells[0].TranslatePoint(new Point(0, 0), sv3).Y - 120);
                Pump(400);
            }
            string f0 = TagKey(favCells[0]);
            var at = favCells[2].TranslatePoint(new Point(favCells[2].ActualWidth / 2, favCells[2].ActualHeight * 0.4), w);
            panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { favCells[0], Activator.CreateInstance(dragItem, false, f0, false), null, new Point(favCells[0].ActualWidth / 2, 20) });
            panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { at });
            Pump(300);
            Check($"즐겨찾기: 끄는 동안 둘째·셋째가 한 칸 앞으로, 넷째 그대로 (x {X(favCells[1]):0}, {X(favCells[2]):0}, {X(favCells[3]):0})",
                Math.Abs(X(favCells[1]) + appCell) < 2 && Math.Abs(X(favCells[2]) + appCell) < 2 && Math.Abs(X(favCells[3])) < 2);
            panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { true });
            Pump(700);
            var favNow = settings.Current.AllApps.Favorites.Take(4).ToList();
            var favWant = catalogApps.Skip(1).Take(2).Append(catalogApps[0]).Append(catalogApps[3]).ToList();
            Check("즐겨찾기: 놓으면 첫 앱이 셋째 자리로", favNow.SequenceEqual(favWant));
        }
        else Check("즐겨찾기: 칸 4개 필요", false);
        close(w);
        Pump(400);
    }

    private static void DragFollowFolders(Type panelType, Func<Window> open, Action<Window> close)
    {
        var w = open();
        w.Show();
        Pump(1200);
        var folders = ((System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!).Cast<object>().ToList();
        var dragItem = panelType.GetNestedType("DragItem", BindingFlags.NonPublic)!;
        var dropTag = panelType.GetNestedType("DropTag", BindingFlags.NonPublic)!;
        var dpi = VisualTreeHelper.GetDpi(w);
        Results.Add($"폴더 {folders.Count}개, 판 배율 {panelType.GetProperty("UiScale", Any)!.GetValue(w):0.00}, 화면 배율 {dpi.DpiScaleX:0.00}");
        Results.Add("폴더 | 첫 프레임 오프셋 | 스크립트 좌표 최대 | 프레임마다 커서 최대 | 빨리 끌기 | 아이콘 | 변환·스크롤");
        foreach (var f in folders)
        {
            string id = (string)f.GetType().GetProperty("Id")!.GetValue(f)!, name = (string)f.GetType().GetProperty("Name")!.GetValue(f)!;
            panelType.GetField("_expanded", Any)!.SetValue(w, id);
            panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
            Pump(600);
            var cells = ((System.Collections.IEnumerable)panelType.GetField("_groupCells", Any)!.GetValue(w)!).Cast<System.Windows.Controls.Border>().ToList();
            if (cells.Count == 0) { Results.Add($"{name} | 칸 없음"); continue; }
            var sv = panelType.GetField("_bodyScroll", Any)!.GetValue(w) as System.Windows.Controls.ScrollViewer;
            if (sv is not null) { sv.ScrollToVerticalOffset(sv.VerticalOffset + cells[0].TranslatePoint(new Point(0, 0), sv).Y - 120); Pump(400); }
            var cell = cells[0];
            // 칸에서 판까지 변환 (RenderTransform·LayoutTransform·Clip 이 남아 있는지)
            var chain = new List<string>();
            for (DependencyObject? o = cell; o is not null && o != w; o = VisualTreeHelper.GetParent(o))
                if (o is FrameworkElement fe)
                {
                    if (fe.RenderTransform is { } rt && !rt.Value.IsIdentity) chain.Add($"{fe.GetType().Name}.Render({rt.Value.M11:0.00},{rt.Value.OffsetX:0},{rt.Value.OffsetY:0})");
                    if (fe.LayoutTransform is { } lt && !lt.Value.IsIdentity) chain.Add($"{fe.GetType().Name}.Layout({lt.Value.M11:0.00})");
                    if (fe.Opacity < 0.99) chain.Add($"{fe.GetType().Name}.Opacity({fe.Opacity:0.00})");
                }
            var img = Descendants(cell).OfType<System.Windows.Controls.Image>().FirstOrDefault();
            string iconInfo = img is null ? "없음" : $"{img.ActualWidth:0}DIP, 원본 {(img.Source as BitmapSource)?.PixelWidth}px";
            string key = (string)dropTag.GetProperty("Key")!.GetValue(cell.Tag)!;
            var grab = new Point(cell.ActualWidth * 0.5, cell.ActualHeight * 0.3);
            var start = cell.TranslatePoint(grab, w);
            var startPx = w.PointToScreen(start);
            // 실제 끌기와 같은 경로: 커서 자리(시험용)를 프레임마다 따라감
            var fake = startPx;
            panelType.GetProperty("CursorForTest", Any)!.SetValue(null, new Func<Point>(() => fake));
            panelType.GetProperty("FollowRealCursor", Any)!.SetValue(null, true);
            panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { cell, Activator.CreateInstance(dragItem, false, key, false), null, grab });
            var drag = panelType.GetField("_drag", Any)!.GetValue(w)!;
            var gh = new WindowInteropHelper((Window)drag.GetType().GetField("Ghost")!.GetValue(drag)!).Handle;
            var grabPx = (Point)drag.GetType().GetField("Grab")!.GetValue(drag)!;
            double Dist(Point cursorPx)
            {
                GetWindowRect(gh, out var r);
                double cx = r.Left + (12 + grabPx.X) * dpi.DpiScaleX, cy = r.Top + (12 + grabPx.Y) * dpi.DpiScaleY;
                return Math.Max(Math.Abs(cx - cursorPx.X), Math.Abs(cy - cursorPx.Y)) / dpi.DpiScaleX;
            }
            // 첫 프레임: 잡은 지점과 그림이 원래 칸 아이콘 자리에 그대로인지 (그림 왼쪽 위 - 아이콘 왼쪽 위)
            Pump(1);
            double first = Dist(startPx);
            string firstRel = "";
            if (img is not null)
            {
                GetWindowRect(gh, out var r0);
                var iconPx = w.PointToScreen(img.TranslatePoint(new Point(0, 0), w));
                firstRel = $" (그림-아이콘 {(r0.Left + 12 * dpi.DpiScaleX - iconPx.X) / dpi.DpiScaleX:0.0},{(r0.Top + 12 * dpi.DpiScaleY - iconPx.Y) / dpi.DpiScaleY:0.0})";
            }
            int frames = 0;
            EventHandler count = (_, _) => frames++;
            CompositionTarget.Rendering += count;
            double worstFollow = 0;
            for (int i = 1; i <= 30; i++)
            {
                double t = i <= 15 ? i : 30 - i;
                fake = w.PointToScreen(new Point(start.X + t * 9, start.Y - t * 2));
                int seen = frames;
                var waited = Stopwatch.StartNew();
                while (frames < seen + 2 && waited.ElapsedMilliseconds < 200) Pump(1);
                worstFollow = Math.Max(worstFollow, Dist(fake));
            }
            CompositionTarget.Rendering -= count;
            panelType.GetProperty("CursorForTest", Any)!.SetValue(null, null);
            panelType.GetProperty("FollowRealCursor", Any)!.SetValue(null, false);
            panelType.GetMethod("StopFollow", Any)!.Invoke(w, null);
            // 스크립트 좌표로 옮길 때 (마우스 이벤트 좌표 경로)
            double worst = 0;
            for (int i = 1; i <= 30; i++)
            {
                double t = i <= 15 ? i : 30 - i;
                var p = new Point(start.X + t * 8, start.Y + t * 3);
                panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { p });
                Pump(16);
                worst = Math.Max(worst, Dist(w.PointToScreen(p)));
            }
            panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { false });
            Pump(400);
            double press = PressDrag(panelType, w, cell, start, new Vector(60, 25));
            Results.Add($"{name} | {first:0.0}px{firstRel} | {worst:0.0}px | {worstFollow:0.0}px | 누르고 60px 빨리 끌기 {press:0.0}px | {iconInfo} | {(chain.Count == 0 ? "없음" : string.Join(" ", chain))}, 스크롤 {sv?.VerticalOffset:0}");
            Check($"끌기 따라오기 폴더 \"{name}\": 첫 프레임·이동 중·빨리 끌기 커서와 거리 ≤ 2px", first <= 2 && worst <= 2 && worstFollow <= 2 && press <= 2);
        }
        // 모든 앱: 판 왼쪽 위 근처 칸과 오른쪽 아래 근처 칸에서 빨리 끌기 (배율이 틀리면 왼쪽 위에서 멀수록 간격이 커짐)
        panelType.GetField("_expanded", Any)!.SetValue(w, null);
        panelType.GetField("_allOpen", Any)!.SetValue(w, true);
        panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
        Pump(600);
        var tagType = panelType.GetNestedType("DropTag", BindingFlags.NonPublic)!;
        var appCells = Descendants((DependencyObject)w.Content).OfType<System.Windows.Controls.Border>()
            .Where(b => b.Tag is { } t && t.GetType() == tagType && (string)tagType.GetProperty("Kind")!.GetValue(t)! == "app" && b.IsVisible && b.ActualWidth > 0).ToList();
        var visibleCells = appCells.Where(c => { var o = c.TranslatePoint(new Point(0, 0), w); return o.Y > 60 && o.Y + c.ActualHeight < w.ActualHeight - 60; }).ToList();
        if (visibleCells.Count > 0)
        {
            var tl = visibleCells.OrderBy(c => { var o = c.TranslatePoint(new Point(0, 0), w); return o.X + o.Y; }).First();
            var br = visibleCells.OrderByDescending(c => { var o = c.TranslatePoint(new Point(0, 0), w); return o.X + o.Y; }).First();
            foreach (var (where, c) in new[] { ("왼쪽 위", tl), ("오른쪽 아래", br) })
            {
                var at = c.TranslatePoint(new Point(c.ActualWidth * 0.5, c.ActualHeight * 0.3), w);
                double d = PressDrag(panelType, w, c, at, new Vector(where == "왼쪽 위" ? 60 : -60, where == "왼쪽 위" ? 25 : -25));
                Results.Add($"모든 앱 {where} (판 좌표 {at.X:0},{at.Y:0}) | 빨리 끌기 {d:0.0}px");
                Check($"끌기 따라오기 모든 앱 {where}: 누르고 60px 빨리 끌어도 커서와 거리 ≤ 2px", d <= 2);
            }
        }
        close(w);
        Pump(400);
    }

    /// <summary>
    /// 실제 마우스 경로 흉내: start 를 누르고(_press) move 만큼 빨리 움직인 뒤에 끌기가 시작될 때(8px·150ms 문턱),
    /// 그림의 누른 지점이 커서에 붙는지 (커서는 시험용 자리 — 진짜 마우스는 안 씀). 반환 = 다음 프레임의 거리(DIP).
    /// </summary>
    private static double PressDrag(Type panelType, Window w, System.Windows.Controls.Border cell, Point start, Vector move)
    {
        var dropTag = panelType.GetNestedType("DropTag", BindingFlags.NonPublic)!;
        var dragItem = panelType.GetNestedType("DragItem", BindingFlags.NonPublic)!;
        var dpi = VisualTreeHelper.GetDpi(w);
        panelType.GetField("_press", Any)!.SetValue(w, start);
        var now = start + move;
        var fake = w.PointToScreen(now);
        panelType.GetProperty("CursorForTest", Any)!.SetValue(null, new Func<Point>(() => fake));
        panelType.GetProperty("FollowRealCursor", Any)!.SetValue(null, true);
        string key = (string)dropTag.GetProperty("Key")!.GetValue(cell.Tag)!;
        var cellOrigin = cell.TranslatePoint(new Point(0, 0), w); // 끌기 전 칸 자리 (끌면 폴더 안 칸이 비켜나 움직일 수 있음)
        panelType.GetMethod("DragFromPress", Any)!.Invoke(w, new object?[] { cell, Activator.CreateInstance(dragItem, false, key, false), null, now });
        Pump(40);
        var drag = panelType.GetField("_drag", Any)!.GetValue(w);
        double dist = double.NaN;
        if (drag is not null)
        {
            var gh = new WindowInteropHelper((Window)drag.GetType().GetField("Ghost")!.GetValue(drag)!).Handle;
            var grabPx = (Point)drag.GetType().GetField("Grab")!.GetValue(drag)!;
            GetWindowRect(gh, out var r);
            double cx = r.Left + (12 + grabPx.X) * dpi.DpiScaleX, cy = r.Top + (12 + grabPx.Y) * dpi.DpiScaleY;
            double toCursor = Math.Max(Math.Abs(cx - fake.X), Math.Abs(cy - fake.Y)) / dpi.DpiScaleX; // 그림의 잡은 지점 ↔ 커서
            // 잡은 지점이 누른 자리인지 (예전처럼 끌기 시작 때 자리면 move 만큼 어긋남): 칸 왼쪽 위 + 아이콘 자리 + 잡은 자리(1.05배 들어올림 뺌)
            var off = (Vector)drag.GetType().GetField("IconOffset")!.GetValue(drag)!;
            var grabPanel = new Point(cellOrigin.X + off.X + grabPx.X / 1.05, cellOrigin.Y + off.Y + grabPx.Y / 1.05);
            double toPress = Math.Max(Math.Abs(grabPanel.X - start.X), Math.Abs(grabPanel.Y - start.Y));
            dist = Math.Max(toCursor, toPress);
            panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { false });
            Pump(400);
        }
        panelType.GetProperty("CursorForTest", Any)!.SetValue(null, null);
        panelType.GetProperty("FollowRealCursor", Any)!.SetValue(null, false);
        panelType.GetMethod("StopFollow", Any)!.Invoke(w, null);
        return dist;
    }

    private static void NewFeatures(AppServices services, SettingsService settings, Type panelType, Func<Window> open, Action<Window> close, string outDir, Rect work, WindowTracker tracker)
    {
        // A) 안 쓰는 앱 보기
        {
            settings.Current.AllApps.ShowSuggestions = true;
            var w = open();
            panelType.GetField("_allOpen", Any)!.SetValue(w, true);
            var filterType = panelType.GetNestedType("AllFilter", BindingFlags.NonPublic)!;
            w.Show();
            Pump(400);
            var sw = Stopwatch.StartNew();
            panelType.GetMethod("SetFilter", Any)!.Invoke(w, new[] { Enum.ToObject(filterType, 2) });
            object? rows = null;
            while (sw.ElapsedMilliseconds < 20000 && (rows = panelType.GetField("_unused", Any)!.GetValue(w)) is null) Pump(50);
            int count = rows is System.Collections.ICollection c ? c.Count : -1;
            Check($"안 쓰는 앱: 목록 계산됨 ({count}개, {sw.ElapsedMilliseconds}ms)", rows is not null);
            Check("안 쓰는 앱: 계산이 5초 안 (UI 안 멈춤 — 백그라운드)", sw.ElapsedMilliseconds < 5000);
            // 판을 계속 그리는지 (계산 중 UI 스레드가 막히지 않았는지): 계산 동안 펌프가 돌았으면 통과
            Pump(600);
            if (w.Content is FrameworkElement fe && fe.ActualWidth > 0)
            {
                var r = new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
                SaveStrip(Path.Combine(outDir, "unused-panel.png"), new List<(long, byte[])> { (0, Grab(r)) }, (int)r.Width, (int)r.Height);
            }
            if (rows is System.Collections.IEnumerable list)
                foreach (var row in list.Cast<object>().Take(8))
                {
                    var app = row.GetType().GetProperty("App")!.GetValue(row)!;
                    Console.WriteLine($"    {app.GetType().GetProperty("Name")!.GetValue(app)} | 마지막 {row.GetType().GetProperty("Last")!.GetValue(row) ?? "없음"} | {row.GetType().GetProperty("Bytes")!.GetValue(row) ?? "용량 모름"}");
                }
            close(w);
            Pump(400);
        }

        // A2) 판 움직임: 검색을 좁히면 빠지는 칸이 흐려지며 사라지고(180ms) 남는 칸이 미끄러짐 — 판 본문을 16ms 마다 찍어 변화가 0.1~0.3초 동안 이어지는지
        {
            var w = open();
            w.Show();
            Pump(900);
            var search = (System.Windows.Controls.TextBox)panelType.GetField("_search", Any)!.GetValue(w)!;
            search.Text = "c";
            Pump(600);
            var body = new Rect(w.Left + 20, w.Top + 70, Math.Min(600, w.ActualWidth - 40), 200);
            var frames = new List<(long Ms, byte[] Px)>();
            var sw = Stopwatch.StartNew();
            search.Text = "chr";
            while (sw.ElapsedMilliseconds < 420) { Pump(16); frames.Add((sw.ElapsedMilliseconds, Grab(body))); }
            var last = frames[^1].Px;
            // 마지막 모습과 다른 프레임이 몇 ms 까지 이어지는지 (바로 바뀌면 ~30ms, 흐려지며 사라지면 ~150ms 이상)
            long changingUntil = frames.Where(f => Diff(last, f.Px) > 0.01).Select(f => f.Ms).DefaultIfEmpty(0).Max();
            Check($"판 움직임: 검색을 좁히면 빠지는 칸이 천천히 사라짐 (변화가 {changingUntil}ms 까지)", changingUntil >= 100 && changingUntil <= 380);
            SaveStrip(Path.Combine(outDir, "frames-search-exit.png"), frames.Where((_, i) => i % 2 == 0).Take(12).ToList(), (int)body.Width, (int)body.Height);
            close(w);
            Pump(400);
        }

        // A3) 폴더 열기·닫기 (아이폰 폴더처럼): 판 본문을 16ms 마다 찍어 프레임 그림 + 열기 250ms·닫기 200ms 안에 끝나는지
        {
            var w = open();
            w.Show();
            Pump(1200);
            var folders = (System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!;
            string? id = folders.Count > 0 ? (string)folders[0]!.GetType().GetProperty("Id")!.GetValue(folders[0])! : null;
            if (id is null) Check("폴더: 시험할 폴더가 없음", false);
            else
            {
                var body = new Rect(w.Left + 10, w.Top + 60, Math.Min(900, w.ActualWidth - 20), w.ActualHeight - 70);
                List<(long, byte[])> Record(Action act, int ms)
                {
                    var frames = new List<(long, byte[])>();
                    var sw = Stopwatch.StartNew();
                    act();
                    while (sw.ElapsedMilliseconds < ms) { Pump(16); frames.Add((sw.ElapsedMilliseconds, Grab(body))); }
                    return frames;
                }
                long Settle(List<(long Ms, byte[] Px)> f) => f.Where(x => Diff(f[^1].Px, x.Px) > 0.005).Select(x => x.Ms).DefaultIfEmpty(0).Max();
                // 폴더 줄이 본문 위쪽에 오게 스크롤 (펼친 폴더가 판 아래로 잘리지 않게 — 판은 스스로 스크롤하지 않음)
                if (panelType.GetField("_bodyScroll", Any)!.GetValue(w) is System.Windows.Controls.ScrollViewer bodyScroll)
                {
                    var tileEl = Descendants((DependencyObject)w.Content).OfType<FrameworkElement>().FirstOrDefault(e => e.Uid == "tile:" + id);
                    if (tileEl is not null) bodyScroll.ScrollToVerticalOffset(bodyScroll.VerticalOffset + tileEl.TranslatePoint(new Point(0, 0), bodyScroll).Y - 20);
                    Pump(300);
                }
                var openFrames = Record(() =>
                {
                    panelType.GetField("_expanded", Any)!.SetValue(w, id);
                    panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
                }, 600);
                long openEnd = Settle(openFrames);
                Check($"폴더 열기: 300ms 안에 자리 잡음 ({openEnd}ms)", openEnd > 60 && openEnd <= 330);
                SaveStrip(Path.Combine(outDir, "frames-folder-open.png"), openFrames.Where((_, i) => i % 2 == 0).Take(12).ToList(), (int)body.Width, (int)body.Height);
                Pump(400);
                var closeFrames = Record(() => panelType.GetMethod("CloseFolderAnimated", Any)!.Invoke(w, null), 600);
                long closeEnd = Settle(closeFrames);
                Check($"폴더 닫기: 450ms 안에 자리 잡음 (접힘 200ms + 다시 그리기 + 남는 칸 미끄러짐 150ms, {closeEnd}ms)", closeEnd > 60 && closeEnd <= 450);
                SaveStrip(Path.Combine(outDir, "frames-folder-close.png"), closeFrames.Where((_, i) => i % 2 == 0).Take(12).ToList(), (int)body.Width, (int)body.Height);
                Check("폴더 닫기 뒤 펼친 폴더 없음", panelType.GetField("_expanded", Any)!.GetValue(w) is null);
            }
            close(w);
            Pump(400);
        }

        // A4) 끌기 시작: 잡는 순간 끄는 그림이 커서 아래(잡은 지점 그대로)에 바로 — 첫 프레임부터 커서에서 4px 안, 판 왼쪽 위에서 날아오지 않음
        {
            var w = open();
            panelType.GetField("_allOpen", Any)!.SetValue(w, true);
            w.Show();
            Pump(1200);
            panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
            Pump(400);
            var dropTag = panelType.GetNestedType("DropTag", BindingFlags.NonPublic)!;
            var dragItem = panelType.GetNestedType("DragItem", BindingFlags.NonPublic)!;
            var cells = Descendants((DependencyObject)w.Content).OfType<System.Windows.Controls.Border>()
                .Where(b => b.Tag is { } t && t.GetType() == dropTag && (string)dropTag.GetProperty("Kind")!.GetValue(t)! is "app" or "group" && b.ActualWidth > 0 && b.IsVisible)
                .Take(2).ToList();
            foreach (var cell in cells)
            {
                var tag = cell.Tag;
                bool group = (string)dropTag.GetProperty("Kind")!.GetValue(tag)! == "group";
                string key = (string)dropTag.GetProperty("Key")!.GetValue(tag)!;
                var grab = new Point(cell.ActualWidth * 0.3, cell.ActualHeight * 0.4); // 칸 안 잡은 자리
                var panelPt = cell.TranslatePoint(grab, w);
                var cursorPx = w.PointToScreen(panelPt);
                var item = Activator.CreateInstance(dragItem, group, key, false)!;
                panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { cell, item, null, grab });
                panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { panelPt });
                var drag = panelType.GetField("_drag", Any)!.GetValue(w)!;
                var ghost = (Window)drag.GetType().GetField("Ghost")!.GetValue(drag)!;
                var gh = new WindowInteropHelper(ghost).Handle;
                var grabPx = (Point)drag.GetType().GetField("Grab")!.GetValue(drag)!;
                var dpi = VisualTreeHelper.GetDpi(w);
                double worst = 0;
                var log = new List<string>();
                for (int f = 0; f < 12; f++)
                {
                    Pump(f == 0 ? 1 : 16);
                    GetWindowRect(gh, out var r);
                    // 커서가 있어야 할 자리 = 그림 창 왼쪽 위 + (그림자 여백 12 + 잡은 자리) × 배율
                    double cx = r.Left + (12 + grabPx.X) * dpi.DpiScaleX, cy = r.Top + (12 + grabPx.Y) * dpi.DpiScaleY;
                    double off = Math.Max(Math.Abs(cx - cursorPx.X), Math.Abs(cy - cursorPx.Y)) / dpi.DpiScaleX;
                    worst = Math.Max(worst, off);
                    if (f < 3) log.Add($"{off:0.0}");
                }
                Check($"끌기 시작({(group ? "폴더 칸" : "앱 칸")}): 첫 프레임부터 그림이 커서에서 4px 안 (가장 먼 {worst:0.0}px, 처음 {string.Join("·", log)})", worst <= 4);
                panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { false });
                Pump(500);
            }
            if (cells.Count == 0) Check("끌기 시작: 끌 칸을 찾지 못함", false);
            // (진짜 마우스 시험은 뺌 — 누른 자리가 판 밖이면 사용자 창을 누를 수 있어서, 2026-10-13)

            close(w);
            Pump(400);
        }

        // A5) 끌어 놓기 (2026-10-13 사용자: 놓을 때 아이콘이 판 왼쪽 위에서 그 자리로 날아감) — 놓은 직후 프레임마다 미끄러지는 칸의 출발점이
        //     판 왼쪽 위(본문 0,0 근처)가 아닌지, 결과가 맞게 저장됐는지. 모든 앱→즐겨찾기 / 즐겨찾기 순서 / 앱→폴더 칸 / 폴더 안→즐겨찾기 / 밖→펼친 폴더
        {
            var w = open();
            panelType.GetField("_allOpen", Any)!.SetValue(w, true);
            w.Show();
            Pump(1200);
            panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
            Pump(500);
            var dropTag = panelType.GetNestedType("DropTag", BindingFlags.NonPublic)!;
            var dragItem = panelType.GetNestedType("DragItem", BindingFlags.NonPublic)!;
            string Kind(FrameworkElement e) => e.Tag is { } t && t.GetType() == dropTag ? (string)dropTag.GetProperty("Kind")!.GetValue(t)! : "";
            string KeyOf(FrameworkElement e) => (string)dropTag.GetProperty("Key")!.GetValue(e.Tag)!;
            List<FrameworkElement> Tagged(string kind) => Descendants((DependencyObject)w.Content).OfType<FrameworkElement>().Where(e => Kind(e) == kind && e.IsVisible && e.ActualWidth > 0).ToList();
            var view = (UIElement)panelType.GetProperty("View", Any)!.GetValue(w)!;
            void Drop(string name, FrameworkElement from, FrameworkElement to, Func<bool> ok)
            {
                bool group = Kind(from) == "group";
                var grab = new Point(from.ActualWidth / 2, from.ActualHeight / 2);
                var target = to.TranslatePoint(new Point(to.ActualWidth / 2, to.ActualHeight / 2), w);
                panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { from, Activator.CreateInstance(dragItem, group, KeyOf(from), false), null, grab });
                panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { target });
                Pump(200);
                panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { target });
                panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { true });
                // 끄는 그림이 자리로 가고(150ms) 저장·다시 그림 → 그 뒤 프레임마다 미끄러지는 칸 출발점 확인
                double worstStart = double.MaxValue, maxOffset = 0;
                string? bad = null;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 600)
                {
                    Pump(16);
                    foreach (var el in Descendants((DependencyObject)w.Content).OfType<FrameworkElement>().Where(e => Kind(e) != "" && e.IsVisible && e.RenderTransform is TranslateTransform))
                    {
                        var tt = (TranslateTransform)el.RenderTransform;
                        if (Math.Abs(tt.X) + Math.Abs(tt.Y) < 2) continue;
                        var now = el.TranslatePoint(new Point(0, 0), view); // 지금 그려진 자리 (이동 포함)
                        var final = new Point(now.X - tt.X, now.Y - tt.Y);
                        maxOffset = Math.Max(maxOffset, Math.Abs(tt.X) + Math.Abs(tt.Y));
                        double fromOrigin = Math.Abs(now.X) + Math.Abs(now.Y);
                        if (fromOrigin < 40 && Math.Abs(final.X) + Math.Abs(final.Y) > 80) { bad = $"{Kind(el)} 칸이 ({now.X:0},{now.Y:0})에서 출발"; }
                        worstStart = Math.Min(worstStart, fromOrigin);
                    }
                }
                Check($"놓기 {name}: 판 왼쪽 위에서 날아오는 칸 없음 ({bad ?? $"가장 큰 미끄러짐 {maxOffset:0}px"})", bad is null);
                Check($"놓기 {name}: 결과 저장됨", ok());
                Pump(300);
            }
            var settings0 = settings.Current.AllApps;
            // 1) 모든 앱 → 즐겨찾기 (빈 즐겨찾기 칸)
            var appCells = Tagged("app");
            var favSlot = Tagged("fav").FirstOrDefault();
            if (appCells.Count >= 3 && favSlot is not null)
            {
                string k1 = KeyOf(appCells[0]);
                Drop("모든 앱→즐겨찾기", appCells[0], favSlot, () => settings0.Favorites.Contains(k1));
                // 두 번째도 넣어 순서 바꾸기 시험 준비
                var fav2 = Tagged("fav").FirstOrDefault() ?? Tagged("favapp").LastOrDefault();
                string k2 = KeyOf(Tagged("app")[1]);
                if (fav2 is not null) Drop("모든 앱→즐겨찾기 (둘째)", Tagged("app")[1], fav2, () => settings0.Favorites.Contains(k2));
                // 2) 즐겨찾기 순서 바꾸기: 첫째를 둘째 자리 뒤로
                var favs = Tagged("favapp");
                if (favs.Count >= 2)
                {
                    string first = KeyOf(favs[0]);
                    Drop("즐겨찾기 순서", favs[0], favs[^1], () => settings0.Favorites.IndexOf(first) > 0);
                }
            }
            else Check("놓기: 모든 앱 칸이나 즐겨찾기 자리를 찾지 못함", false);
            // 2-2) 즐겨찾기 순서 바꾸기 5번 — 놓은 뒤 판 전체를 프레임으로 찍어 판 왼쪽 위로 무언가 지나가는지 (사용자: "즐겨찾기 순서 바꿀 때 왼쪽 위에서 날아옴")
            {
                var extra = Tagged("app").Skip(2).FirstOrDefault();
                var slot = Tagged("favapp").LastOrDefault();
                if (extra is not null && slot is not null) Drop("모든 앱→즐겨찾기 (셋째)", extra, slot, () => true);
                for (int round = 1; round <= 5; round++)
                {
                    var favs = Tagged("favapp");
                    if (favs.Count < 3) { Check("즐겨찾기 순서 5번: 즐겨찾기가 3개 안 됨", false); break; }
                    var from = round % 2 == 1 ? favs[0] : favs[^1];
                    var to = round % 2 == 1 ? favs[^1] : favs[0];
                    var grab = new Point(from.ActualWidth / 2, from.ActualHeight / 2);
                    var target = to.TranslatePoint(new Point(to.ActualWidth * (round % 2 == 1 ? 0.8 : 0.2), to.ActualHeight / 2), w);
                    panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { from, Activator.CreateInstance(dragItem, false, KeyOf(from), false), null, grab });
                    panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { target });
                    Pump(200);
                    panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { target });
                    var whole = new Rect(w.Left, w.Top, w.ActualWidth, Math.Min(w.ActualHeight, 420));
                    var corner = new Rect(w.Left + 4, w.Top + 4, 200, 120);
                    var before = Grab(corner);
                    var frames = new List<(long, byte[])>();
                    var cornerFrames = new List<byte[]>();
                    var sw = Stopwatch.StartNew();
                    panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { true });
                    while (sw.ElapsedMilliseconds < 500)
                    {
                        Pump(16);
                        cornerFrames.Add(Grab(corner));
                        if (frames.Count < 16) frames.Add((sw.ElapsedMilliseconds, Grab(whole)));
                    }
                    var after = cornerFrames[^1];
                    int passing = cornerFrames.Count(f => Diff(before, f) > 0.02 && Diff(after, f) > 0.02);
                    SaveStrip(Path.Combine(outDir, $"frames-fav-order-{round}.png"), frames, (int)whole.Width, (int)whole.Height);
                    Check($"즐겨찾기 순서 {round}번째: 판 왼쪽 위로 지나가는 것 없음 (지나간 프레임 {passing}개)", passing == 0);
                    Pump(300);
                }
            }
            // 3) 앱 → 폴더 칸
            var groups = Tagged("group");
            var someApp = Tagged("app").Skip(5).FirstOrDefault();
            if (groups.Count > 0 && someApp is not null)
            {
                string gid = KeyOf(groups[0]), key = KeyOf(someApp);
                Drop("앱→폴더 칸", someApp, groups[0], () => true);
            }
            // 4) 폴더 펼치고: 폴더 안 → 즐겨찾기, 밖 → 펼친 폴더
            if (groups.Count > 0)
            {
                panelType.GetField("_expanded", Any)!.SetValue(w, KeyOf(groups[0]));
                panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
                Pump(500);
                var inner = Descendants((DependencyObject)w.Content).OfType<FrameworkElement>()
                    .FirstOrDefault(e => Kind(e) == "app" && e.IsVisible && ((System.Collections.IList)panelType.GetField("_groupCells", Any)!.GetValue(w)!).Contains(e));
                var favTarget = Tagged("favapp").LastOrDefault() ?? Tagged("fav").FirstOrDefault();
                if (inner is not null && favTarget is not null)
                {
                    string ik = KeyOf(inner);
                    Drop("폴더 안→즐겨찾기", inner, favTarget, () => settings0.Favorites.Contains(ik));
                }
                var grid = Descendants((DependencyObject)w.Content).OfType<FrameworkElement>().FirstOrDefault(e => Kind(e) == "groupgrid" && e.IsVisible);
                var outside = Tagged("app").LastOrDefault(e => !((System.Collections.IList)panelType.GetField("_groupCells", Any)!.GetValue(w)!).Contains(e));
                if (grid is not null && outside is not null) Drop("밖→펼친 폴더", outside, grid, () => true);
            }
            close(w);
            Pump(400);
        }

        // A6) 폴더에 앱 넣는 길: 펼친 폴더 [+] → 앱 고르기 → 넣기 / [+ 새 폴더] → 이름 + 고르기 → 만들기 / 끌 때 폴더 칸 1.08배
        {
            var w = open();
            w.Show();
            Pump(1200);
            var folders = (System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!;
            if (folders.Count == 0) Check("앱 넣기: 시험할 폴더가 없음", false);
            else
            {
                string id = (string)folders[0]!.GetType().GetProperty("Id")!.GetValue(folders[0])!;
                panelType.GetField("_expanded", Any)!.SetValue(w, id);
                panelType.GetMethod("Rebuild", Any)!.Invoke(w, null);
                Pump(400);
                var uids = Descendants((DependencyObject)w.Content).OfType<FrameworkElement>().Select(e => e.Uid).Where(u => u.Length > 0).Distinct().Take(12);
                Console.WriteLine($"    폴더 {folders.Count}개, 펼친 {panelType.GetField("_expanded", Any)!.GetValue(w)}, 보이는 표시: {string.Join(" ", uids)}");
                bool plus = Descendants((DependencyObject)w.Content).OfType<FrameworkElement>().Any(e => e.Uid == "m:addapps:" + id && e.IsVisible);
                Check("앱 넣기: 펼친 폴더 마지막에 [+] 칸", plus);
                // 고르기 카드
                panelType.GetMethod("OpenAddApps", Any)!.Invoke(w, new object?[] { id });
                Pump(500);
                var full = new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
                SaveStrip(Path.Combine(outDir, "addapps-picker.png"), new List<(long, byte[])> { (0, Grab(full)) }, (int)full.Width, (int)full.Height);
                var st = panelType.GetField("_adding", Any)!.GetValue(w)!;
                var chosen = (HashSet<string>)st.GetType().GetField("Chosen")!.GetValue(st)!;
                var apps = ((System.Collections.IEnumerable)panelType.GetField("_apps", Any)!.GetValue(w)!).Cast<object>().ToList();
                var inFolder = ((System.Collections.IEnumerable)folders[0]!.GetType().GetProperty("Apps")!.GetValue(folders[0])!).Cast<object>().Select(a => (string)a.GetType().GetProperty("Key")!.GetValue(a)!).ToHashSet();
                var pick = apps.Select(a => (string)a.GetType().GetProperty("Key")!.GetValue(a)!).Where(k => !inFolder.Contains(k)).Take(2).ToList();
                foreach (var k in pick) chosen.Add(k);
                panelType.GetMethod("ApplyAddApps", Any)!.Invoke(w, new object[] { st, inFolder });
                Pump(500);
                var after = (System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!;
                var f0 = after.Cast<object>().First(f => (string)f.GetType().GetProperty("Id")!.GetValue(f)! == id);
                var keysNow = ((System.Collections.IEnumerable)f0.GetType().GetProperty("Apps")!.GetValue(f0)!).Cast<object>().Select(a => (string)a.GetType().GetProperty("Key")!.GetValue(a)!).ToHashSet();
                Check($"앱 넣기: 고른 앱 {pick.Count}개가 폴더에 들어감", pick.All(keysNow.Contains));
                // 새 폴더
                panelType.GetMethod("OpenAddApps", Any)!.Invoke(w, new object?[] { null });
                Pump(500);
                SaveStrip(Path.Combine(outDir, "addapps-newfolder.png"), new List<(long, byte[])> { (0, Grab(full)) }, (int)full.Width, (int)full.Height);
                var st2 = panelType.GetField("_adding", Any)!.GetValue(w)!;
                st2.GetType().GetField("Name")!.SetValue(st2, "시험 폴더");
                var chosen2 = (HashSet<string>)st2.GetType().GetField("Chosen")!.GetValue(st2)!;
                var pick2 = apps.Select(a => (string)a.GetType().GetProperty("Key")!.GetValue(a)!).Skip(10).Take(3).ToList();
                foreach (var k in pick2) chosen2.Add(k);
                panelType.GetMethod("ApplyAddApps", Any)!.Invoke(w, new object[] { st2, new HashSet<string>(StringComparer.OrdinalIgnoreCase) });
                Pump(500);
                var made = ((System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!).Cast<object>()
                    .FirstOrDefault(f => (string)f.GetType().GetProperty("Name")!.GetValue(f)! == "시험 폴더");
                int madeCount = made is null ? -1 : ((System.Collections.ICollection)made.GetType().GetProperty("Apps")!.GetValue(made)!).Count;
                Check($"새 폴더: 이름 그대로 만들어지고 고른 앱 {pick2.Count}개가 들어감 ({madeCount}개)", madeCount == pick2.Count);
                // 끌 때 폴더 칸 위: 1.08배
                var dropTag = panelType.GetNestedType("DropTag", BindingFlags.NonPublic)!;
                var dragItem = panelType.GetNestedType("DragItem", BindingFlags.NonPublic)!;
                string Kind(FrameworkElement e) => e.Tag is { } t && t.GetType() == dropTag ? (string)dropTag.GetProperty("Kind")!.GetValue(t)! : "";
                var appCell = Descendants((DependencyObject)w.Content).OfType<System.Windows.Controls.Border>().FirstOrDefault(e => Kind(e) is "app" or "favapp" && e.IsVisible);
                var groupCell = Descendants((DependencyObject)w.Content).OfType<System.Windows.Controls.Border>().LastOrDefault(e => Kind(e) == "group" && e.IsVisible);
                if (appCell is not null && groupCell is not null)
                {
                    panelType.GetMethod("BeginDrag", Any)!.Invoke(w, new object?[] { appCell, Activator.CreateInstance(dragItem, false, (string)dropTag.GetProperty("Key")!.GetValue(appCell.Tag)!, false), null, new Point(10, 10) });
                    var over = groupCell.TranslatePoint(new Point(groupCell.ActualWidth / 2, groupCell.ActualHeight / 2), w);
                    panelType.GetMethod("UpdateDrag", Any)!.Invoke(w, new object[] { over });
                    Pump(250);
                    double sc = groupCell.RenderTransform is ScaleTransform stt ? stt.ScaleX : 1;
                    Check($"끌기: 폴더 칸 위에서 1.08배 + 진한 테두리 ({sc:0.00}배)", Math.Abs(sc - 1.08) < 0.01);
                    panelType.GetMethod("EndDrag", Any)!.Invoke(w, new object[] { false });
                    Pump(500);
                }
            }
            close(w);
            Pump(400);
        }

        // A7) 앱 고르기 키보드 (스크립트 키 — 진짜 키 입력 없음): 검색 칸 ↓ → 첫 줄, ↓/→ 이동, Space 체크, Enter 넣기 + 몽독 포커스 테두리(점선 없음)
        {
            typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.FocusRing")!.GetMethod("Init", Any)!.Invoke(null, null);
            var w = open();
            w.Show();
            w.Activate();
            Pump(1200);
            var folders = (System.Collections.IList)panelType.GetField("_folders", Any)!.GetValue(w)!;
            if (folders.Count > 0)
            {
                string id = (string)folders[0]!.GetType().GetProperty("Id")!.GetValue(folders[0])!;
                panelType.GetMethod("OpenAddApps", Any)!.Invoke(w, new object?[] { id });
                Pump(600);
                void Key(System.Windows.Input.Key k)
                {
                    var target = System.Windows.Input.Keyboard.FocusedElement as IInputElement ?? w;
                    var src = PresentationSource.FromVisual(w)!;
                    // PreviewKeyDown 만 — 안 막히면 WPF 가 KeyDown 으로 이어 보냄 (둘 다 보내면 두 번 눌린 것이 됨)
                    System.Windows.Input.InputManager.Current.ProcessInput(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    Pump(120);
                }
                var st = panelType.GetField("_adding", Any)!.GetValue(w)!;
                var chosen = (HashSet<string>)st.GetType().GetField("Chosen")!.GetValue(st)!;
                Key(System.Windows.Input.Key.Down);
                var f1 = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                Key(System.Windows.Input.Key.Down);
                var f2 = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                Check($"키보드: 검색 칸 ↓ 로 첫 줄, ↓ 로 다음 줄 ({f1?.GetType().Name}→{f2?.GetType().Name}, 다른 줄 {f1 != f2})", f1 is System.Windows.Controls.Border && f2 is System.Windows.Controls.Border && f1 != f2);
                var layer = f2 is null ? null : System.Windows.Documents.AdornerLayer.GetAdornerLayer(f2);
                bool ring = layer?.GetAdorners(f2!)?.Any(a => a.GetType().Name == "RingAdorner") == true;
                Check("키보드: 줄에 몽독 포커스 테두리 (점선 아님)", ring);
                if (f2 is not null)
                {
                    Pump(200);
                    var (dark, lilac) = EdgePixels(f2);
                    Check($"키보드: 실제 화면 픽셀 — 줄 가장자리에 검은 점선 없음 (어두운 점 {dark}개), 연보라 테두리 있음 ({lilac}개)", dark == 0 && lilac > 20);
                    Check($"키보드: WPF 점선 장식 없음", !HasDotted(f2));
                }
                var full = new Rect(w.Left, w.Top, w.ActualWidth, Math.Min(w.ActualHeight, 360));
                SaveStrip(Path.Combine(outDir, "addapps-keyboard-focus.png"), new List<(long, byte[])> { (0, Grab(full)) }, (int)full.Width, (int)full.Height);
                Key(System.Windows.Input.Key.Space);
                Check($"키보드: Space 로 체크 ({chosen.Count}개)", chosen.Count == 1);
                Key(System.Windows.Input.Key.Enter);
                Check("키보드: Enter 로 넣기 (고르기 카드 닫힘)", panelType.GetField("_adding", Any)!.GetValue(w) is null);
            }
            close(w);
            Pump(400);
        }

        // A8) 판·설정·신고·루틴 편집: 스크립트 Tab 30번 — 포커스 간 요소마다 WPF 점선 장식이 붙는지
        {
            var asm = typeof(Mongdock.App).Assembly;
            var wins = new List<(string Name, Func<Window> Make)>
            {
                ("앱 모음 판", () => open()),
                ("설정", () => (Window)asm.GetType("Mongdock.Views.SettingsWindow")!.GetConstructors(Any)[0].Invoke(new object[] { services })),
                ("신고", () => new Mongdock.Views.ReportWindow(settings.Current)),
                ("루틴 편집", () =>
                {
                    var r = new RoutineDef { Id = "live", Name = "시험" };
                    return (Window)asm.GetType("Mongdock.Views.RoutineEditorWindow")!.GetConstructors(Any)[0].Invoke(new object?[] { services, null, r, false });
                }),
            };
            foreach (var (name, make) in wins)
            {
                Window? w = null;
                try
                {
                    w = make();
                    w.Show();
                    w.Activate();
                    Pump(800);
                    int dotted = 0, visited = 0;
                    List<UIElement> targets;
                    // Tab 이 닿는 곳 = 포커스 받을 수 있는 보이는 칸 전부 — 키 입력(스크립트)으로 "키보드 상태"를 만든 뒤 하나씩 포커스
                    targets = Descendants(w).OfType<UIElement>().Where(e => e.Focusable && e.IsVisible && e.IsEnabled && System.Windows.Input.KeyboardNavigation.GetIsTabStop(e)).Take(40).ToList();
                    foreach (var fe in targets)
                    {
                        var src = PresentationSource.FromVisual(w);
                        if (src is null) break;
                        System.Windows.Input.InputManager.Current.ProcessInput(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src, 0, System.Windows.Input.Key.LeftShift) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                        System.Windows.Input.Keyboard.Focus(fe);
                        Pump(40);
                        if (!fe.IsKeyboardFocused) continue;
                        visited++;
                        if (HasDotted(fe)) dotted++;
                    }
                    Check($"Tab 한 바퀴({name}): WPF 점선 없음 (Tab 칸 {targets.Count}개 중 포커스 간 곳 {visited}개, 점선 {dotted}번, 창 활성 {w.IsActive})", dotted == 0);
                }
                catch (Exception ex) { Check($"Tab 한 바퀴({name}): 창을 못 띄움 {ex.GetType().Name} {ex.InnerException?.Message ?? ex.Message}", false); }
                finally { try { w?.Close(); } catch { } Pump(300); }
            }
        }

        // A9) 몽독 툴팁 그림 (라이트·다크): 앱 칸 위에 툴팁을 띄워 그 자리를 찍음
        {
            typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.MongToolTip")!.GetMethod("Init", Any)!.Invoke(null, null);
            foreach (var theme in new[] { DockTheme.Light, DockTheme.Dark })
            {
                settings.Current.Dock.Theme = theme;
                Mongdock.ViewModels.UiTheme.Apply(settings.Current);
                var w = open();
                w.Show();
                Pump(1000);
                var cell = Descendants((DependencyObject)w.Content).OfType<System.Windows.Controls.Border>().FirstOrDefault(b => b.Tag is not null && b.Tag.GetType().Name == "DropTag" && b.IsVisible && b.ActualWidth > 0);
                if (cell is not null)
                {
                    var tip = new System.Windows.Controls.ToolTip { Content = "Microsoft Defender 보안 센터", PlacementTarget = cell };
                    tip.IsOpen = true;
                    Pump(500);
                    var at = cell.PointToScreen(new Point(0, 0));
                    double sc = Monitors.GetPrimary().Scale;
                    var r = new Rect(at.X / sc - 90, at.Y / sc - 60, cell.ActualWidth + 180, cell.ActualHeight + 70);
                    SaveStrip(Path.Combine(outDir, $"tooltip-{theme.ToString().ToLowerInvariant()}.png"), new List<(long, byte[])> { (0, Grab(r)) }, (int)r.Width, (int)r.Height);
                    Check($"몽독 툴팁({theme}): 몽독 스타일로 열림", tip.IsOpen && tip.Template?.VisualTree is not null);
                    tip.IsOpen = false;
                }
                close(w);
                Pump(400);
            }
            settings.Current.Dock.Theme = DockTheme.Light;
            Mongdock.ViewModels.UiTheme.Apply(settings.Current);
        }

        // B) 튀기기 애니메이션 (실제 창)
        {
            var layout = Mongdock.ViewModels.DockLayout.From(settings.Current.Dock, true);
            var vm = new Mongdock.ViewModels.DockItemViewModel("live-bounce", null, false, "live-test", null);
            var viewType = typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.DockItemView")!;
            var view = (FrameworkElement)Activator.CreateInstance(viewType, Any, null, new object[] { vm, layout }, null)!;
            var host = new Window
            {
                Title = "live-test bounce host", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
                Width = layout.IconSize * 2, Height = layout.IconSize * 3, Left = work.Left + 40, Top = work.Bottom - layout.IconSize * 3 - 40,
                Background = Brushes.Transparent, AllowsTransparency = true, ShowActivated = false,
                Content = new System.Windows.Controls.Border { VerticalAlignment = VerticalAlignment.Bottom, Child = view },
            };
            host.Show();
            Pump(400);
            var anim = typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.Anim")!;
            bool animOn = (bool)anim.GetProperty("Enabled", Any)!.GetValue(null)!;
            var bounce = (TranslateTransform)viewType.GetField("_bounce", Any)!.GetValue(view)!;
            var samples = new List<(long Ms, double Y)>();
            var sw = Stopwatch.StartNew();
            viewType.GetMethod("NotifyBounce", Any)!.Invoke(view, null);
            while (sw.ElapsedMilliseconds < 1900) { Pump(16); samples.Add((sw.ElapsedMilliseconds, bounce.Y)); }
            double expect = layout.NotifyBounceHeight;
            double maxUp = samples.Max(x => -x.Y);
            // 튄 횟수 = 낮은 곳(30% 아래)에서 높은 곳(60% 위)으로 올라간 횟수 (위에서 잠깐 머무는 구간의 작은 흔들림은 세지 않음)
            int peaks = 0;
            bool low = true;
            foreach (var (_, y) in samples)
            {
                if (low && -y > expect * 0.6) { peaks++; low = false; }
                else if (!low && -y < expect * 0.3) low = true;
            }
            long settled = samples.FirstOrDefault(x => x.Ms > 500 && Math.Abs(x.Y) < 0.5 && samples.Where(y => y.Ms >= x.Ms).All(y => Math.Abs(y.Y) < 0.5)).Ms;
            Console.WriteLine($"    애니메이션 켬 {animOn}, 아이콘 {layout.IconSize}, 기대 높이 {expect:0.0}, 최고 {maxUp:0.0}, 봉우리 {peaks}, 제자리 {settled}ms");
            Check($"튀기기: 애니메이션 설정 켜짐 (꺼져 있으면 튀지 않는 게 맞음)", animOn);
            if (animOn)
            {
                Check($"튀기기: 높이 아이콘의 60%·창 여유 안 (최고 {maxUp:0.0} / 기대 {expect:0.0})", Math.Abs(maxUp - expect) <= expect * 0.15);
                Check($"튀기기: 딱 2번 ({peaks}번)", peaks == 2);
                Check($"튀기기: 1.5초 안에 제자리 ({settled}ms)", settled > 0 && settled <= 1500);
            }
            host.Close();
            Pump(300);
        }

        // C) 작업 표시줄 깜빡임 신호
        {
            tracker.Start();
            int flashes = 0;
            IntPtr target = IntPtr.Zero;
            tracker.WindowFlashed += (_, h) => { if (h == target) flashes++; };
            var test = new Window { Title = "live-test flash", Width = 260, Height = 120, Left = work.Left + 60, Top = work.Top + 60, ShowInTaskbar = true };
            test.Show();
            target = new WindowInteropHelper(test).Handle;
            Pump(1500); // 설치된 몽독 독에 실행 중 앱으로 나타날 시간
            var other = new Window { Title = "live-test other", Width = 200, Height = 100, Left = work.Left + 340, Top = work.Top + 60, ShowInTaskbar = false };
            other.Show();
            other.Activate();
            Pump(500);
            var started = DateTime.Now;
            var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = target, dwFlags = 2 /* FLASHW_TRAY */, uCount = 3, dwTimeout = 0 };
            FlashWindowEx(ref fi);
            Pump(2500);
            Check($"깜빡임: 창 추적이 깜빡임을 받음 ({flashes}번, {started:HH:mm:ss})", flashes > 0);
            File.WriteAllText(Path.Combine(outDir, "flash-started.txt"), started.ToString("yyyy-MM-dd HH:mm:ss"));
            fi.dwFlags = 0; // FLASHW_STOP
            FlashWindowEx(ref fi);
            other.Close();
            test.Close();
            Pump(500);
        }
    }

    /// <summary>
    /// --demo: 사용자 앞 튀기기 시연 — "몽독 튀기기 시연" 시험 창을 띄워(독에 실행 중 앱으로 나타남) 다른 시험 창으로 앞을 넘긴 뒤
    /// 작업 표시줄 깜빡임을 간격(쿨다운 10초보다 길게)을 두고 보냄. 사용자 앱은 건드리지 않음. 끝나면 창을 모두 닫음.
    /// </summary>
    private static int Demo(int times, int gapSeconds)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var work = SystemParameters.WorkArea;
        // 회마다 다른 시험 창을 깜빡임 (같은 창을 다시 깜빡이려면 활성화해야 하는데, 앞을 다시 넘기는 게 막힐 수 있어서)
        var tests = new List<Window>();
        for (int i = 0; i < times; i++)
        {
            var t = new Window { Title = $"몽독 튀기기 시연 {i + 1}", Width = 300, Height = 120, Left = work.Left + 60 + i * 24, Top = work.Top + 60 + i * 24, ShowInTaskbar = true,
                Content = new System.Windows.Controls.TextBlock { Text = "몽독 튀기기 시연 창 (곧 닫혀요)", Margin = new Thickness(16) } };
            t.Show();
            tests.Add(t);
        }
        Pump(2500); // 독에 나타날 시간
        var other = new Window { Title = "시연 도우미", Width = 220, Height = 100, Left = work.Left + 420, Top = work.Top + 60, ShowInTaskbar = false };
        other.Show();
        other.Activate();
        Pump(800);
        for (int i = 0; i < times; i++)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} 깜빡임 {i + 1}/{times} (앞 창 {(GetForegroundWindow() == new WindowInteropHelper(other).Handle ? "도우미" : "다른 창")})");
            var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = new WindowInteropHelper(tests[i]).Handle, dwFlags = 2, uCount = 2, dwTimeout = 0 };
            FlashWindowEx(ref fi);
            Pump(1500);
            fi.dwFlags = 0;
            FlashWindowEx(ref fi);
            if (i < times - 1) Pump(gapSeconds * 1000 - 1500);
        }
        Pump(4000);
        other.Close();
        foreach (var t in tests) t.Close();
        Pump(300);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct POINTL { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINTL p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    /// <summary>Esc (끄는 중 취소 — 원래 자리로).</summary>
    private static void Key2Escape() { keybd_event(0x1B, 0, 0, IntPtr.Zero); keybd_event(0x1B, 0, 2, IntPtr.Zero); Pump(200); }
    [StructLayout(LayoutKind.Sequential)] private struct RECTL { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECTL rect);

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

    /// <summary>WPF 기본 점선(FocusVisualAdorner)이 그 요소에 붙어 있는지.</summary>
    private static bool HasDotted(UIElement el) =>
        System.Windows.Documents.AdornerLayer.GetAdornerLayer(el)?.GetAdorners(el)?.Any(a => a.GetType().Name == "FocusVisualAdorner") == true;

    /// <summary>요소 가장자리(바깥 4px 포함 8px 띠)의 실제 화면 픽셀: 어두운 점(점선) 수, 연보라 점(몽독 테두리) 수.</summary>
    private static (int Dark, int Lilac) EdgePixels(FrameworkElement el)
    {
        double scale = Monitors.GetPrimary().Scale;
        var tl = el.PointToScreen(new Point(0, 0));
        var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        const int pad = 4;
        var rectPx = new Rect(tl.X - pad, tl.Y - pad, br.X - tl.X + pad * 2, br.Y - tl.Y + pad * 2);
        var px = Grab(new Rect(rectPx.X / scale, rectPx.Y / scale, rectPx.Width / scale, rectPx.Height / scale));
        int w = (int)(rectPx.Width / scale * scale), h = px.Length / 4 / Math.Max(1, w);
        int dark = 0, lilac = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int edge = Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y));
                if (edge >= pad * 2) continue;
                int k = (y * w + x) * 4;
                byte b = px[k], g = px[k + 1], r = px[k + 2];
                if (r < 100 && g < 100 && b < 100) dark++;
                if (Math.Abs(r - 0xA8) < 30 && Math.Abs(g - 0x96) < 30 && Math.Abs(b - 0xF2) < 30) lilac++;
            }
        return (dark, lilac);
    }

    private static void Check(string what, bool ok)
    {
        string line = $"{(ok ? "통과" : "실패")}  {what}";
        Results.Add(line);
        Console.WriteLine("  " + line);
        if (!ok) _fail++;
    }

    /// <summary>판을 열고 900ms 동안 16ms 마다 판 자리를 찍어, 보이는 정도가 오르다 내려가지 않는지(번쩍임·두 번 뜸) 확인. 그다음 닫고 그림 창이 남지 않는지.</summary>
    private static void OpenAndWatch(string name, Func<Window> open, Action<Window> close, Func<bool> ghostVisible, string outDir, Rect work)
    {
        double width = Math.Clamp(work.Width * (work.Height > work.Width ? 0.92 : 0.80), Math.Min(640, work.Width - 40), 1800);
        var sample = new Rect(work.Left + work.Width / 2 - 120, work.Top + work.Height * 0.35, 240, 120); // 판 가운데 위쪽 (검색 칸 아래)
        var dpi = VisualTreeHelper.GetDpi(new System.Windows.Controls.Border());
        var before = Grab(sample);
        var frames = new List<(long Ms, byte[] Px)>();
        var clock = Stopwatch.StartNew();
        var w = open();
        w.Show();
        while (clock.ElapsedMilliseconds < 900)
        {
            Pump(1);
            frames.Add((clock.ElapsedMilliseconds, Grab(sample)));
            Pump(15);
        }
        bool active = w.IsActive;
        bool visible = w.IsVisible;
        // 보이는 정도: 다 열린 마지막 화면과 얼마나 같은지 (1 = 같음). 열기 전 바탕과 비교하면 판 뒤에 밝은 창이 있을 때 판정이 안 됨(4분 쉰 뒤 시험)
        var last = frames.Count > 0 ? frames[^1].Px : before;
        var shown = frames.Select(f => 1 - Diff(last, f.Px)).ToList();
        // 열기 전과 마지막이 다르면 판이 떴음. 판 뒤가 판과 비슷한 밝은 창이면 글자만 달라 차이가 몇 % 뿐 — 2% 넘으면 뜬 것으로
        double final = Diff(last, before) > 0.02 ? 1 : 0;
        double peak = 0;
        int dips = 0;
        foreach (var s in shown)
        {
            if (peak > 0.6 && s < peak - 0.35) dips++; // 거의 다 보였다가 크게 사라짐 = 번쩍임·두 번 뜸
            peak = Math.Max(peak, s);
        }
        // 움직임 덩어리: 프레임 사이 변화가 있는 구간이 몇 번인지 (멈춘 프레임 3개 이상으로 나뉘면 따로 셈) — 2번이면 "두 번 열리는" 것처럼 보임
        int bursts = 0, still = 99;
        long burst2At = -1;
        for (int i = 1; i < frames.Count; i++)
        {
            bool moving = Diff(frames[i - 1].Px, frames[i].Px) > 0.004;
            if (moving) { if (still >= 3) { bursts++; if (bursts == 2) burst2At = frames[i].Ms; } still = 0; }
            else still++;
        }
        int firstSeen = shown.FindIndex(s => s > 0.1);
        int full = shown.FindIndex(s => s >= final - 0.05 && final > 0.3);
        Check($"{name}: 열림·활성 (활성 {active}, 보임 {visible})", visible);
        Check($"{name}: 사라졌다 다시 뜨지 않음 (떨어짐 {dips}번, 처음 보임 {(firstSeen >= 0 ? frames[firstSeen].Ms : -1)}ms, 다 보임 {(full >= 0 ? frames[full].Ms : -1)}ms)", dips == 0 && final > 0.3);
        Check($"{name}: 움직임이 한 번에 끝남 (움직임 {bursts}번{(burst2At >= 0 ? $", 두 번째 {burst2At}ms" : "")})", bursts <= 1);
        SaveStrip(Path.Combine(outDir, $"frames-{name}.png"), frames.Where((_, i) => i % 3 == 0).Take(18).ToList(), (int)sample.Width, (int)sample.Height);
        close(w);
        Pump(250);
        Check($"{name}: 닫은 뒤 그림 창 없음", !ghostVisible() && !w.IsVisible);
        Pump(300);
    }

    private static double Diff(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length) / 4, d = 0;
        for (int i = 0; i < n; i++)
        {
            int k = i * 4;
            if (Math.Abs(a[k] - b[k]) + Math.Abs(a[k + 1] - b[k + 1]) + Math.Abs(a[k + 2] - b[k + 2]) > 40) d++;
        }
        return n == 0 ? 0 : (double)d / n;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    // ───────────────────────── 화면 찍기 (GDI) ─────────────────────────

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    /// <summary>화면의 DIP 사각형을 BGRA 바이트로 (주 모니터 배율로 픽셀 변환).</summary>
    private static byte[] Grab(Rect dip)
    {
        double scale = Monitors.GetPrimary().Scale;
        int x = (int)(dip.X * scale), y = (int)(dip.Y * scale), w = (int)(dip.Width * scale), h = (int)(dip.Height * scale);
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */);
        SelectObject(mem, old);
        var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var px = new byte[conv.PixelWidth * conv.PixelHeight * 4];
        conv.CopyPixels(px, conv.PixelWidth * 4, 0);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return px;
    }

    private static void SaveStrip(string path, List<(long Ms, byte[] Px)> frames, int dipW, int dipH)
    {
        if (frames.Count == 0) return;
        double scale = Monitors.GetPrimary().Scale;
        int w = (int)(dipW * scale), h = (int)(dipH * scale);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            for (int i = 0; i < frames.Count; i++)
            {
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, frames[i].Px, w * 4);
                double x = (i % 6) * (w / 2 + 6), y = (i / 6) * (h / 2 + 22);
                dc.DrawImage(bmp, new Rect(x, y + 16, w / 2.0, h / 2.0));
                dc.DrawText(new FormattedText($"{frames[i].Ms}ms", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Black, 1.0), new Point(x, y));
            }
        }
        int rows = (frames.Count + 5) / 6;
        var rtb = new RenderTargetBitmap(6 * (w / 2 + 6), rows * (h / 2 + 22), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
