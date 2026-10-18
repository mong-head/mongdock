using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace LiveTest;

/// <summary>
/// --overlaytiming (#27 독 클릭 되살리기 "빠방"): 최소화 효과 덮개를 화면 밖(x=-4000 모니터 크기 칸)에서 돌려
/// 덮개 시작(Run) 동기 시간·첫 프레임까지 시간을 잼. 썸네일 원본은 화면 밖 시험 창(다른 프로세스). 사용자 화면에는 아무것도 안 보이고 입력도 없음.
/// 처음 한 번(차가운 시작)과 이어서 5번(따뜻한 시작)을 따로.
/// </summary>
internal static partial class Program
{
    private static void OverlayTimingRun(AppServices services)
    {
        var asm = typeof(Mongdock.App).Assembly;
        var mec = asm.GetType("Mongdock.Views.MinimizeEffectController")!;
        var overlayType = mec.GetNestedType("OverlayWindow", Any)!;
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--testwindow --offscreen") { UseShellExecute = false })!;
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++) { Pump(100); hwnd = FindWindow(null, TestWindowTitle); }
        try
        {
            if (hwnd == IntPtr.Zero) { Check("시험 창 뜸", false); return; }
            Pump(800);
            // 컨트롤러가 미리 만들어 둔 덮개가 있으면 그것(실제 앱과 같게), 없으면 새로
            object? controller = null;
            if (Environment.GetEnvironmentVariable("OVERLAY_VIA_CONTROLLER") == "1")
            {
                services.Settings.Current.Dock.MinimizeEffect = MinimizeEffect.Genie;
                services.Settings.Save();
                controller = mec.GetConstructors()[0].Invoke(new object[] { services, (Func<IntPtr, Rect?>)(_ => null), (Func<DockEdge>)(() => DockEdge.Bottom) });
                Pump(1500); // 미리 만들기 (있다면)
            }
            object overlay = controller is not null && mec.GetField("_overlay", Any)!.GetValue(controller) is { } pre ? pre : overlayType.GetConstructors()[0].Invoke(new object[] { services });
            Console.WriteLine($"  정보  덮개: {(controller is null ? "새로 만듦" : mec.GetField("_overlay", Any)!.GetValue(controller) is null ? "컨트롤러가 아직 안 만듦 → 새로" : "컨트롤러가 미리 만든 것")}");
            var run = overlayType.GetMethod("Run", Any)!;
            var monitorPx = new Rect(-4000, 0, 1920, 1080);
            var from = new Rect(-4000 + 500, 150, 900, 540);
            var to = new Rect(-4000 + 930, 1000, 64, 64);
            var lines = new List<string>();
            foreach (var (shape, label) in new[] { (MinimizeEffect.Genie, "지니"), (MinimizeEffect.Scale, "아이콘으로"), (MinimizeEffect.Settle, "차분하게") })
            {
                var syncs = new List<double>();
                var firsts = new List<double>();
                for (int k = 0; k < 6; k++)
                {
                    var sw = Stopwatch.StartNew();
                    double first = -1;
                    EventHandler h = (_, _) => { if (first < 0) first = sw.Elapsed.TotalMilliseconds; };
                    CompositionTarget.Rendering += h;
                    bool ok = (bool)run.Invoke(overlay, new object?[] { hwnd, monitorPx, from, to, shape, DockEdge.Bottom, true, 240, (Action)(() => { }) })!;
                    double sync = sw.Elapsed.TotalMilliseconds;
                    Pump(600);
                    CompositionTarget.Rendering -= h;
                    syncs.Add(sync);
                    firsts.Add(first);
                    if (!ok) Console.WriteLine("  정보  Run 이 false (썸네일 못 만듦)");
                }
                string line = $"{label}: 시작 동기 {string.Join("/", syncs.Select(x => x.ToString("0.0")))}ms, 첫 프레임 {string.Join("/", firsts.Select(x => x.ToString("0")))}ms (첫 번째가 차가운 시작)";
                Console.WriteLine("  정보  " + line);
                Results.Add("정보  " + line);
                lines.Add(line);
            }
            if (controller is IDisposable d) d.Dispose();
            else ((Window)overlay).Close();
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(); } catch { }
        }
    }
}
