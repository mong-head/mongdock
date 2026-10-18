using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mongdock.Services;

/// <summary>
/// 본체 쪽 미디어 서비스 (#37 격리): 미디어 세션은 자식 프로세스(<see cref="MediaHost"/>)에서만 다루고, 여기는 파이프로 받은 상태를 보여 주고 조작만 보냄.
/// 자식이 죽으면(미디어 세션 쪽 힙 손상 등) 상태를 비우고 2·5·15·30·60초 뒤 다시 띄움 — 10분에 6번 넘게 죽으면 이번 실행에서는 그만둠(재생 표시·미디어 판만 빔).
/// 위치는 받은 값에서 흐른 시간만큼 이어 셈(재생 중일 때), 도우미가 5초마다 고쳐 줌.
/// 되돌리기: 환경 변수 MONGDOCK_MEDIA_INPROC=1 이면 예전처럼 본체 안 <see cref="MediaService"/> (App).
/// </summary>
public sealed class MediaHostClient : IMediaService, IDisposable
{
    private static readonly int[] RestartDelaysSec = { 2, 5, 15, 30, 60 };
    private const int MaxRestarts = 6;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    private Dispatcher? _dispatcher;
    private bool _started;
    private Process? _host;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private readonly object _writeGate = new();
    private int _generation;
    private readonly List<DateTime> _deaths = new();
    private DispatcherTimer? _restartTimer;
    private DispatcherTimer? _aliveTimer;
    private DateTime _lastLineAt = DateTime.UtcNow;

    private MediaSnapshot _snap = new();
    private DateTime _snapAt = DateTime.UtcNow;

