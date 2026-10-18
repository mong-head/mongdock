using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Mongdock.Services;

/// <summary>
/// 현재 재생 중인 미디어 (GlobalSystemMediaTransportControlsSessionManager).
/// WinRT 이벤트는 임의 스레드에서 오므로 모든 상태 갱신·Changed 는 UI 스레드에서.
/// Position 은 마지막 타임라인 값 + (재생 중이면) 그 이후 경과 시간으로 보정.
/// </summary>
public sealed class MediaService : IMediaService, IDisposable
{
    private Dispatcher? _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    /// <summary>
    /// 떼어 낸 미디어 세션을 붙잡아 둠 (#15): 0.6.3 에서 세션 호출이 NullReferenceException 을 낸 뒤(창·탭이 닫히는 순간) 그 WinRT 객체를
    /// 종료자가 치우다 같은 예외로 앱 전체가 꺼짐 — .NET 은 종료자 예외를 못 잡음. 세션은 하루 몇 개뿐이라 붙잡아 두면 종료자가 돌지 않음.
    /// </summary>
    private static readonly Queue<GlobalSystemMediaTransportControlsSession> Retired = new();
    private const int RetiredMax = 256;
    private bool _started;
    private int _refreshVersion;

    private TimeSpan _position;
    private DateTimeOffset _positionUpdated;
    private double _rate = 1.0;

    public bool HasSession { get; private set; }
    public string? SourceApp { get; private set; }
    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public ImageSource? Thumbnail { get; private set; }
    public bool IsPlaying { get; private set; }
    public TimeSpan Duration { get; private set; }

    public TimeSpan Position
    {
        get
        {
            if (!IsPlaying || _positionUpdated == default) return Clamp(_position);
            var elapsed = DateTimeOffset.Now - _positionUpdated;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            return Clamp(_position + TimeSpan.FromTicks((long)(elapsed.Ticks * _rate)));
        }
    }

