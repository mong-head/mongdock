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
internal static class Program
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly List<string> Results = new();
    private static int _fail;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("live-test OUT [--idle 초]"); return 2; }
        string outDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outDir);
        int idleAt = Array.IndexOf(args, "--idle");
        int idle = idleAt >= 0 && idleAt + 1 < args.Length ? int.Parse(args[idleAt + 1]) : 0;
        string data = Path.Combine(Path.GetTempPath(), "mongdock-live-" + Guid.NewGuid().ToString("N")[..8]);
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
        double width = Math.Clamp(work.Width * 0.6, Math.Min(640, work.Width - 40), 1200);
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
        double final = 1 - Diff(last, before) < 0.9 ? 1 : 0; // 열기 전과 마지막이 다르면 판이 떴음
        double peak = 0;
        int dips = 0;
        foreach (var s in shown)
        {
            if (peak > 0.6 && s < peak - 0.35) dips++; // 거의 다 보였다가 크게 사라짐 = 번쩍임·두 번 뜸
            peak = Math.Max(peak, s);
        }
        int firstSeen = shown.FindIndex(s => s > 0.1);
        int full = shown.FindIndex(s => s >= final - 0.05 && final > 0.3);
        Check($"{name}: 열림·활성 (활성 {active}, 보임 {visible})", visible);
        Check($"{name}: 사라졌다 다시 뜨지 않음 (떨어짐 {dips}번, 처음 보임 {(firstSeen >= 0 ? frames[firstSeen].Ms : -1)}ms, 다 보임 {(full >= 0 ? frames[full].Ms : -1)}ms)", dips == 0 && final > 0.3);
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