    public bool HasSession => _snap.HasSession;
    public string? SourceApp => _snap.SourceApp;
    public string? Title => _snap.Title;
    public string? Artist => _snap.Artist;
    public ImageSource? Thumbnail { get; private set; }
    public bool IsPlaying => _snap.IsPlaying;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_snap.DurationMs);
    public TimeSpan Position => Extrapolate(_snap.PositionMs, _snap.DurationMs, _snap.IsPlaying);
    public IReadOnlyList<string> PlayingApps { get; private set; } = Array.Empty<string>();
    public event EventHandler? Changed;
    public event EventHandler? PlayingChanged;

    public (TimeSpan Position, TimeSpan Duration)? PlayingTime(string sourceApp)
    {
        var t = _snap.Playing.FirstOrDefault(p => string.Equals(p.Source, sourceApp, StringComparison.OrdinalIgnoreCase));
        if (t is null || t.PositionMs is not long pos) return null;
        long dur = t.DurationMs ?? 0;
        return (Extrapolate(pos, dur, true), TimeSpan.FromMilliseconds(dur));
    }

    private TimeSpan Extrapolate(long posMs, long durMs, bool running)
    {
        double ms = posMs + (running ? (DateTime.UtcNow - _snapAt).TotalMilliseconds : 0);
        if (ms < 0) ms = 0;
        if (durMs > 0 && ms > durMs) ms = durMs;
        return TimeSpan.FromMilliseconds(ms);
    }

    public Task PlayPauseAsync() => SendAsync("playpause");
    public Task NextAsync() => SendAsync("next");
    public Task PreviousAsync() => SendAsync("prev");

    // 쓰기는 백그라운드에서, 잠금은 1초까지만 기다림 — 도우미가 멈춰도 본체(특히 끝낼 때 UI 스레드)가 묶이지 않게 (리뷰)
    private Task SendAsync(string cmd)
    {
        var w = _writer;
        if (w is null) return Task.CompletedTask;
        return Task.Run(() =>
        {
            if (!Monitor.TryEnter(_writeGate, 1000)) { Log.Warn($"미디어 도우미에 「{cmd}」 못 보냄 (쓰기 막힘)"); return; }
            try { w.WriteLine(cmd); }
            catch (Exception ex) { Log.Warn($"미디어 도우미에 「{cmd}」 보내기 실패: {ex.GetType().Name}"); }
            finally { Monitor.Exit(_writeGate); }
        });
    }

    // ───────────────────────── 시작·정지 ─────────────────────────

    public void Start()
    {
        if (_started) return;
        _started = true;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // 도우미가 살아 있는지: 3초마다 한 줄씩 와야 함 — 15초 넘게 아무것도 안 오면(미디어 세션 호출에서 멈춤) 끝내고 다시 띄움 (리뷰)
        _aliveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        _aliveTimer.Tick += (_, _) =>
        {
            if (_writer is null || _host is not { } h) return;
            if (DateTime.UtcNow - _lastLineAt < TimeSpan.FromSeconds(15)) return;
            Log.Warn("미디어 도우미가 15초 넘게 응답 없음 → 끝내고 다시 띄움");
            try { h.Kill(); } catch { }
        };
        _aliveTimer.Start();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Launch();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _generation++; // 큐에 남은 옛 연결의 상태·쓰기 설정을 무시 (리뷰)
        _restartTimer?.Stop();
        _aliveTimer?.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Shutdown(kill: true);
        Reset();
    }

    public void Dispose() => Stop();

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) _lastLineAt = DateTime.UtcNow; // 리뷰: 절전 시간을 응답 없음으로 세지 않게
    }

    private void Launch()
    {
        int gen = ++_generation;
        string name = $"mongdock-media-{Environment.ProcessId}-{Guid.NewGuid():N}"[..48];
        try
        {
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _host = StartHost($"{MediaHost.Arg} {name} {Environment.ProcessId}");
            _host.EnableRaisingEvents = true; // 연결 전에 끝나도 15초를 기다리지 않고 바로 알아챔 (리뷰)
            var started = _host;
            _host.Exited += (_, _) => _dispatcher?.BeginInvoke(() => OnHostGone(gen, started, "도우미 프로세스 끝남"));
        }
        catch (Exception ex)
        {
            Log.Warn($"미디어 도우미 시작 실패: {ex.GetType().Name} {ex.Message}");
            Shutdown(kill: true);
            ScheduleRestart(++_generation, "시작 실패");
            return;
        }
        var pipe = _pipe;
        var host = _host;
        Log.Info($"미디어 도우미 시작 (pid {host?.Id})");
        _ = Task.Run(async () =>
        {
            string why = "파이프 끊김";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await pipe.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                _ = _dispatcher!.BeginInvoke(() => { if (gen == _generation) { _writer = writer; _lastLineAt = DateTime.UtcNow; } });
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    _lastLineAt = DateTime.UtcNow;
                    if (line.Length == 0 || line[0] != '{') continue; // "alive"
                    MediaSnapshot? s;
                    try { s = JsonSerializer.Deserialize<MediaSnapshot>(line); }
                    catch { continue; }
                    if (s is null) continue;
                    _ = _dispatcher.BeginInvoke(() => { if (gen == _generation) Apply(s); });
                }
            }
            catch (OperationCanceledException) { why = "15초 안에 연결 안 됨"; }
            catch (Exception ex) { why = ex.GetType().Name; }
            _ = _dispatcher!.BeginInvoke(() => OnHostGone(gen, host, why));
        });
    }

    /// <summary>
    /// 도우미 띄우기: 같은 exe. 스토어판에서 패키지 안 exe 를 직접 못 띄우면 실행 별칭(mongdock.exe)으로 한 번 더 (언어 바꾼 뒤 다시 시작과 같은 사정 — 스토어판 확인 필요).
    /// </summary>
    private static Process StartHost(string args)
    {
        try { return Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false, CreateNoWindow = true })!; }
        catch (Exception ex) when (AppInfo.IsPackaged)
        {
            Log.Warn($"미디어 도우미를 패키지 경로로 못 띄움 ({ex.GetType().Name}) → 실행 별칭으로");
            return Process.Start(new ProcessStartInfo("mongdock.exe", args) { UseShellExecute = false, CreateNoWindow = true })!;
        }
    }

    private void Apply(MediaSnapshot s)
    {
        if (s.Thumb is { } t) Thumbnail = t.Length == 0 ? null : DecodePng(t);
        _snap = s;
        _snapAt = DateTime.UtcNow;
        var playing = s.Playing.Select(p => p.Source).ToList();
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("MediaService.Changed 핸들러 예외", ex); }
        if (!playing.SequenceEqual(PlayingApps, StringComparer.OrdinalIgnoreCase))
        {
            PlayingApps = playing;
            try { PlayingChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("MediaService.PlayingChanged 핸들러 예외", ex); }
        }
    }

    private static ImageSource? DecodePng(string base64)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private void OnHostGone(int gen, Process? host, string why)
    {
        if (gen != _generation) return;
        int next = ++_generation; // 파이프 끊김·프로세스 끝남 두 알림 중 처음 것만
        string code = "";
        try { if (host is not null && host.WaitForExit(300)) code = $", 종료 코드 0x{unchecked((uint)host.ExitCode):X8}"; } catch { }
        Shutdown(kill: true);
        Reset();
        if (!_started) return;
        Log.Warn($"미디어 도우미가 끝남 ({why}{code}) → 다시 띄움 (몽독은 그대로)");
        ScheduleRestart(next, why);
    }

    private void ScheduleRestart(int gen, string why)
    {
        var now = DateTime.Now;
        _deaths.RemoveAll(d => now - d > RestartWindow);
        _deaths.Add(now);
        if (_deaths.Count > MaxRestarts)
        {
            Log.Warn($"미디어 도우미가 {RestartWindow.TotalMinutes:0}분에 {MaxRestarts}번 넘게 끝남 → 이번 실행에서는 다시 안 띄움 (재생 표시·미디어 판 빔)");
            return;
        }
        int delay = RestartDelaysSec[Math.Min(_deaths.Count - 1, RestartDelaysSec.Length - 1)];
        _restartTimer?.Stop();
        _restartTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher!) { Interval = TimeSpan.FromSeconds(delay) };
        _restartTimer.Tick += (_, _) =>
        {
            _restartTimer?.Stop();
            if (_started && gen == _generation) Launch();
        };
        _restartTimer.Start();
    }

    /// <summary>도우미 끝내기: 「quit」를 쓰지 않고 바로 끝냄 (도우미가 멈췄으면 쓰기에서 본체 UI 가 묶일 수 있음 — 리뷰). 파이프가 닫히면 도우미도 스스로 끝남.</summary>
    private void Shutdown(bool kill)
    {
        _writer = null;
        var host = _host;
        _host = null;
        if (kill && host is not null)
        {
            try { if (!host.HasExited) host.Kill(); } catch { }
            host.Dispose();
        }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    /// <summary>상태 비우기 — 바뀌었으면 알림.</summary>
    private void Reset()
    {
        bool had = _snap.HasSession || PlayingApps.Count > 0 || Thumbnail is not null;
        _snap = new MediaSnapshot();
        Thumbnail = null;
        if (!had) return;
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { }
        if (PlayingApps.Count > 0)
        {
            PlayingApps = Array.Empty<string>();
            try { PlayingChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }
}
