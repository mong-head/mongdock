using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 몽독 스크롤바 (설정·신고·루틴 편집·판·목록 공통): 내용 위에 겹쳐 뜨는 얇은 연보라 반투명 막대, 넘칠 때만.
/// 스크롤하면 나타났다 1초 뒤 흐려지고, 마우스를 올리면 조금 굵어짐 (Themes/Controls.xaml OverlayScrollViewer·ThinScrollBar).
/// 윈도우 기본(굵은 회색 막대 + 화살표)이 몽독 창과 안 어울림 — 사용자.
/// </summary>
internal static class MongScroll
{
    public static T Apply<T>(T scroll, UiPalette? p = null) where T : ScrollViewer
    {
        if (Application.Current?.TryFindResource("OverlayScrollViewer") is Style overlay) scroll.Style = overlay;
        if (Application.Current?.TryFindResource("ThinScrollBar") is Style thin && !scroll.Resources.Contains(typeof(ScrollBar)))
            scroll.Resources[typeof(ScrollBar)] = thin;
        var bar = (p ?? ViewModels.UiTheme.Current).SoftAccentLine;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ScrollBar? vbar = null;
        void Find()
        {
            if (vbar is not null) return;
            scroll.ApplyTemplate();
            vbar = scroll.Template?.FindName("PART_VerticalScrollBar", scroll) as ScrollBar;
            if (vbar is null) return;
            vbar.Foreground = bar; // 막대 색 (ThinScrollBar 가 Foreground 로 칠함)
            vbar.Opacity = 0;
            vbar.MouseEnter += (_, _) => Show();
            vbar.MouseLeave += (_, _) => timer.Start();
        }
        void Show()
        {
            Find();
            if (vbar is null) return;
            vbar.BeginAnimation(UIElement.OpacityProperty, null);
            vbar.Opacity = 1;
            timer.Stop();
            timer.Start();
        }
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (vbar is null || vbar.IsMouseOver || vbar.IsMouseCaptureWithin) { timer.Start(); return; }
            vbar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        };
        scroll.Loaded += (_, _) => Find();
        scroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) Show(); };
        scroll.Unloaded += (_, _) => timer.Stop();
        return scroll;
    }
}