    private TimeSpan Clamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return TimeSpan.Zero;
        return Duration > TimeSpan.Zero && t > Duration ? Duration : t;
    }

    public event EventHandler? Changed;

    // ───────────────────────── 재생 중인 모든 세션 (#22·#27) ─────────────────────────

    /// <summary>지켜보는 세션 → 앱 이름·마지막으로 읽은 재생 상태. 재생 상태는 그 세션의 알림이 올 때 그 세션만 읽음 (#37).</summary>
    private readonly Dictionary<GlobalSystemMediaTransportControlsSession, Watched> _watched = new();

    private sealed class Watched
    {
        public required string Source;
        public bool Playing;
        public double Rate = 1.0;
    }

    /// <summary>
    /// 한 번이라도 깨진(E_POINTER → NullReference 등) 세션 — 다시 지켜보거나 현재 세션으로 붙이지 않고, 영원히 붙잡아 종료자로도 안 보냄 (#37).
    /// 10/18 크래시(0.8.2): 깨진 세션을 지켜보기에서 뺐다가 다음 세션 목록에서 다시 넣어 GetPlaybackInfo 를 또 불렀고, 그 결과를 놓다 힙 손상.
    /// 같은 세션의 래퍼는 네이티브 포인터로 같다고 봄(CsWinRT Equals).
    /// </summary>
    private static readonly HashSet<GlobalSystemMediaTransportControlsSession> Broken = new();

    private static bool IsBroken(GlobalSystemMediaTransportControlsSession s) { lock (Broken) return Broken.Contains(s); }

    private static void MarkBroken(GlobalSystemMediaTransportControlsSession s) { lock (Broken) Broken.Add(s); }

    public IReadOnlyList<string> PlayingApps { get; private set; } = Array.Empty<string>();
    public event EventHandler? PlayingChanged;

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) =>
        _ = OnUi(WatchSessions);

    /// <summary>세션 목록을 다시 받아 새 세션은 재생 상태 변화를 구독, 사라진 세션은 떼어 붙잡아 둠(#15). UI 스레드.</summary>
    private void WatchSessions()
    {
        if (!_started || _manager is null) return;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> now;
        try { now = _manager.GetSessions(); }
        catch (Exception ex) { Log.Warn($"미디어 세션 목록 읽기 실패: {ex.GetType().Name}"); return; }
        foreach (var gone in _watched.Keys.Where(s => !now.Contains(s)).ToList()) Unwatch(gone);
        foreach (var s in now)
        {
            if (_watched.ContainsKey(s) || IsBroken(s)) continue;
            string source;
            try { source = s.SourceAppUserModelId ?? ""; }
            catch (Exception ex) { MarkBroken(s); Log.Warn($"재생 앱 읽기 실패: {ex.GetType().Name}"); continue; }
            var w = new Watched { Source = source };
            _watched[s] = w;
            try { s.PlaybackInfoChanged += OnAnyPlaybackChanged; } catch { }
            ReadWatched(s, w);
        }
        UpdatePlayingApps();
    }

    private void Unwatch(GlobalSystemMediaTransportControlsSession s)
    {
        _watched.Remove(s);
        Retire(s);
        try { s.PlaybackInfoChanged -= OnAnyPlaybackChanged; } catch { }
    }

    // 알림이 온 그 세션만 다시 읽음 (0.8.2 는 알림마다 지켜보는 모든 세션에 GetPlaybackInfo — #37 크래시 경로)
    private void OnAnyPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _ = OnUi(() =>
        {
            if (!_watched.TryGetValue(sender, out var w)) return;
            ReadWatched(sender, w);
            UpdatePlayingApps();
        });

    /// <summary>지켜보는 세션 하나의 재생 상태를 읽어 둠. 깨졌으면 깨짐으로 표시하고 지켜보기에서 뺌 (다시 안 넣음).</summary>
    private void ReadWatched(GlobalSystemMediaTransportControlsSession s, Watched w)
    {
        if (IsBroken(s)) { Unwatch(s); return; }
        try
        {
            var info = s.GetPlaybackInfo();
            w.Playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            w.Rate = info?.PlaybackRate is double r && r > 0 ? r : 1.0;
        }
        catch (Exception ex) when (ex is NullReferenceException or ObjectDisposedException or InvalidCastException)
        {
            OnBrokenSession(s, ex, "재생 상태(지켜보기)");
        }
        catch (Exception ex) { Log.Warn($"재생 상태 읽기 실패: {ex.GetType().Name}"); }
    }

    /// <summary>재생 중인 앱 이름을 읽어 둔 상태로 다시 셈 (세션을 부르지 않음) — 바뀌었으면 PlayingChanged.</summary>
    private void UpdatePlayingApps()
    {
        var playing = new List<string>();
        foreach (var w in _watched.Values)
            if (w.Playing && w.Source.Length > 0 && !playing.Contains(w.Source, StringComparer.OrdinalIgnoreCase)) playing.Add(w.Source);
        if (playing.SequenceEqual(PlayingApps, StringComparer.OrdinalIgnoreCase)) return;
        PlayingApps = playing;
        try { PlayingChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("MediaService.PlayingChanged 핸들러 예외", ex); }
    }

    public (TimeSpan Position, TimeSpan Duration)? PlayingTime(string sourceApp)
    {
        foreach (var (s, w) in _watched.ToList())
        {
            // 재생 상태는 읽어 둔 값으로 (1초마다 GetPlaybackInfo 를 부르지 않음 — #37)
            if (!w.Playing || IsBroken(s) || !string.Equals(w.Source, sourceApp, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var t = s.GetTimelineProperties();
                var duration = t.EndTime - t.StartTime;
                if (duration < TimeSpan.FromSeconds(1)) duration = TimeSpan.Zero;
                var pos = t.Position - t.StartTime;
                // 재생 중이면 마지막 갱신 뒤 흐른 시간만큼 (LastUpdatedTime 이 비어 있는 앱은 그대로)
                if (t.LastUpdatedTime.Year > 2000)
                {
                    var elapsed = DateTimeOffset.Now - t.LastUpdatedTime;
                    if (elapsed > TimeSpan.Zero) pos += TimeSpan.FromTicks((long)(elapsed.Ticks * w.Rate));
                }
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (duration > TimeSpan.Zero && pos > duration) pos = duration;
                return (pos, duration);
            }
            catch (Exception ex) when (ex is NullReferenceException or ObjectDisposedException or InvalidCastException)
            {
                OnBrokenSession(s, ex, "재생 시간");
            }
            catch (Exception ex) { Log.Warn($"재생 시간 읽기 실패: {ex.GetType().Name}"); }
        }
        return null;
    }

    // ───────────────────────── 시작/정지 ─────────────────────────

    public void Start()
    {
        if (_started) return;
        _started = true;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            await OnUi(() =>
            {
                if (!_started) return;
                _manager = mgr;
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                _manager.SessionsChanged += OnSessionsChanged;
                AttachSession(_manager.GetCurrentSession());
                WatchSessions();
            });
        }
        catch (Exception ex)
        {
            Log.Error("미디어 세션 관리자 초기화 실패", ex);
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        DetachSession();
        if (_manager is not null)
        {
            try { _manager.CurrentSessionChanged -= OnCurrentSessionChanged; } catch { }
            try { _manager.SessionsChanged -= OnSessionsChanged; } catch { }
            foreach (var s in _watched.Keys.ToList()) Unwatch(s);
            PlayingApps = Array.Empty<string>();
            _manager = null;
        }
    }

    public void Dispose() => Stop();

    // ───────────────────────── 세션 ─────────────────────────

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        _ = OnUi(() =>
        {
            if (_started && _manager is not null) AttachSession(_manager.GetCurrentSession());
        });

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is not null && IsBroken(session)) session = null; // 한 번 깨진 세션은 다시 안 씀 (#37)
        DetachSession();
        _session = session;
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        _ = RefreshAllAsync();
    }

    private void DetachSession()
    {
        var s = _session;
        _session = null;
        if (s is null) return;
        Retire(s);
        try
        {
            s.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            s.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            s.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch { }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ = OnUi(() => { if (sender == _session && !IsBroken(sender)) _ = RefreshMediaPropertiesAsync(); });

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _ = OnUi(() =>
        {
            if (sender != _session || IsBroken(sender)) return;
            bool changed = ReadPlayback(sender);
            if (sender != _session) return; // 재생 상태 읽기에서 깨져 세션이 바뀜 → 깨진 세션에 더 부르지 않음 (#37)
            if (ReadTimeline(sender) | changed) RaiseChanged();
        });

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        _ = OnUi(() => { if (sender == _session && !IsBroken(sender) && ReadTimeline(sender)) RaiseChanged(); });

    private static void Retire(GlobalSystemMediaTransportControlsSession s)
    {
        lock (Retired)
        {
            if (Retired.Contains(s)) return;
            Retired.Enqueue(s);
            while (Retired.Count > RetiredMax) Retired.Dequeue();
        }
    }

    /// <summary>
    /// 세션 호출이 깨진 객체 예외(NullReference·ObjectDisposed·InvalidCast)를 내면: 그 세션을 붙잡아 두고(종료자 방지) 떼어 낸 뒤
    /// 지금 세션을 다시 받음 (#15). UI 스레드에서.
    /// </summary>
    private void OnBrokenSession(GlobalSystemMediaTransportControlsSession s, Exception ex, string what)
    {
        if (ex is not (NullReferenceException or ObjectDisposedException or InvalidCastException)) return;
        Retire(s);
        MarkBroken(s);
        Log.Warn($"미디어 세션이 깨짐({what}: {ex.GetType().Name}) → 다시 안 씀");
        // 어느 경로에서 깨졌든: 재생 표시 지켜보기에서도 빼고(남은 「재생 중」 표시 지움), 현재 세션이면 새로 받음 (#37 리뷰)
        if (_watched.ContainsKey(s)) { Unwatch(s); UpdatePlayingApps(); }
        _ = OnUi(() =>
        {
            if (!_started || _manager is null || !ReferenceEquals(s, _session)) return;
            GlobalSystemMediaTransportControlsSession? fresh = null;
            try { fresh = _manager.GetCurrentSession(); }
            catch (Exception e2) { Log.Warn($"미디어 세션 다시 받기 실패: {e2.GetType().Name}"); }
            AttachSession(ReferenceEquals(fresh, s) ? null : fresh);
        });
    }

    // ───────────────────────── 읽기 ─────────────────────────

    private async Task RefreshAllAsync()
    {
        var s = _session;
        if (s is null)
        {
            HasSession = false;
            SourceApp = null;
            Title = Artist = null;
            Thumbnail = null;
            IsPlaying = false;
            _position = Duration = TimeSpan.Zero;
            _positionUpdated = default;
            RaiseChanged();
            return;
        }
        HasSession = true;
        try { SourceApp = s.SourceAppUserModelId; }
        catch (Exception ex) { SourceApp = null; Log.Warn($"재생 앱 읽기 실패: {ex.GetType().Name}"); }
        ReadPlayback(s);
        ReadTimeline(s);
        RaiseChanged();
        await RefreshMediaPropertiesAsync();
    }

    private async Task RefreshMediaPropertiesAsync()
    {
        var s = _session;
        if (s is null) return;
        int version = Interlocked.Increment(ref _refreshVersion);
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            ImageSource? thumb = null;
            if (props?.Thumbnail is { } reference)
                thumb = await LoadThumbnailAsync(reference);

            await OnUi(() =>
            {
                if (version != _refreshVersion || s != _session) return; // 그 사이 새 요청/세션
                Title = string.IsNullOrWhiteSpace(props?.Title) ? null : props!.Title;
                Artist = string.IsNullOrWhiteSpace(props?.Artist) ? (string.IsNullOrWhiteSpace(props?.AlbumArtist) ? null : props!.AlbumArtist) : props!.Artist;
                Thumbnail = thumb;
                RaiseChanged();
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"미디어 정보 읽기 실패: {ex.Message}");
            OnBrokenSession(s, ex, "미디어 정보");
        }
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var net = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 256;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze(); // 백그라운드에서 만들어도 Freeze 하면 UI 스레드에서 사용 가능
            return bi;
        }
        catch (Exception ex)
        {
            Log.Warn($"미디어 썸네일 로드 실패: {ex.Message}");
            return null;
        }
    }

    private bool ReadPlayback(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var info = s.GetPlaybackInfo();
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double rate = info.PlaybackRate is double r && r > 0 ? r : 1.0;
            if (playing == IsPlaying && Math.Abs(rate - _rate) < 0.001) return false;
            // 재생 상태가 바뀌기 전 위치를 확정해 둠
            _position = Position;
            _positionUpdated = DateTimeOffset.Now;
            IsPlaying = playing;
            _rate = rate;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"재생 상태 읽기 실패: {ex.Message}");
            OnBrokenSession(s, ex, "재생 상태");
            return false;
        }
    }

    private bool ReadTimeline(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var t = s.GetTimelineProperties();
            var duration = t.EndTime - t.StartTime;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            var pos = t.Position - t.StartTime;
            var updated = t.LastUpdatedTime;
            bool changed = duration != Duration || pos != _position;
            Duration = duration;
            _position = pos;
            // LastUpdatedTime 이 비어 있는 앱도 있어 그때는 지금 시각
            _positionUpdated = updated.Year > 2000 ? updated : DateTimeOffset.Now;
            return changed;
        }
        catch (Exception ex)
        {
            Log.Warn($"타임라인 읽기 실패: {ex.Message}");
            OnBrokenSession(s, ex, "타임라인");
            return false;
        }
    }

    // ───────────────────────── 조작 ─────────────────────────

    public async Task PlayPauseAsync()
    {
        try { if (_session is { } s) await s.TryTogglePlayPauseAsync(); }
        catch (Exception ex) { Log.Error("재생/일시정지 실패", ex); }
    }

    public async Task NextAsync()
    {
        try { if (_session is { } s) await s.TrySkipNextAsync(); }
        catch (Exception ex) { Log.Error("다음 곡 실패", ex); }
    }

    public async Task PreviousAsync()
    {
        try { if (_session is { } s) await s.TrySkipPreviousAsync(); }
        catch (Exception ex) { Log.Error("이전 곡 실패", ex); }
    }

    // ───────────────────────── 도우미 ─────────────────────────

    private Task OnUi(Action a)
    {
        var d = _dispatcher;
        if (d is null) return Task.CompletedTask;
        if (d.CheckAccess())
        {
            try { a(); } catch (Exception ex) { Log.Error("미디어 상태 처리 예외", ex); }
            return Task.CompletedTask;
        }
        return d.InvokeAsync(() =>
        {
            try { a(); } catch (Exception ex) { Log.Error("미디어 상태 처리 예외", ex); }
        }).Task;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("MediaService.Changed 핸들러 예외", ex); }
    }
}
