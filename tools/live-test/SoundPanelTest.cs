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
        Pump(300);
        // 화면 밖 판이라 다른 창이 앞에 오거나 사용자가 다른 곳을 눌러도 닫히지 않게 바깥 클릭 감시를 멈춤
        panelType.GetField("_watch", Any)!.GetValue(w)!.GetType().GetMethod("Stop")!.Invoke(panelType.GetField("_watch", Any)!.GetValue(w), null);
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
    }
}
