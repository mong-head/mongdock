using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 영상·음악을 보고 있는지 (사용자 결정 2026-10-21): 미디어 세션이 「재생」인 앱의 창이 지금 앞에 있으면 true.
/// 그동안은 전체 화면이 아니어도(브라우저 창 최대화로 넷플릭스 등) 알림 배너와 독 아이콘 튀기기를 하지 않음 — 알림은 알림 센터에만 쌓이고,
/// 윈도우 기본 토스트는 그대로 숨김(NotificationService.LastBannerTick 은 배너 창과 상관없이 기록됨). 재생을 멈추거나 다른 창으로 가면 다시 보임.
/// </summary>
internal static class MediaFocus
{
    /// <summary>시험용: 앞 창·재생 앱을 바꿔 끼움.</summary>
    internal static Func<(AppWindowInfo? Foreground, IReadOnlyList<string> Playing)>? StateForTest { get; set; }

    public static bool Watching(AppServices services)
    {
        try
        {
            AppWindowInfo? fg;
            IReadOnlyList<string> playing;
            if (StateForTest is { } t) (fg, playing) = t();
            else
            {
                playing = services.Media.PlayingApps;
                if (playing.Count == 0) return false;
                var hwnd = services.Windows.ForegroundWindow;
                fg = hwnd == IntPtr.Zero ? null : services.Windows.Windows.FirstOrDefault(w => w.Hwnd == hwnd);
            }
            return Watching(fg, playing);
        }
        catch { return false; }
    }

    /// <summary>앞 창이 재생 중인 앱 중 하나의 창인지 (독의 재생 표시와 같은 맞춤: AUMID 또는 실행 파일 이름).</summary>
    internal static bool Watching(AppWindowInfo? foreground, IReadOnlyList<string> playing)
    {
        if (foreground is null || playing.Count == 0) return false;
        var one = new[] { foreground };
        return playing.Any(source => DockWindow.MediaSourceMatches(source, null, one));
    }
}
