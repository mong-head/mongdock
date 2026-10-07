using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MyDock.Services;

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
    private bool _started;
    private int _refreshVersion;

    private TimeSpan _position;
    private DateTimeOffset _positionUpdated;
    private double _rate = 1.0;

    public bool HasSession { get; private set; }
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
                AttachSession(_manager.GetCurrentSession());
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
        try
        {
            s.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            s.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            s.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch { }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ = OnUi(() => { if (sender == _session) _ = RefreshMediaPropertiesAsync(); });

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _ = OnUi(() => { if (sender == _session && ReadPlayback(sender) | ReadTimeline(sender)) RaiseChanged(); });

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        _ = OnUi(() => { if (sender == _session && ReadTimeline(sender)) RaiseChanged(); });

    // ───────────────────────── 읽기 ─────────────────────────

    private async Task RefreshAllAsync()
    {
        var s = _session;
        if (s is null)
        {
            HasSession = false;
            Title = Artist = null;
            Thumbnail = null;
            IsPlaying = false;
            _position = Duration = TimeSpan.Zero;
            _positionUpdated = default;
            RaiseChanged();
            return;
        }
        HasSession = true;
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
