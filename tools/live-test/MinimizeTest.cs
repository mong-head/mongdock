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
        var w = new Window { Title = TestWindowTitle, Width = 900, Height = 540, Left = work.Left + (work.Width - 900) / 2, Top = work.Top + 110, Content = grid, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
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
}
