using System.Diagnostics;
using System.IO;
using System.Windows;
using Mongdock.Services;

namespace LiveTest;


/// <summary>
/// --soundpanel (#21 넘김 끊김): 실제 소리 판을 화면 밖(왼쪽 -4000)에 띄워 세부 화면 넘김을 움직임과 함께 5번씩 해 보고
/// 첫 프레임까지·가장 긴 프레임 간격을 잼 (판의 로그와 같은 값). 사용자 화면에는 아무것도 안 보이고 입력도 없음.
/// 판을 열 때 앱 목록 읽기·세부 화면 미리 만들기에 드는 시간·메모리도.
/// </summary>
internal static partial class Program
{
    private static void SoundPanelRun(AppServices services, string outDir)
    {
        var asm = typeof(Mongdock.App).Assembly;
        var panelType = asm.GetType("Mongdock.Views.StatusPanelWindow")!;
        var kindType = asm.GetType("Mongdock.Views.StatusPanelKind")!;
        services.Status.Start();
        Pump(1500);
        var palette = Mongdock.ViewModels.UiTheme.Palette(services.Settings.Current);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long mem0 = Process.GetCurrentProcess().PrivateMemorySize64;
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var w = (Window)panelType.GetConstructors(Any)[0].Invoke(new object[] { services, Enum.Parse(kindType, "Volume"), palette, 0 });
        w.Left = -4000;
        w.Top = 100;
        w.ShowActivated = false;
        var sw = Stopwatch.StartNew();
        w.Show();
        // 실제 마우스 훅에서 떼고(사용자가 PC 를 써도 닫히지 않게) 「켜져 있음」만 남겨, 아래에서 넣어 주는 자리만 받게
        {
            var watch0 = panelType.GetField("_watch", Any)!.GetValue(w)!;
            Pump(300);
            watch0.GetType().GetMethod("Stop")!.Invoke(watch0, null);
            watch0.GetType().GetField("_running", Any)!.SetValue(watch0, true);
        }
        Pump(1500); // 앱 목록이 채워져 판이 다 커질 때까지
        // 회귀 재현 (0.8.2 설치본: 「앱별 볼륨 ›」을 누르면 판이 닫힘): 감시자를 켠 채로 각 › 줄 가운데를 누른 것처럼(감시자에 그 자리 전달 → 버튼 Click)
        {
            var watch = panelType.GetField("_watch", Any)!.GetValue(w)!;
            var down = watch.GetType().GetMethod("OnGlobalMouseDown", Any)!;
            var startedAt = watch.GetType().GetField("_startedAt", Any)!;
            foreach (var label in new[] { "앱별 볼륨", "스피커:", "마이크:" })
            {
                var btn = Descendants(w).OfType<System.Windows.Controls.Button>().FirstOrDefault(b => Descendants(b).OfType<System.Windows.Controls.TextBlock>().Any(tb => tb.Text.StartsWith(label, StringComparison.Ordinal)));
                Console.WriteLine($"  정보  '{label}' 줄 {(btn is null ? "없음" : $"보임 {btn.IsVisible}")}, 판 보임 {w.IsVisible}");
                if (btn is null || !btn.IsVisible)
                {
                    // 마이크가 꺼져 있으면(이 PC 설정) 「마이크:」 줄 대신 「음소거됨」 — 실패 아님
                    bool micOff = label == "마이크:" && services.Status.InputMuted;
                    Check($"› 줄 '{label}' 찾음{(micOff ? " (마이크 꺼짐이라 없음)" : "")}", micOff);
                    continue;
                }
                var center = btn.PointToScreen(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));
                var dip = PresentationSource.FromVisual(btn)!.CompositionTarget!.TransformFromDevice.Transform(center);
                startedAt.SetValue(watch, Environment.TickCount64 - 10_000); // 연 직후 무시 시간 지남
                down.Invoke(watch, new object?[] { null, (Point?)dip });
                Pump(50);
                bool openAfterDown = w.IsVisible;
                btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(500);
                Check($"'{label} ›' 누름: 판이 닫히지 않음 (누른 순간 {openAfterDown}, 넘긴 뒤 {w.IsVisible})", openAfterDown && w.IsVisible);
                if (!w.IsVisible) return;
                panelType.GetMethod("OpenSoundPageForTest", Any)!.Invoke(w, new object[] { "None" });
                Pump(500);
            }
        }
        // 몽독 자신의 창이 앞으로 와도(덮개·툴팁·이름표 등) 판이 닫히지 않음
        {
            var watch = panelType.GetField("_watch", Any)!.GetValue(w)!;
            var act = watch.GetType().GetMethod("OnWindowActivated", Any)!;
            var own = new Window { Left = -4000, Top = 500, Width = 50, Height = 50, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            own.Show();
            act.Invoke(watch, new object?[] { null, new System.Windows.Interop.WindowInteropHelper(own).Handle });
            Pump(300);
            Check($"몽독 창 활성화: 판이 닫히지 않음 ({w.IsVisible})", w.IsVisible);
            own.Close();
        }
        // 넘김 측정 동안은 감시자를 완전히 멈춤
        panelType.GetField("_watch", Any)!.GetValue(w)!.GetType().GetField("_running", Any)!.SetValue(panelType.GetField("_watch", Any)!.GetValue(w), false);
        Pump(1700); // 판이 뜨고 앱 목록을 읽어 채울 시간
        var cpuOpen = Process.GetCurrentProcess().TotalProcessorTime - cpu0;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long mem1 = Process.GetCurrentProcess().PrivateMemorySize64;
        Console.WriteLine($"  정보  판 열기 2초: CPU {cpuOpen.TotalMilliseconds:0}ms, 전용 메모리 +{(mem1 - mem0) / 1048576.0:0.0}MB");
        Results.Add($"정보  판 열기 2초: CPU {cpuOpen.TotalMilliseconds:0}ms, 전용 메모리 +{(mem1 - mem0) / 1048576.0:0.0}MB");
        var stats = asm.GetType("Mongdock.Views.StatusPanelWindow")!.GetProperty("LastSoundTransition", Any)!;
        var open = panelType.GetMethod("OpenSoundPageAnimatedForTest", Any)!;
        long baseGap = 50;
        // 비교: 같은 PC에서 아주 작은 투명 창(화면 밖)의 단순 움직임 프레임 간격 — 환경(화면 갱신·부하) 기준선
        {
            var probe = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = 700, Width = 100, Height = 60 };
            var box = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.SteelBlue, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
            var tt = new System.Windows.Media.TranslateTransform();
            box.RenderTransform = tt;
            probe.Content = box;
            probe.Show();
            Pump(300);
            var gapsP = new List<long>();
            for (int i = 0; i < 5; i++)
            {
                var swp = Stopwatch.StartNew();
                long lastP = -1, maxP = 0;
                EventHandler h = (_, _) => { long n = swp.ElapsedMilliseconds; if (lastP >= 0) maxP = Math.Max(maxP, n - lastP); lastP = n; };
                System.Windows.Media.CompositionTarget.Rendering += h;
                tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 50, TimeSpan.FromMilliseconds(220)));
                Pump(300);
                System.Windows.Media.CompositionTarget.Rendering -= h;
                gapsP.Add(maxP);
                Pump(200);
            }
            probe.Close();
            gapsP.Sort();
            baseGap = gapsP[gapsP.Count / 2];
            Console.WriteLine($"  정보  기준선(작은 창 단순 움직임) 가장 긴 간격 {string.Join("/", gapsP)}ms");
            Results.Add($"정보  기준선(작은 창 단순 움직임) 가장 긴 간격 {string.Join("/", gapsP)}ms");
        }
        foreach (var page in new[] { "Apps", "Output", "Input" })
        {
            var firsts = new List<long>();
            var gaps = new List<long>();
            for (int i = 0; i < 5; i++)
            {
                if (!w.IsVisible) { Check("소리 판이 열려 있음 (밖을 눌러 닫혔으면 다시)", false); return; }
                stats.SetValue(null, null);
                open.Invoke(w, new object[] { page });
                Pump(500);
                if (stats.GetValue(null) is ValueTuple<long, long, int> t) { firsts.Add(t.Item1); gaps.Add(t.Item2); }
                open.Invoke(w, new object[] { "None" });
                Pump(500);
            }
            firsts.Sort();
            gaps.Sort();
            string line = $"{page} 넘김 5번: 첫 프레임 {string.Join("/", firsts)}ms (가운데 {(firsts.Count > 0 ? firsts[firsts.Count / 2] : -1)}), 가장 긴 간격 {string.Join("/", gaps)}ms";
            Console.WriteLine("  정보  " + line);
            Results.Add("정보  " + line);
            // 가장 긴 간격은 같은 PC 기준선(작은 창 단순 움직임) + 15ms 안 — 이 PC 는 원격 화면이라 작은 창도 30~50ms
            Check($"{page}: 첫 프레임 모두 50ms 안 (가장 큰 {(firsts.Count > 0 ? firsts[^1] : -1)}ms), 가장 긴 간격 기준선 {baseGap}ms + 15 안 (가운데 {(gaps.Count > 0 ? gaps[gaps.Count / 2] : -1)}ms)",
                firsts.Count == 5 && firsts[^1] <= 50 && gaps[gaps.Count / 2] <= baseGap + 15);
        }
        w.Close();
        Pump(300);
        foreach (var kind in new[] { "ControlCenter", "Wifi", "Bluetooth", "Calendar", "Battery", "Tray" })
        {
            var p2 = (Window)panelType.GetConstructors(Any)[0].Invoke(new object[] { services, Enum.Parse(kindType, kind), palette, 0 });
            p2.Left = -4000; p2.Top = 100; p2.ShowActivated = false;
            p2.Show();
            var watch2 = panelType.GetField("_watch", Any)!.GetValue(p2)!;
            Pump(150);
            watch2.GetType().GetMethod("Stop")!.Invoke(watch2, null);
            Pump(1500);
            var card = (FrameworkElement)panelType.GetField("_card", Any)!.GetValue(p2)!;
            Check($"짧은 확인: {kind} 판 열림·2초 버팀 (보임 {p2.IsVisible}, 카드 {card.ActualWidth:0}×{card.ActualHeight:0})", p2.IsVisible && card.ActualHeight > (kind == "Tray" ? 10 : 40)); // 트레이 판은 아이콘이 없으면 작음
            p2.Close();
            Pump(200);
        }
    }
}
