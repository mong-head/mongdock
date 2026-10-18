using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Mongdock.Services;

/// <summary>
/// 미디어 세션 도우미 프로세스 (#37 근본 해결): 윈도우 미디어 세션(GSMTC, WinRT)은 닫히는 세션을 건드리는 순간 OS 쪽에서 힙이 깨져
/// 몽독째 꺼질 수 있음 (10/18 덤프). 그래서 미디어 세션은 「mongdock.exe --media-host 파이프 부모pid」 자식 프로세스에서만 다루고,
/// 본체(<see cref="MediaHostClient"/>)는 파이프로 상태만 받음 — 자식이 죽어도 본체는 그대로, 잠시 뒤 다시 띄움.
/// 자식: 기존 MediaService 를 그대로 돌리고 바뀔 때마다(+재생 중이면 5초마다) 한 줄 JSON 상태를 보냄, 본체가 보낸 조작(재생/다음/이전)을 실행.
/// 부모가 끝나거나 파이프가 끊기면 같이 끝남. 창 없음(숨은 디스패처 창은 메시지를 계속 받아 로그오프를 막지 않음).
/// </summary>
public sealed class MediaHost
{
    public const string Arg = "--media-host";

    private readonly string _pipeName;
    private readonly int _parentPid;
    private readonly Action _quit;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly object _sendGate = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private MediaService? _media;
    private ImageSource? _lastThumb;
    private bool _thumbSent;
    private DispatcherTimer? _tick;
    private DispatcherTimer? _alive;
    private int _quitting;

    public MediaHost(string pipeName, int parentPid, Action quit)
    {
        _pipeName = pipeName;
        _parentPid = parentPid;
        _quit = quit;
    }

    /// <summary>UI 스레드에서: 파이프 연결 → 미디어 서비스 시작 → 읽기·부모 지켜보기 스레드.</summary>
    public void Start()
    {
        // 로그는 따로 (본체 로그와 동시에 쓰면 한쪽 줄이 버려짐 — 크래시 직전 도우미 로그가 가장 필요한 정보, 리뷰)
        Log.UseFile("mongdock-media.log");
        Log.Tag = "미디어 도우미: ";
        // 죽을 때 윈도우 「작동이 중지되었습니다」 창을 띄우지 않음 (덤프·보고는 그대로) — 본체가 알아서 다시 띄움
        try { WerSetFlags(WER_FAULT_REPORTING_NO_UI); } catch { }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("처리되지 않은 예외", e.ExceptionObject as Exception);
        try
        {
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _pipe.Connect(5000);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }
        catch (Exception ex)
        {
            Log.Warn($"파이프 연결 실패: {ex.GetType().Name} {ex.Message}");
            Quit();
            return;
        }
        _media = new MediaService();
        _media.Changed += (_, _) => Send();
        _media.PlayingChanged += (_, _) => Send();
        _media.Start();
        // 재생 중이면 5초마다 위치를 다시 보냄 (본체는 그 사이를 시간으로 이어 셈)
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _tick.Tick += (_, _) => { if (_media is { } m && (m.IsPlaying || m.PlayingApps.Count > 0)) Send(); };
        // 살아 있음 표시 (UI 스레드에서 — 미디어 세션 호출에서 멈추면 안 감 → 본체가 15초 뒤 끝내고 다시 띄움)
        _alive = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _alive.Tick += (_, _) => WriteLine("alive");
        _alive.Start();
        _tick.Start();
        Send();
        new Thread(ReadCommands) { IsBackground = true, Name = "media host read" }.Start();
        new Thread(WatchParent) { IsBackground = true, Name = "media host parent" }.Start();
    }

