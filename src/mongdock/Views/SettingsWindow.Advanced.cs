using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.Models;

namespace Mongdock.Views;

/// <summary>
/// 설정 정리 (#21): 페이지 맨 아래 접힌 "세부 설정" — 중요한 설정이 먼저 보이고, 자잘한 건 펼쳐서.
/// 펼침 상태는 창이 열려 있는 동안 페이지마다 기억 (다시 그려도 그대로).
/// </summary>
internal sealed partial class SettingsWindow
{
    private readonly HashSet<Page> _advancedOpen = new();

    private void AddAdvanced(Panel body, List<UIElement> rows)
    {
        if (rows.Count == 0) return;
        bool open = _advancedOpen.Contains(_page);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = open ? "" : "", // ChevronDown / ChevronRight
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 10,
            Foreground = _p.SubText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 8, 0),
        });
        header.Children.Add(new TextBlock { Text = Loc.T("세부 설정"), FontWeight = FontWeights.SemiBold, Foreground = _p.SubText });
        if (!open)
            header.Children.Add(new TextBlock { Text = Loc.F($"  {rows.Count}개"), FontSize = 12, Foreground = _p.Disabled, VerticalAlignment = VerticalAlignment.Center });
        var toggle = new Button
        {
            Style = (Style)FindResource("CardLinkButton"),
            Foreground = _p.Text,
            Padding = new Thickness(4, 5, 8, 5),
            Margin = new Thickness(0, 4, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = header,
        };
        var page = _page;
        toggle.Click += (_, _) =>
        {
            if (!_advancedOpen.Remove(page)) _advancedOpen.Add(page);
            QueueRebuild();
            if (!_advancedOpen.Contains(page)) return;
            // 펼친 내용이 보이게 (다시 그린 뒤)
            ScrollToWhenReady(() => _scroll?.Content is Panel p ? p.Children.OfType<FrameworkElement>().LastOrDefault() : null);
        };
        body.Children.Add(toggle);
        if (open) body.Children.Add(Group(rows.ToArray()));
    }
}

/// <summary>상단바 크기 세 단계 (#21: 높이·글자 크기 슬라이더 대신). 지금 기본값 26/13 = 보통.</summary>
internal enum TopBarSize { Small, Normal, Large }

internal static class TopBarSizes
{
    private static readonly (TopBarSize Size, double Height, double Font)[] Steps =
    {
        (TopBarSize.Small, 22, 12),
        (TopBarSize.Normal, 26, 13),
        (TopBarSize.Large, 30, 14),
    };

    /// <summary>지금 값에 가장 가까운 단계 (예전에 슬라이더로 다른 값을 골랐어도 가까운 쪽이 선택돼 보임).</summary>
    public static TopBarSize Of(TopBarSettings t) =>
        Steps.OrderBy(x => Math.Abs(x.Height - t.Height) + Math.Abs(x.Font - t.FontSize) * 2).First().Size;

    public static void Apply(TopBarSettings t, TopBarSize size)
    {
        var step = Steps.First(x => x.Size == size);
        t.Height = step.Height;
        t.FontSize = step.Font;
    }
}
