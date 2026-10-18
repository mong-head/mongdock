using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mongdock.Models;
using Mongdock.Services;

namespace LiveTest;

/// <summary>
/// --minimize (#20): 창 최소화 효과 실제 창 시험. 시험 창은 다른 프로세스(live-test --testwindow)라 몽독 훅이 받음. 실제 입력 없음(ShowWindowAsync 만).
/// 효과(차분하게·아이콘으로·지니) × 속도 보통: 최소화·되살리기를 시간 25·50·75% 에 멈춰 화면을 찍고(FreezeAtForTest), 애니메이션 줄이기 1장.
/// 윈도우 「최소화·최대화 애니메이션」 끄기·끔(=[되돌리기])·종료 때 되돌림을 실제 설정으로 확인하고 끝에 원래 값인지 확인.
/// </summary>
internal static partial class Program
{
    private const string TestWindowTitle = "mongdock 최소화 시험 창";

    /// <summary>live-test --testwindow: 시험 창 하나만 띄움 (부모가 닫음).</summary>
    private static int TestWindowMain()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var grid = new Grid { Background = new LinearGradientBrush(Color.FromRgb(0x7B, 0x8C, 0xF0), Color.FromRgb(0xE0, 0x8F, 0xC8), 30) };
        grid.Children.Add(new TextBlock { Text = "최소화 시험 창", FontSize = 54, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        for (int i = 0; i < 6; i++)
            grid.Children.Add(new Border { Height = 14, Width = 560 - i * 60, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(40, 40 + i * 28, 0, 0) });
        var work = SystemParameters.WorkArea;
        bool offscreen = Environment.GetCommandLineArgs().Contains("--offscreen");
        var w = new Window { Title = TestWindowTitle, Width = 900, Height = 540, Left = offscreen ? -4000 : work.Left + (work.Width - 900) / 2, Top = work.Top + 110, Content = grid, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        app.Run(w);
        return 0;
    }

    private static void MinimizeRun(AppServices services, SettingsService settings, Rect work, string outDir)
    {
        var asm = typeof(Mongdock.App).Assembly;
        var mecType = asm.GetType("Mongdock.Views.MinimizeEffectController")!;
        var was = asm.GetType("Mongdock.Services.WindowAnimationSetting")!;
        bool? sysOriginal = SysAnimation();
        Console.WriteLine($"윈도우 최소화·최대화 애니메이션 시작 값: {sysOriginal}");
        double scale = Monitors.GetPrimary().Scale;

        // 독 아이콘 자리(가짜): 작업 영역 아래 가운데 56 DIP 주황 칸
        var iconDip = new Rect(work.Left + work.Width / 2 - 28, work.Bottom - 70, 56, 56);
        var iconPx = new Rect(iconDip.X * scale, iconDip.Y * scale, iconDip.Width * scale, iconDip.Height * scale);
        var iconWin = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Left = iconDip.X, Top = iconDip.Y, Width = iconDip.Width, Height = iconDip.Height, Title = "mongdock 시험 독 아이콘",
            Content = new Border { CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x3C)) },
        };
        iconWin.Show();

        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--testwindow") { UseShellExecute = false })!;
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++) { Pump(100); hwnd = FindWindow(null, TestWindowTitle); }
        Check("시험 창 뜸 (다른 프로세스)", hwnd != IntPtr.Zero);
        object? controller = null;
        try
        {
            if (hwnd == IntPtr.Zero) return;
            Pump(800);
            GetWindowRect(hwnd, out RECTL wr);
            var crop = new Rect(wr.Left / scale - 70, wr.Top / scale - 24, (wr.Right - wr.Left) / scale + 140, work.Bottom - (wr.Top / scale - 24));
            Console.WriteLine($"시험 창 {wr.Left},{wr.Top}~{wr.Right},{wr.Bottom} 물리 px, 아이콘 {iconPx}");

            settings.Current.Dock.MinimizeEffect = MinimizeEffect.Settle;
            settings.Current.Dock.MinimizeSpeed = MinimizeSpeed.Normal;
            settings.Save();
            controller = mecType.GetConstructors()[0].Invoke(new object[] { services, (Func<IntPtr, Rect?>)(_ => iconPx), (Func<DockEdge>)(() => DockEdge.Bottom) });
            Pump(300);
            bool byMong = (bool)was.GetProperty("TurnedOffByMongdock")!.GetValue(null)!;
            Check($"효과 켬 → 윈도우 애니메이션 꺼짐 (지금 {SysAnimation()}, 몽독이 끔 {byMong})", SysAnimation() == false && (byMong || sysOriginal == false));

            var freeze = mecType.GetProperty("FreezeAtForTest", Any)!;
            var reduced = mecType.GetProperty("ReducedForTest", Any)!;
            var restoreHook = typeof(AppLauncher).GetProperty("AnimatedRestore", Any)!;
            var rows = new List<(string Name, List<(string Label, byte[] Px)> Frames)>();
            int cw = (int)(crop.Width * scale), ch = (int)(crop.Height * scale);
            byte[]? idle = null;

            foreach (var (effect, name) in new[] { (MinimizeEffect.Settle, "차분하게"), (MinimizeEffect.Scale, "아이콘으로"), (MinimizeEffect.Genie, "지니") })
            {
                settings.Current.Dock.MinimizeEffect = effect;
                settings.Save();
                Pump(200);
                var frames = new List<(string, byte[])>();
                // 최소화
                freeze.SetValue(null, 0.25);
                ShowWindowAsync(hwnd, 6 /* SW_MINIMIZE */);
                Pump(500);
                frames.Add(("최소화 25%", Grab(crop)));
                freeze.SetValue(null, 0.5);
                Pump(150);
                frames.Add(("최소화 50%", Grab(crop)));
                freeze.SetValue(null, 0.75);
                Pump(150);
                frames.Add(("최소화 75%", Grab(crop)));
                freeze.SetValue(null, null);
                Pump(900);
                Check($"{name}: 최소화됨", IsIconic(hwnd));
                idle ??= Grab(crop);
                double seen = Diff(frames[1].Item2, idle);
                Check($"{name}: 최소화 50% 프레임에 창 모습이 보임 (다른 픽셀 {seen:P0})", seen > 0.05);
                // 되살리기 (몽독에서 열 때와 같은 길 — 포그라운드·입력 없이 되살리기만)
                freeze.SetValue(null, 0.25);
                var hook = restoreHook.GetValue(null) as Func<IntPtr, Action, bool>;
                bool animated = hook?.Invoke(hwnd, () => ShowWindowAsync(hwnd, 9 /* SW_RESTORE */)) == true;
                Pump(300);
                Check($"{name}: 되살리기 효과 시작 (효과 {animated}, 아직 최소화 {IsIconic(hwnd)})", animated && IsIconic(hwnd));
                frames.Add(("되살리기 25%", Grab(crop)));
                freeze.SetValue(null, 0.5);
                Pump(150);
                frames.Add(("되살리기 50%", Grab(crop)));
                freeze.SetValue(null, 0.75);
                Pump(150);
                frames.Add(("되살리기 75%", Grab(crop)));
                freeze.SetValue(null, null);
                if (!animated) ShowWindowAsync(hwnd, 9);
                Pump(900);
                Check($"{name}: 되살아남", !IsIconic(hwnd) && IsWindowVisible(hwnd));

                // 되살리기 끝 넘기기 (#20 깜빡임): 90%(진짜 창을 덮개 밑에서 되살린 뒤)·99%·끝난 뒤 사진
                ShowWindowAsync(hwnd, 6);
                Pump(900);
                freeze.SetValue(null, 0.9);
                hook = restoreHook.GetValue(null) as Func<IntPtr, Action, bool>;
                bool animatedEnd = hook?.Invoke(hwnd, () => { ShowWindowAsync(hwnd, 9); SetForegroundWindow(hwnd); }) == true;
                Pump(400);
                Check($"{name}: 90% 에서 진짜 창은 덮개 밑에서 이미 되살아남 (최소화 {IsIconic(hwnd)})", animatedEnd && !IsIconic(hwnd));
                frames.Add(("되살리기 90% (넘긴 뒤)", Grab(crop)));
                freeze.SetValue(null, 0.99);
                Pump(150);
                frames.Add(("되살리기 99%", Grab(crop)));
                freeze.SetValue(null, null);
                Pump(700);
                frames.Add(("끝 (덮개 치움)", Grab(crop)));
                Check($"{name}: 끝 모습 = 덮개 치운 뒤 모습 (99% 와 다른 픽셀 {Diff(frames[^2].Item2, frames[^1].Item2):P1})", Diff(frames[^2].Item2, frames[^1].Item2) < 0.02);

                // 깜빡임: 다시 최소화 → 멈춤 없이 되살리기를 화면 프레임마다(DXGI) 재서, 창 자리 밝기가 끝 값에 닿은 뒤 다시 벗어나는 프레임이 있는지
                ShowWindowAsync(hwnd, 6);
                Pump(900);
                string flick = RestoreFlicker(name, wr, () =>
                {
                    var h2 = restoreHook.GetValue(null) as Func<IntPtr, Action, bool>;
                    if (h2?.Invoke(hwnd, () => { ShowWindowAsync(hwnd, 9); SetForegroundWindow(hwnd); }) != true) ShowWindowAsync(hwnd, 9);
                }, outDir, out bool flickered);
                Check(flick, !flickered);
                rows.Add((name, frames));
            }

            // 애니메이션 줄이기·절전: 짧은 흐려짐만 (50%)
            reduced.SetValue(null, (Func<bool>)(() => true));
            freeze.SetValue(null, 0.5);
            ShowWindowAsync(hwnd, 6);
            Pump(500);
            var fade = Grab(crop);
            freeze.SetValue(null, null);
            Pump(600);
            var hook2 = restoreHook.GetValue(null) as Func<IntPtr, Action, bool>;
            bool animated2 = hook2?.Invoke(hwnd, () => ShowWindowAsync(hwnd, 9)) == true;
            if (!animated2) ShowWindowAsync(hwnd, 9);
            Pump(600);
            Check($"애니메이션 줄이기: 되살리기는 효과 없이 바로 ({!animated2}), 되살아남 {!IsIconic(hwnd)}", !animated2 && !IsIconic(hwnd));
            reduced.SetValue(null, null);
            rows.Add(("애니메이션 줄이기", new List<(string, byte[])> { ("최소화 50% (흐려짐만)", fade) }));
            SaveMinimizeSheet(Path.Combine(outDir, "minimize-frames.png"), rows, cw, ch);
            Console.WriteLine($"  저장  {Path.Combine(outDir, "minimize-frames.png")}");

            // 윈도우 애니메이션 끄기·되돌리기(설정에서 끔 = [되돌리기])·종료
            settings.Current.Dock.MinimizeEffect = MinimizeEffect.Off;
            settings.Save();
            Pump(300);
            Check($"효과 끔([되돌리기]) → 윈도우 애니메이션 원래대로 ({SysAnimation()} = 시작 {sysOriginal}), 기록 지움", SysAnimation() == sysOriginal && !(bool)was.GetProperty("HasRecord")!.GetValue(null)!);
            settings.Current.Dock.MinimizeEffect = MinimizeEffect.Settle;
            settings.Save();
            Pump(300);
            Check($"다시 켬 → 꺼짐 ({SysAnimation()})", SysAnimation() == false);
            ((IDisposable)controller).Dispose();
            controller = null;
            Pump(300);
            Check($"몽독 종료 → 윈도우 애니메이션 원래대로 ({SysAnimation()} = 시작 {sysOriginal}), 기록 지움", SysAnimation() == sysOriginal && !(bool)was.GetProperty("HasRecord")!.GetValue(null)!);
        }
        finally
        {
            mecType.GetProperty("FreezeAtForTest", Any)!.SetValue(null, null);
            if (controller is IDisposable d) d.Dispose();
            try { if (!child.HasExited) { child.CloseMainWindow(); if (!child.WaitForExit(3000)) child.Kill(); } } catch { }
            iconWin.Close();
            Pump(300);
            Check($"끝: 윈도우 애니메이션 시작 값 그대로 ({SysAnimation()} = {sysOriginal}), 시험 창 닫힘 {child.HasExited}", SysAnimation() == sysOriginal && child.HasExited);
        }
    }

    /// <summary>--cloaktest (#27): 몽독이 다른 프로세스 창을 DWM 으로 가릴 수 있는지 (화면 밖 시험 창, 입력 없음).</summary>
    private static int CloakTest()
    {
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--testwindow --offscreen") { UseShellExecute = false })!;
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++) { System.Threading.Thread.Sleep(100); hwnd = FindWindow(null, TestWindowTitle); }
        int one = 1, v = 0;
        int hr = hwnd == IntPtr.Zero ? -1 : DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref one, 4);
        int hr2 = hwnd == IntPtr.Zero ? -1 : DwmGetWindowAttributeInt(hwnd, 14 /* DWMWA_CLOAKED */, out v, 4);
        Console.WriteLine($"창 0x{hwnd.ToInt64():X}: 다른 프로세스에서 DWMWA_CLOAK hr=0x{hr:X8}, 가려짐 hr=0x{hr2:X8} 값={v}");
        int zero = 0;
        if (hwnd != IntPtr.Zero) DwmSetWindowAttribute(hwnd, 13, ref zero, 4);
        try { child.Kill(); } catch { }
        return 0;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr h, int a, out int v, int s);

    /// <summary>
    /// 되살리기 동안 화면 프레임마다(DXGI) 창 안 네 곳의 밝기를 재서: 끝 값(마지막 프레임)에 닿은 뒤 10 넘게 벗어나는 프레임 = 깜빡임.
    /// 프레임 값은 OUT/restore-flicker-이름.csv.
    /// </summary>
    private static string RestoreFlicker(string name, RECTL wr, Action restore, string outDir, out bool flickered)
    {
        flickered = false;
        int cx = (wr.Left + wr.Right) / 2, cy = (wr.Top + wr.Bottom) / 2;
        var boxes = new (int X, int Y, int W, int H)[]
        {
            (wr.Left + 40, wr.Top + 10, 200, 16),        // 제목 줄
            (cx - 60, cy - 40, 120, 80),                 // 가운데 글자
            (wr.Left + 40, wr.Bottom - 120, 120, 60),    // 왼쪽 아래
            (wr.Right - 200, cy + 40, 120, 60),          // 오른쪽
        };
        var frames = new List<(long T, double[] V)>();
        var sw = Stopwatch.StartNew();
        bool stop = false, ready = false;
        string? error = null;
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
        Pump(200);
        long at = sw.ElapsedMilliseconds;
        restore();
        Pump(1200);
        System.Threading.Volatile.Write(ref stop, true);
        th.Join();
        if (error is not null) { flickered = true; return $"{name}: 깜빡임 — 화면 받기 실패 ({error})"; }
        var during = frames.Where(x => x.T >= at).ToList();
        File.WriteAllLines(Path.Combine(outDir, $"restore-flicker-{name}.csv"), frames.Select(x => $"{x.T - at},{string.Join(",", x.V.Select(v => v.ToString("0.0")))}"));
        if (during.Count < 3) { flickered = true; return $"{name}: 깜빡임 — 프레임이 {during.Count}개뿐"; }
        var end = during[^1].V;
        int arrived = during.FindIndex(x => Enumerable.Range(0, boxes.Length).All(k => Math.Abs(x.V[k] - end[k]) < 4));
        var after = arrived < 0 ? new List<(long T, double[] V)>() : during.Skip(arrived).Where(x => Enumerable.Range(0, boxes.Length).Any(k => Math.Abs(x.V[k] - end[k]) > 10)).ToList();
        flickered = arrived < 0 || after.Count > 0;
        return $"{name}: 되살리기 끝 깜빡임 없음 (프레임 {during.Count}개, 끝 모습에 닿음 {(arrived < 0 ? "없음" : $"{during[arrived].T - at}ms")}, 그 뒤 벗어난 프레임 {after.Count}개{(after.Count > 0 ? ": " + string.Join(" ", after.Select(x => $"{x.T - at}ms")) : "")})";
    }

    private static void SaveMinimizeSheet(string path, List<(string Name, List<(string Label, byte[] Px)> Frames)> rows, int w, int h)
    {
        const double k = 0.34;
        double cellW = w * k + 8, cellH = h * k + 22;
        int cols = rows.Max(r => r.Frames.Count);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 110 + cols * cellW, rows.Count * cellH));
            for (int r = 0; r < rows.Count; r++)
            {
                dc.DrawText(new FormattedText(rows[r].Name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 14, Brushes.Black, 1.0), new Point(6, r * cellH + cellH / 2 - 10));
                for (int c = 0; c < rows[r].Frames.Count; c++)
                {
                    var (label, px) = rows[r].Frames[c];
                    var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
                    double x = 110 + c * cellW, y = r * cellH;
                    dc.DrawText(new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 11, Brushes.Black, 1.0), new Point(x, y + 2));
                    dc.DrawImage(bmp, new Rect(x, y + 18, w * k, h * k));
                }
            }
        }
        var rtb = new RenderTargetBitmap((int)(110 + cols * cellW), (int)(rows.Count * cellH), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>윈도우 「최소화·최대화 애니메이션」 실제 값.</summary>
    private static bool? SysAnimation()
    {
        var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
        return SystemParametersInfo(0x0048 /* SPI_GETANIMATION */, info.cbSize, ref info, 0) ? info.iMinAnimate != 0 : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO info, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
