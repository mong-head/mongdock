using System.Runtime.CompilerServices;
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

        // 시험 창: 보통은 몽독 시험 창(WPF), --electron 이면 Electron 앱 창 (#27 — 사용자 "빠방"은 Claude 앱 같은 Electron 창).
        // Electron 은 VS Code 를 임시 사용자 폴더로 따로 띄움 (사용자 VS Code·설정은 안 건드림, 끝나면 그 프로세스들만 닫음)
        bool electron = Environment.GetCommandLineArgs().Contains("--electron");
        string? electronData = null;
        Process child;
        IntPtr hwnd = IntPtr.Zero;
        if (electron)
        {
            string code = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe");
            electronData = Path.Combine(Path.GetTempPath(), "mongdock-electron-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(electronData);
            var before = new HashSet<IntPtr>(TopWindowsOfClass("Chrome_WidgetWin_1"));
            child = Process.Start(new ProcessStartInfo(code, $"--user-data-dir \"{electronData}\" --extensions-dir \"{Path.Combine(electronData, "ext")}\" --disable-extensions --new-window --skip-welcome --skip-release-notes") { UseShellExecute = false })!;
            for (int i = 0; i < 300 && hwnd == IntPtr.Zero; i++)
            {
                Pump(100);
                hwnd = TopWindowsOfClass("Chrome_WidgetWin_1").FirstOrDefault(h => !before.Contains(h) && IsWindowVisible(h) && WindowTitle(h).Contains("Visual Studio Code"));
            }
            Pump(2500); // 첫 화면 그리기가 끝나게
        }
        else
        {
            child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--testwindow") { UseShellExecute = false })!;
            for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++) { Pump(100); hwnd = FindWindow(null, TestWindowTitle); }
        }
        Check($"시험 창 뜸 (다른 프로세스{(electron ? ", Electron = VS Code 임시 창" : "")})", hwnd != IntPtr.Zero);
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
                bool animatedEnd = hook?.Invoke(hwnd, () => ShowWindowAsync(hwnd, 9)) == true;
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
                    if (h2?.Invoke(hwnd, () => ShowWindowAsync(hwnd, 9)) != true) ShowWindowAsync(hwnd, 9);
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
            if (electron)
            {
                // 임시 VS Code 만 닫음 (그 사용자 폴더로 뜬 프로세스 묶음) → 임시 폴더 지움
                if (hwnd != IntPtr.Zero) { GetWindowThreadProcessId(hwnd, out uint epid); try { Process.Start(new ProcessStartInfo("taskkill", $"/PID {epid} /T /F") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000); } catch { } }
                try { Process.Start(new ProcessStartInfo("taskkill", $"/PID {child.Id} /T /F") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000); } catch { }
                Pump(1000);
                try { if (electronData is not null) Directory.Delete(electronData, true); } catch { }
            }
            else
                try { if (!child.HasExited) { child.CloseMainWindow(); if (!child.WaitForExit(3000)) child.Kill(); } } catch { }
            iconWin.Close();
            Pump(300);
            bool closed = electron ? hwnd == IntPtr.Zero || !IsWindow(hwnd) : child.HasExited;
            Check($"끝: 윈도우 애니메이션 시작 값 그대로 ({SysAnimation()} = {sysOriginal}), 시험 창 닫힘 {closed}", SysAnimation() == sysOriginal && closed);
        }
    }

    /// <summary>마지막 RestoreFlicker 숫자: 누른 뒤 첫 변화 ms, 끝 모습에 닿은 ms, 그 뒤 벗어난 프레임 수, 끝 모습에서 가장 크게 벗어난 밝기.</summary>
    private static (long FirstMs, long ArriveMs, int Flashes, double MaxDev) LastFlicker;

    /// <summary>
    /// --flashmatrix [--electron] [--newdesktop] (#27 「되살리기 끝 번쩍임」 화면 측정): 같은 시험 창을 조건마다 3번씩 최소화 → 되살리기 하며
    /// DXGI 로 창 자리 밝기를 프레임마다 재서, 끝 모습에 닿은 뒤 다시 벗어나는 프레임(번쩍임)을 셈.
    /// 조건: A 효과 끔(윈도우 애니메이션 켬) · B 효과 끔 + 윈도우 애니메이션 끔 · C 차분하게 · D 지니 — 모두 「되살리기만」(활성화 없음),
    /// 그리고 A2·D2 = 같은 조건 + 되살린 뒤 SetForegroundWindow(앞으로 가져오기, 성공 여부 기록).
    /// 실제 마우스·키 입력 없음(--newdesktop 만 몽독 「새 데스크톱」과 같은 Ctrl+Win+D·Ctrl+Win+F4 를 보냄). 윈도우 애니메이션 설정은 시작 값으로 되돌림.
    /// </summary>
    private static void FlashMatrix(AppServices services, SettingsService settings, Rect work, string outDir)
    {
        var asm = typeof(Mongdock.App).Assembly;
        var mecType = asm.GetType("Mongdock.Views.MinimizeEffectController")!;
        bool? sysOriginal = SysAnimation();
        // --edge: Edge 임시 프로필 창 (크롬 계열 — VS Code 가 업데이트 대기로 「vscode-updating」 잠금을 쥐고 있어 임시 창이 안 뜰 때, 10-21)
        bool edge = Environment.GetCommandLineArgs().Contains("--edge");
        bool electron = edge || Environment.GetCommandLineArgs().Contains("--electron");
        bool newDesktop = Environment.GetCommandLineArgs().Contains("--newdesktop");
        Console.WriteLine($"윈도우 애니메이션 시작 값 {sysOriginal}, 대상 {(edge ? "Edge 임시 창(크롬 계열)" : electron ? "VS Code 임시 창(Electron)" : "몽독 시험 창(WPF)")}, 새 데스크톱 {newDesktop}");
        Guid? madeDesktop = null;
        if (newDesktop)
        {
            var vds = typeof(VirtualDesktopService);
            List<Guid> Ids() => (List<Guid>)vds.GetMethod("ReadDesktopIds", Any)!.Invoke(null, null)!;
            var before = Ids();
            var task = (Task<bool>)vds.GetMethod("NewAsync", Any)!.Invoke(null, null)!;
            while (!task.IsCompleted) Pump(50);
            if (task.Result)
            {
                Pump(1200);
                madeDesktop = Ids().FirstOrDefault(g => !before.Contains(g));
                Console.WriteLine($"  시험용 데스크톱 {(madeDesktop is { } g && g != Guid.Empty ? "만듦" : "못 만듦")}");
            }
        }
        // 시험 창
        string? electronData = null;
        Process child;
        IntPtr hwnd = IntPtr.Zero;
        if (electron)
        {
            electronData = Path.Combine(Path.GetTempPath(), "mongdock-electron-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(electronData);
            var before = new HashSet<IntPtr>(TopWindowsOfClass("Chrome_WidgetWin_1"));
            string titleHas;
            if (edge)
            {
                string msedge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
                string page = Path.Combine(electronData, "page27.html");
                File.WriteAllText(page, ContentPage());
                child = Process.Start(new ProcessStartInfo(msedge, $"--user-data-dir=\"{electronData}\" --no-first-run --no-default-browser-check --disable-extensions --disable-sync --new-window \"{new Uri(page).AbsoluteUri}\"") { UseShellExecute = false })!;
                titleHas = "Edge";
            }
            else
            {
                string code = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe");
                child = Process.Start(new ProcessStartInfo(code, $"--user-data-dir \"{electronData}\" --extensions-dir \"{Path.Combine(electronData, "ext")}\" --disable-extensions --new-window --skip-welcome --skip-release-notes") { UseShellExecute = false })!;
                titleHas = "Visual Studio Code";
            }
            for (int i = 0; i < 300 && hwnd == IntPtr.Zero; i++)
            {
                Pump(100);
                hwnd = TopWindowsOfClass("Chrome_WidgetWin_1").FirstOrDefault(h => !before.Contains(h) && IsWindowVisible(h) && WindowTitle(h).Contains(titleHas));
            }
            Pump(3000);
        }
        else
        {
            child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--testwindow") { UseShellExecute = false })!;
            for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++) { Pump(100); hwnd = FindWindow(null, TestWindowTitle); }
            Pump(800);
        }
        Check("시험 창 뜸", hwnd != IntPtr.Zero);
        object? controller = null;
        var lines = new List<string>();
        try
        {
            if (hwnd == IntPtr.Zero) return;
            GetWindowRect(hwnd, out RECTL wr);
            double scale = Monitors.GetPrimary().Scale;
            var iconPx = new Rect((work.Left + work.Width / 2 - 28) * scale, (work.Bottom - 70) * scale, 56 * scale, 56 * scale);
            var restoreHook = typeof(AppLauncher).GetProperty("AnimatedRestore", Any)!;

            bool viaDock = false; // true: 독 클릭처럼 최소화 직전에 AppLauncher.NoteBeforeMinimize (보이는 자리 기록, #27 스냅 회귀)
            var noteBefore = typeof(AppLauncher).GetMethod("NoteBeforeMinimize", Any)!;
            void Run(string label, Action restore)
            {
                var flashes = new List<int>();
                var devs = new List<double>();
                var firsts = new List<long>();
                var stills = new List<string>();
                var stillProp = mecType.GetProperty("LastStillForTest", Any)!;
                var endProp = mecType.GetProperty("LastEndMismatchForTest", Any)!; // #27 끝 자리 차이 (진짜 창 − 효과 끝)
                var retargetRun = mecType.GetProperty("LastRetargetForTest", Any)!; // 안전망이 끝 자리를 다시 맞췄는지
                var ends = new List<string>();
                int fgOk = 0, fgTried = 0;
                for (int k = 0; k < 3; k++)
                {
                    if (viaDock) noteBefore.Invoke(null, new object[] { hwnd });
                    ShowWindowAsync(hwnd, 6 /* SW_MINIMIZE */);
                    Pump(1200);
                    _fgResult = null;
                    stillProp.SetValue(null, null);
                    endProp.SetValue(null, null);
                    retargetRun.SetValue(null, null);
                    RestoreFlicker($"{label}-{k + 1}", wr, restore, outDir, out _);
                    flashes.Add(LastFlicker.Flashes);
                    stills.Add(stillProp.GetValue(null) is ITuple st ? $"{st[1]}ms·{st[2]}프레임(찍기 {st[0]}ms, 기준 {st[3]})" : "없음");
                    ends.Add((endProp.GetValue(null) as string ?? "없음") + (retargetRun.GetValue(null) is string rt ? $" · 다시 맞춤 {rt}" : ""));
                    devs.Add(LastFlicker.MaxDev);
                    firsts.Add(LastFlicker.FirstMs);
                    if (_fgResult is { } r) { fgTried++; if (r) fgOk++; }
                    if (IsIconic(hwnd)) { ShowWindowAsync(hwnd, 9); Pump(600); }
                    Pump(400);
                }
                string line = $"{label}: 번쩍임 프레임 {string.Join("/", flashes)} (가장 크게 벗어난 밝기 {string.Join("/", devs.Select(d => d.ToString("0")))}), 누른 뒤 첫 변화 {string.Join("/", firsts)}ms{(fgTried > 0 ? $", 앞으로 가져오기 성공 {fgOk}/{fgTried}" : "")}, 붙잡은 그림 {string.Join(" / ", stills)}, 끝 자리 차이(진짜 창−효과 끝 px) {string.Join(" / ", ends)}";
                Console.WriteLine("  정보  " + line);
                Results.Add("정보  " + line);
                lines.Add(line);
            }
            Action restoreOnly = () => ShowWindowAsync(hwnd, 9);
            Action restoreAndActivate = () =>
            {
                ShowWindowAsync(hwnd, 9);
                SetForegroundWindow(hwnd);
                var h = hwnd;
                _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(async () => { await Task.Delay(300); _fgResult = GetForegroundWindow() == h; });
            };

            // A·A2: 효과 끔, 윈도우 애니메이션 켬 (시작 값이 켜짐일 때)
            SetSysAnimation(true);
            Run("A 효과 끔 (윈도우 애니메이션 켬)", restoreOnly);
            Run("A2 효과 끔 + 앞으로 가져오기", restoreAndActivate);
            // B: 효과 끔 + 윈도우 애니메이션 끔
            SetSysAnimation(false);
            Run("B 효과 끔 + 윈도우 애니메이션 끔", restoreOnly);
            // C·D·D2: 몽독 효과 (컨트롤러가 윈도우 애니메이션을 끄고 기록, 끝에 되돌림)
            SetSysAnimation(sysOriginal ?? true);
            settings.Current.Dock.MinimizeSpeed = MinimizeSpeed.Normal;
            settings.Current.Dock.MinimizeEffect = MinimizeEffect.Settle;
            settings.Save();
            controller = mecType.GetConstructors()[0].Invoke(new object[] { services, (Func<IntPtr, Rect?>)(_ => iconPx), (Func<DockEdge>)(() => DockEdge.Bottom) });
            Pump(1500);
            Action viaEffect(Action inner) => () =>
            {
                var hook = restoreHook.GetValue(null) as Func<IntPtr, Action, bool>;
                if (hook?.Invoke(hwnd, inner) != true) inner();
            };
            Run("C 차분하게", viaEffect(restoreOnly));
            settings.Current.Dock.MinimizeEffect = MinimizeEffect.Genie;
            settings.Save();
            Pump(300);
            Run("D 지니", viaEffect(restoreOnly));
            Run("D2 지니 + 앞으로 가져오기", viaEffect(restoreAndActivate));
            // D3: 최대화 창 (#27 사용자: 보통 창·최대화 둘 다 「커졌다가 크기 조정」) — 되살리면 최대화로 돌아옴
            ShowWindowAsync(hwnd, 3 /* SW_MAXIMIZE */);
            Pump(1500);
            GetWindowRect(hwnd, out wr);
            Run("D3 지니 · 최대화 창", viaEffect(restoreOnly));
            ShowWindowAsync(hwnd, 9 /* SW_RESTORE */);
            Pump(800);
            // F: 작업 영역 왼쪽 반·오른쪽 반 (스냅 자리 흉내 — 진짜 스냅 상태는 키 입력 없이 못 만듦), 독처럼 최소화 직전 기록 → 끝 자리 차이 0~1px 이어야 함
            {
                var pm = Monitors.GetPrimary();
                var wa = new Rect(pm.WorkArea.X * pm.Scale, pm.WorkArea.Y * pm.Scale, pm.WorkArea.Width * pm.Scale, pm.WorkArea.Height * pm.Scale); // 물리 px
                int half = (int)(wa.Width / 2), hh = (int)wa.Height;
                viaDock = true;
                foreach (var (fl, fx) in new[] { ("F 왼쪽 반", (int)wa.Left), ("F2 오른쪽 반", (int)wa.Left + half) })
                {
                    // 보이는 자리가 반쪽이 되도록 창 사각형은 보이지 않는 테두리만큼 넓게 (보통 창의 스냅과 같은 모양)
                    double inv = (double)mecType.GetMethod("InvisibleBorder", Any)!.Invoke(null, new object[] { hwnd })!;
                    SetWindowPos(hwnd, IntPtr.Zero, (int)(fx - inv), (int)wa.Top, (int)(half + 2 * inv), (int)(hh + inv), 0x0014 /* NOZORDER|NOACTIVATE */);
                    Pump(1000);
                    GetWindowRect(hwnd, out wr);
                    Run(fl, viaEffect(restoreOnly));
                }
                // G: 기억을 일부러 틀리게 (최소화가 끝난 뒤 기억한 자리를 400px 옮김 — 키보드 스냅 뒤 늦은 훅 같은 경우) → 안전망이 진짜 자리로 다시 맞춰 끝 자리 0~1px 이어야 함
                {
                    var remField = mecType.GetField("Remembered", Any)!;
                    var dict = (System.Collections.IDictionary)remField.GetValue(null)!;
                    var retargetProp = mecType.GetProperty("LastRetargetForTest", Any)!;
                    var endP = mecType.GetProperty("LastEndMismatchForTest", Any)!;
                    var gl = new List<string>();
                    for (int k = 0; k < 3; k++)
                    {
                        noteBefore.Invoke(null, new object[] { hwnd });
                        ShowWindowAsync(hwnd, 6 /* SW_MINIMIZE */);
                        Pump(1200);
                        bool corrupted = false;
                        if (dict.Contains(hwnd))
                        {
                            object v = dict[hwnd]!; // (Rect Visible, Point Offset, RECT Normal, bool Maximized, RECT Work) — 박싱된 복사본의 Item1 을 바꿔 다시 넣음
                            var f1 = v.GetType().GetField("Item1")!;
                            var r = (Rect)f1.GetValue(v)!;
                            f1.SetValue(v, new Rect(r.X - 400, r.Y + 40, r.Width, r.Height));
                            dict[hwnd] = v;
                            corrupted = true;
                        }
                        retargetProp.SetValue(null, null);
                        endP.SetValue(null, null);
                        RestoreFlicker($"G 틀린 기억-{k + 1}", wr, viaEffect(restoreOnly), outDir, out _);
                        gl.Add($"{(corrupted ? "" : "(기억 없음) ")}번쩍임 {LastFlicker.Flashes}, 다시 맞춤 {retargetProp.GetValue(null) as string ?? "없음"}, 끝 자리 {endP.GetValue(null) as string ?? "없음"}");
                        if (IsIconic(hwnd)) { ShowWindowAsync(hwnd, 9); Pump(600); }
                        Pump(400);
                    }
                    string gline = "G 틀린 기억(왼쪽으로 400px): " + string.Join(" / ", gl);
                    Console.WriteLine("  정보  " + gline);
                    Results.Add("정보  " + gline);
                    lines.Add(gline);
                }
                viaDock = false;
                ShowWindowAsync(hwnd, 9 /* SW_RESTORE */);
                Pump(600);
            }
            // E: 붙잡은 그림이 보이는 동안 vs 걷힌 뒤 내용 차이 (#27 사용자: 다 켜진 뒤 잠시 창이 움직이는 느낌) — 픽셀 이동량
            GetWindowRect(hwnd, out wr);
            for (int k = 0; k < 3; k++) lines.Add(ContentShift($"E 내용 비교-{k + 1}", hwnd, wr, viaEffect(restoreOnly), outDir));
            ShowWindowAsync(hwnd, 3 /* SW_MAXIMIZE */);
            Pump(1500);
            GetWindowRect(hwnd, out wr);
            for (int k = 0; k < 2; k++) lines.Add(ContentShift($"E2 최대화 내용 비교-{k + 1}", hwnd, wr, viaEffect(restoreOnly), outDir));
            ShowWindowAsync(hwnd, 9 /* SW_RESTORE */);
            Pump(800);
            ((IDisposable)controller).Dispose();
            controller = null;
            File.WriteAllLines(Path.Combine(outDir, "flash-matrix.txt"), lines);
        }
        finally
        {
            if (controller is IDisposable d) d.Dispose();
            SetSysAnimation(sysOriginal ?? true);
            if (electron)
            {
                if (hwnd != IntPtr.Zero) { GetWindowThreadProcessId(hwnd, out uint epid); try { Process.Start(new ProcessStartInfo("taskkill", $"/PID {epid} /T /F") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000); } catch { } }
                try { Process.Start(new ProcessStartInfo("taskkill", $"/PID {child.Id} /T /F") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000); } catch { }
                Pump(1000);
                try { if (electronData is not null) Directory.Delete(electronData, true); } catch { }
            }
            else try { if (!child.HasExited) { child.CloseMainWindow(); if (!child.WaitForExit(3000)) child.Kill(); } } catch { }
            if (madeDesktop is { } md && md != Guid.Empty)
            {
                Pump(500);
                var ct = (Task<bool>)typeof(VirtualDesktopService).GetMethod("CloseCurrentIfAsync", Any)!.Invoke(null, new object[] { md })!;
                while (!ct.IsCompleted) Pump(50);
                bool closed = ct.Result;
                Pump(800);
                Console.WriteLine($"  시험용 데스크톱 닫음 {closed}");
            }
            Pump(300);
            Check($"끝: 윈도우 애니메이션 시작 값 그대로 ({SysAnimation()} = {sysOriginal})", SysAnimation() == sysOriginal);
        }
    }

    private static bool? _fgResult;

    /// <summary>윈도우 「최소화·최대화 애니메이션」을 이번 로그온 동안만 바꿈 (파일에 쓰지 않음 — SPIF_SENDCHANGE 만).</summary>
    private static void SetSysAnimation(bool on)
    {
        var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = on ? 1 : 0 };
        SystemParametersInfo(0x0049 /* SPI_SETANIMATION */, info.cbSize, ref info, 0x02 /* SPIF_SENDCHANGE */);
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
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool SetWindowPosF(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    private static bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags) => SetWindowPosF(h, after, x, y, w, hgt, flags);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr h, int a, out int v, int s);

    /// <summary>E 내용 비교용 페이지: 글줄·가는 선·색 막대 (흰 바탕만이면 이동을 잴 수 없음).</summary>
    private static string ContentPage()
    {
        var sb = new System.Text.StringBuilder("<!doctype html><meta charset=utf-8><title>mongdock 27</title><body style='margin:0;font:15px Segoe UI;background:#fff'>");
        for (int i = 0; i < 60; i++)
            sb.Append($"<div style='height:24px;border-bottom:1px solid #ccc;display:flex;align-items:center'><span style='width:{40 + i * 7 % 300}px;height:10px;background:hsl({i * 37 % 360},60%,55%);margin:0 12px'></span>줄 {i} — 몽독 최소화 되살리기 내용 비교 abcdefghijklmnopqrstuvwxyz 0123456789</div>");
        return sb.Append("</body>").ToString();
    }

    /// <summary>
    /// 화면을 세 번 찍어 비교 (물리 px, 창 안쪽 가운데 부분): ① 최소화 전 ② 되살린 뒤 붙잡은 그림이 보일 때(효과 시작 +520ms) ③ 걷힌 뒤(+1400ms).
    /// 각 쌍의 「제자리 평균 차이」와 「가장 잘 맞는 이동(dx,dy)과 그때 차이」 — 내용이 밀렸으면 이동이 0 이 아니고, 다시 그려 바뀌었으면 이동해도 차이가 남음. PNG 도 남김.
    /// </summary>
    private static string ContentShift(string name, IntPtr hwnd, RECTL wr, Action restore, string outDir)
    {
        double scale = Monitors.GetPrimary().Scale;
        var area = new Rect((wr.Left + 60) / scale, (wr.Top + 120) / scale, Math.Max(200, wr.Right - wr.Left - 120) / scale, Math.Max(200, wr.Bottom - wr.Top - 200) / scale);
        int w = (int)(area.Width * scale), h = (int)(area.Height * scale);
        var before = Grab(area);
        ShowWindowAsync(hwnd, 6 /* SW_MINIMIZE */);
        Pump(1200);
        var sw = Stopwatch.StartNew();
        restore();
        while (sw.ElapsedMilliseconds < 520) Pump(5);
        var still = Grab(area);
        while (sw.ElapsedMilliseconds < 1400) Pump(20);
        var after = Grab(area);
        if (IsIconic(hwnd)) { ShowWindowAsync(hwnd, 9); Pump(600); }
        Pump(400);
        foreach (var (tag, px) in new[] { ("before", before), ("still", still), ("after", after) })
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(outDir, $"content27-{name}-{tag}.png"));
            enc.Save(fs);
        }
        var stillProp = typeof(Mongdock.App).Assembly.GetType("Mongdock.Views.MinimizeEffectController")!.GetProperty("LastStillForTest", Any)!;
        string held = stillProp.GetValue(null) is ITuple st ? $"{st[1]}ms" : "없음";
        string line = $"{name}: 그림↔걷힌 뒤 {Compare(still, after, w, h)} · 최소화 전↔걷힌 뒤 {Compare(before, after, w, h)} · 최소화 전↔그림 {Compare(before, still, w, h)} (붙잡은 그림 {held})";
        Console.WriteLine("  정보  " + line);
        Results.Add("정보  " + line);
        return line;
    }

    /// <summary>두 장면(BGRA) 회색 비교: 제자리 평균 차이, ±16px 안에서 가장 잘 맞는 이동과 그때 평균 차이 (4px 간격으로 표본).</summary>
    private static string Compare(byte[] a, byte[] b, int w, int h)
    {
        double Gray(byte[] p, int x, int y) { int i = (y * w + x) * 4; return 0.114 * p[i] + 0.587 * p[i + 1] + 0.299 * p[i + 2]; }
        const int R = 16, Step = 4;
        double Sad(int dx, int dy)
        {
            double sum = 0; int n = 0;
            for (int y = R; y < h - R; y += Step)
                for (int x = R; x < w - R; x += Step) { sum += Math.Abs(Gray(a, x, y) - Gray(b, x + dx, y + dy)); n++; }
            return n == 0 ? 0 : sum / n;
        }
        double zero = Sad(0, 0), best = zero; int bx = 0, by = 0;
        for (int dy = -R; dy <= R; dy++)
            for (int dx = -R; dx <= R; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                double v = Sad(dx, dy);
                if (v < best - 0.05) { best = v; bx = dx; by = dy; }
            }
        return $"제자리 {zero:0.00}, 가장 잘 맞는 이동 ({bx},{by}) {best:0.00}";
    }

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
        // 첫 변화 = 누른 뒤 창 자리 밝기가 처음 바뀐 프레임 (덮개 첫 프레임 또는 실제 창)
        var beforeV = frames.Where(x => x.T < at).Select(x => x.V).LastOrDefault() ?? during[0].V;
        var firstChange = during.FirstOrDefault(x => Enumerable.Range(0, boxes.Length).Any(k => Math.Abs(x.V[k] - beforeV[k]) > 3));
        long firstMs = firstChange.V is null ? -1 : firstChange.T - at;
        int arrived = during.FindIndex(x => Enumerable.Range(0, boxes.Length).All(k => Math.Abs(x.V[k] - end[k]) < 4));
        var after = arrived < 0 ? new List<(long T, double[] V)>() : during.Skip(arrived).Where(x => Enumerable.Range(0, boxes.Length).Any(k => Math.Abs(x.V[k] - end[k]) > 10)).ToList();
        flickered = arrived < 0 || after.Count > 0;
        double maxDev = arrived < 0 ? 0 : during.Skip(arrived).Max(x => Enumerable.Range(0, boxes.Length).Max(k => Math.Abs(x.V[k] - end[k])));
        LastFlicker = (firstMs, arrived < 0 ? -1 : during[arrived].T - at, after.Count, maxDev);
        return $"{name}: 되살리기 끝 깜빡임 없음 (누른 뒤 첫 변화 {firstMs}ms, 프레임 {during.Count}개, 끝 모습에 닿음 {(arrived < 0 ? "없음" : $"{during[arrived].T - at}ms")}, 그 뒤 벗어난 프레임 {after.Count}개{(after.Count > 0 ? ": " + string.Join(" ", after.Select(x => $"{x.T - at}ms")) : "")})";
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
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);

    private static List<IntPtr> TopWindowsOfClass(string cls)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); if (sb.ToString() == cls) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    private static string WindowTitle(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
}