    private void ReadCommands()
    {
        try
        {
            using var reader = new StreamReader(_pipe!, new UTF8Encoding(false));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                string cmd = line.Trim();
                _dispatcher.BeginInvoke(() =>
                {
                    var m = _media;
                    if (m is null) return;
                    switch (cmd)
                    {
                        case "playpause": _ = m.PlayPauseAsync(); break;
                        case "next": _ = m.NextAsync(); break;
                        case "prev": _ = m.PreviousAsync(); break;
                        case "quit": Quit(); break;
                    }
                });
            }
        }
        catch { }
        _dispatcher.BeginInvoke(Quit); // 파이프가 끊김 = 본체가 끝났거나 다시 띄우는 중
    }

    private void WatchParent()
    {
        IntPtr h = OpenProcess(SYNCHRONIZE, false, (uint)_parentPid);
        if (h == IntPtr.Zero) { _dispatcher.BeginInvoke(Quit); return; }
        try { WaitForSingleObject(h, INFINITE); }
        finally { CloseHandle(h); }
        _dispatcher.BeginInvoke(Quit);
    }

    /// <summary>지금 상태 한 줄 (UI 스레드). 썸네일은 바뀌었을 때만 PNG 로.</summary>
    private void Send()
    {
        var m = _media;
        var w = _writer;
        if (m is null || w is null || _quitting != 0) return;
        try
        {
            var snap = new MediaSnapshot
            {
                HasSession = m.HasSession,
                SourceApp = m.SourceApp,
                Title = m.Title,
                Artist = m.Artist,
                IsPlaying = m.IsPlaying,
                PositionMs = (long)m.Position.TotalMilliseconds,
                DurationMs = (long)m.Duration.TotalMilliseconds,
                Playing = m.PlayingApps.Select(a =>
                {
                    var t = m.PlayingTime(a);
                    return new MediaSnapshot.AppTime { Source = a, PositionMs = t is { } x ? (long)x.Position.TotalMilliseconds : null, DurationMs = t is { } y ? (long)y.Duration.TotalMilliseconds : null };
                }).ToList(),
            };
            if (!_thumbSent || !ReferenceEquals(m.Thumbnail, _lastThumb))
            {
                _lastThumb = m.Thumbnail;
                _thumbSent = true;
                snap.Thumb = EncodePng(m.Thumbnail) ?? "";
            }
            WriteLine(JsonSerializer.Serialize(snap));
        }
        catch (Exception ex) { Log.Warn($"상태 보내기 실패: {ex.GetType().Name} {ex.Message}"); }
    }

    private void WriteLine(string line)
    {
        var w = _writer;
        if (w is null || _quitting != 0) return;
        try { lock (_sendGate) w.WriteLine(line); }
        catch (IOException) { Quit(); }
        catch (ObjectDisposedException) { Quit(); }
    }

    private static string? EncodePng(ImageSource? image)
    {
        if (image is not BitmapSource bmp) return null;
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    /// <summary>
    /// 끝: 미디어 세션은 해제하지 않고 바로 프로세스를 끝냄 — 해제(GSMTC Release)가 바로 크래시가 났던 지점이라 거기서 멈추면 도우미가 남아
    /// 교체(swap)·설치가 막힘 (리뷰). 프로세스가 끝나면 윈도우가 정리함.
    /// </summary>
    private void Quit()
    {
        if (Interlocked.Exchange(ref _quitting, 1) != 0) return;
        try { _tick?.Stop(); _alive?.Stop(); } catch { }
        Environment.Exit(0);
    }

    private const uint SYNCHRONIZE = 0x00100000, INFINITE = 0xFFFFFFFF, WER_FAULT_REPORTING_NO_UI = 0x20;
    [DllImport("kernel32.dll")] private static extern int WerSetFlags(uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>도우미 → 본체 한 줄 상태 (JSON). Thumb: null = 그대로, "" = 없음, 그 밖 = PNG base64.</summary>
internal sealed class MediaSnapshot
{
    public bool HasSession { get; set; }
    public string? SourceApp { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public bool IsPlaying { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public List<AppTime> Playing { get; set; } = new();
    public string? Thumb { get; set; }

    public sealed class AppTime
    {
        public string Source { get; set; } = "";
        public long? PositionMs { get; set; }
        public long? DurationMs { get; set; }
    }
}
