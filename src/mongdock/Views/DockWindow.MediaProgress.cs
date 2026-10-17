using System.Windows;
using System.IO;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 재생 중 표시 (#22 → #27, 0.8.2): 음악·영상을 재생 중(Playing)인 앱마다 독의 실행 중 점이 작은 소리 표시로 바뀜 (일시 정지면 그냥 점).
/// 재생 앱 = 미디어 세션의 SourceAppUserModelId ("Spotify.exe"·"Chrome"·스토어 앱 AUMID) — 독 항목의 핀·창과 짝지음. 세션마다 따로라 여러 앱이 동시에 재생 중이면 앱마다.
/// 마우스를 올리면 이름표에 「재생 중 · 2:31 / 4:05」(길이를 모르면 「재생 중」) — 이름표가 떠 있는 동안만 1초마다 다시.
/// 진행 막대는 다운로드 막대처럼 보여 뺐음. 다운로드 같은 작업 표시줄 진행은 다른 앱 것을 읽을 공개 방법이 없어 안 함.
/// </summary>
public partial class DockWindow
{
    private DispatcherTimer? _mediaTimer;
    private bool _mediaHooked;

    private void StartMediaProgress()
    {
        if (_mediaHooked) return;
        _mediaHooked = true;
        _services.Media.PlayingChanged += OnMediaChanged;
        ApplyMediaProgress();
    }

    private void StopMediaProgress()
    {
        if (!_mediaHooked) return;
        _mediaHooked = false;
        _services.Media.PlayingChanged -= OnMediaChanged;
        _mediaTimer?.Stop();
    }

    private void OnMediaChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ApplyMediaProgress); return; }
        ApplyMediaProgress();
    }

    /// <summary>재생 중인 앱마다 짝이 맞는 독 항목 하나에 소리 표시 (같은 앱이 독에 둘이면 앞의 것). 이름표가 그 항목 위면 시간도 다시.</summary>
    private void ApplyMediaProgress()
    {
        if (_closed) return;
        bool on = _services.Settings.Current.Dock.ShowMediaProgress;
        var playing = on ? _services.Media.PlayingApps : Array.Empty<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items)
        {
            if (item.IsSeparator) continue;
            string? mine = null;
            foreach (var source in playing)
                if (!taken.Contains(source) && MediaSourceMatches(source, item.Pin, item.Windows)) { mine = source; break; }
            if (mine is not null) taken.Add(mine);
            item.PlayingSource = mine;
        }
        RefreshMediaLabel();
    }

    /// <summary>이름표 글자: 이름 + 재생 중이면 「 · 재생 중 · 2:31 / 4:05」.</summary>
    private string LabelText(DockItemView view)
    {
        string name = view.Item.Name;
        if (view.Item.PlayingSource is not { } src) return name;
        return name + " · " + MediaTimeText(_services.Media.PlayingTime(src));
    }

    /// <summary>「재생 중 · 2:31 / 4:05」, 길이를 모르면 「재생 중」.</summary>
    internal static string MediaTimeText((TimeSpan Position, TimeSpan Duration)? time)
    {
        string playing = Loc.T("재생 중");
        if (time is not { } t || t.Duration <= TimeSpan.Zero) return playing;
        static string F(TimeSpan x) => x.TotalHours >= 1 ? $"{(int)x.TotalHours}:{x.Minutes:00}:{x.Seconds:00}" : $"{(int)x.TotalMinutes}:{x.Seconds:00}";
        return $"{playing} · {F(t.Position)} / {F(t.Duration)}";
    }

    /// <summary>이름표가 재생 중 항목 위에 떠 있으면 글자를 다시 쓰고 1초 뒤 또 (아니면 타이머 멈춤).</summary>
    private void RefreshMediaLabel()
    {
        bool showing = _label is { IsVisible: true } && _labelView is { } v && v.IsMouseOver && v.Item.PlayingSource is not null;
        if (showing && LabelAnchor(_labelView!) is Point a) _label!.ShowAt(LabelText(_labelView!), a, _layout.Edge);
        if (showing)
        {
            _mediaTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshMediaLabel(), Dispatcher);
            if (!_mediaTimer.IsEnabled) _mediaTimer.Start();
        }
        else _mediaTimer?.Stop();
    }

    /// <summary>
    /// 재생 앱 이름이 이 독 항목인지: AUMID("…!App")면 핀·창의 AUMID 와, 아니면 실행 파일 이름(확장자 빼고, "Chrome" = chrome.exe)과 비교.
    /// </summary>
    internal static bool MediaSourceMatches(string source, PinItem? pin, IEnumerable<AppWindowInfo> windows)
    {
        source = source.Trim();
        if (source.Length == 0) return false;
        if (source.Contains('!'))
        {
            if (pin is { Kind: PinKind.Aumid } p && string.Equals(p.Target, source, StringComparison.OrdinalIgnoreCase)) return true;
            return windows.Any(w => string.Equals(w.Aumid, source, StringComparison.OrdinalIgnoreCase));
        }
        string name = Path.GetFileNameWithoutExtension(source);
        if (name.Length == 0) return false;
        bool Same(string? path) => path is { Length: > 0 } && string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase);
        if (pin is { Kind: PinKind.Exe } e && Same(e.Target)) return true;
        return windows.Any(w => Same(w.ProcessPath));
    }
}

public partial class DockWindow
{
    /// <summary>
    /// 창 최소화 효과(#20)의 도착 자리: 이 창이 속한 독 아이콘의 화면 사각형(물리 픽셀). 독에 없으면(또는 숨은 독) null.
    /// </summary>
    internal Rect? IconScreenRectFor(IntPtr hwnd)
    {
        if (_closed || !IsVisible) return null;
        var item = _items.FirstOrDefault(i => !i.IsSeparator && i.Windows.Any(w => w.Hwnd == hwnd));
        if (item is null || !_views.TryGetValue(item.Id, out var view) || view.ActualWidth < 1 || PresentationSource.FromVisual(view) is null) return null;
        var a = view.PointToScreen(new Point(0, 0));
        var b = view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight));
        return new Rect(a, b);
    }

    /// <summary>독이 놓인 가장자리 (최소화 효과 방향).</summary>
    internal DockEdge EdgeForEffects => _layout.Edge;
}
