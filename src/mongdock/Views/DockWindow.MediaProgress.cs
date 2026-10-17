using System.IO;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 재생 진행 막대 (#22, 0.8): 음악·영상을 재생 중인 앱의 독 아이콘 아래 얇은 막대로 재생 위치를 보임.
/// 재생 앱 = 미디어 세션의 SourceAppUserModelId ("Spotify.exe"·"Chrome"·스토어 앱 AUMID) — 독 항목의 핀·창과 짝지음.
/// 재생 중일 때만 1초 타이머 (멈추거나 세션이 없으면 타이머도 멈춤). 다운로드 같은 작업 표시줄 진행은 다른 앱 것을 읽을 공개 방법이 없어 아직 안 함.
/// </summary>
public partial class DockWindow
{
    private DispatcherTimer? _mediaTimer;
    private bool _mediaHooked;

    private void StartMediaProgress()
    {
        if (_mediaHooked) return;
        _mediaHooked = true;
        _services.Media.Changed += OnMediaChanged;
        ApplyMediaProgress();
    }

    private void StopMediaProgress()
    {
        if (!_mediaHooked) return;
        _mediaHooked = false;
        _services.Media.Changed -= OnMediaChanged;
        _mediaTimer?.Stop();
    }

    private void OnMediaChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ApplyMediaProgress); return; }
        ApplyMediaProgress();
    }

    /// <summary>재생 위치를 짝이 맞는 독 항목에만 (나머지는 막대 없음). 재생 중이면 1초마다 다시.</summary>
    private void ApplyMediaProgress()
    {
        if (_closed) return;
        var m = _services.Media;
        bool on = _services.Settings.Current.Dock.ShowMediaProgress;
        double? value = null;
        string? source = null;
        if (on && m.HasSession && m.Duration.TotalSeconds >= 1)
        {
            value = Math.Clamp(m.Position.TotalSeconds / m.Duration.TotalSeconds, 0, 1);
            source = m.SourceApp;
        }
        bool matched = false;
        foreach (var item in _items)
        {
            if (item.IsSeparator) continue;
            bool mine = !matched && value is not null && source is { Length: > 0 } && MediaSourceMatches(source, item.Pin, item.Windows);
            if (mine) matched = true;
            item.Progress = mine ? value : null;
        }
        bool tick = matched && m.IsPlaying;
        if (tick)
        {
            _mediaTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ApplyMediaProgress(), Dispatcher);
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
