using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Mongdock.Services;

namespace LiveTest;

internal static partial class Program
{
    /// <summary>
    /// --mediachild: 무음 WAV 를 소리 0 으로 반복 재생하는 미디어 세션 하나 (MediaPlayer → 윈도우 미디어 세션 목록에 나타남). 끝날 때까지 기다림 — 부모가 강제로 끝냄.
    /// </summary>
    private static int MediaChildMain()
    {
        string wav = Path.Combine(Path.GetTempPath(), "mongdock-silence.wav");
        if (!File.Exists(wav)) WriteSilentWav(wav, seconds: 10);
        var player = new Windows.Media.Playback.MediaPlayer
        {
            Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(wav)),
            IsLoopingEnabled = true,
            Volume = 0,
            IsMuted = true,
        };
        player.Play();
        Thread.Sleep(Timeout.Infinite);
        GC.KeepAlive(player);
        return 0;
    }

    private static void WriteSilentWav(string path, int seconds)
    {
        const int rate = 8000;
        int samples = rate * seconds;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        w.Write(new byte[samples * 2]);
    }

    /// <summary>
    /// --mediasoak 횟수 (#37 장시간 크래시 재현): 몽독 MediaService 를 켜 두고, 무음 미디어 세션 자식 프로세스를 띄웠다가(재생 중으로 보일 때까지) 강제로 끝내기를 반복.
    /// 매번 재생 앱 목록·재생 시간을 읽고(0.8 경로), 10번마다 GC + 종료자 실행. 시험 프로세스가 끝까지 살아 있으면 통과.
    /// 실제 소리 없음(소리 0·음소거·무음 파일), 창·입력 없음.
    /// </summary>
    private static void MediaSoak(int rounds)
    {
        var media = new MediaService();
        media.Start();
        Pump(1500);
        int seen = 0, broken = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++)
        {
            var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--mediachild") { UseShellExecute = false, CreateNoWindow = true })!;
            bool playing = false;
            for (int k = 0; k < 40 && !playing; k++)
            {
                Pump(50);
                playing = media.PlayingApps.Any(a => a.Contains("live-test", StringComparison.OrdinalIgnoreCase));
                foreach (var a in media.PlayingApps) _ = media.PlayingTime(a);
            }
            if (playing) seen++;
            try { child.Kill(); child.WaitForExit(3000); } catch { }
            for (int k = 0; k < 6; k++)
            {
                Pump(50);
                foreach (var a in media.PlayingApps) _ = media.PlayingTime(a);
            }
            if (i % 10 == 9)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var p = Process.GetCurrentProcess();
                Console.WriteLine($"  {i + 1}/{rounds}: 재생으로 보임 {seen}, 전용 {p.PrivateMemorySize64 / 1048576}MB, 핸들 {p.HandleCount}, {sw.Elapsed.TotalSeconds:0}초");
            }
        }
        string logPath = Path.Combine(Environment.GetEnvironmentVariable("MONGDOCK_DATA_DIR")!, "logs", "mongdock.log");
        try { broken = File.ReadLines(logPath).Count(l => l.Contains("미디어 세션이 깨짐") || l.Contains("재생 앱 읽기 실패")); } catch { }
        media.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Check($"미디어 세션 {rounds}번 생성·강제 종료 뒤 살아 있음 (재생으로 보임 {seen}, 깨진 세션 로그 {broken})", true);
        Check("자식 미디어 세션이 재생으로 보임 (시험이 경로를 탔는지)", seen > rounds / 2);
    }
}
