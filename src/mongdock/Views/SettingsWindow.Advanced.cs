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
        var page = _page;
        // 0.8.2: 다른 펼치기 줄과 같은 모양 — 카드 안 한 줄(「세부 설정  n개 ›」), 누르면 같은 카드 안에서 펼침
        var toggle = ExpandRow(Loc.T("세부 설정"), open, open ? null : Loc.F($"{rows.Count}개"), () =>
        {
            if (!_advancedOpen.Remove(page)) _advancedOpen.Add(page);
            QueueRebuild();
            if (!_advancedOpen.Contains(page)) return;
            // 펼친 내용이 보이게 (다시 그린 뒤)
            ScrollToWhenReady(() => _scroll?.Content is Panel p ? p.Children.OfType<FrameworkElement>().LastOrDefault() : null);
        });
        var all = new List<UIElement> { toggle };
        if (open) all.AddRange(rows);
        body.Children.Add(Group(all.ToArray()));
    }

    /// <summary>
    /// 펼치기 줄 (설정 전체 공통, 0.8.2): 다른 줄과 같은 높이·안쪽 여백, 보통 글자색 제목, 오른쪽 끝에 [옅은 보조 글] + › (접힘) / ⌄ (펼침).
    /// 줄 전체를 누르면 펼치고 접음. 펼친 내용은 같은 카드 안 아래 줄들로 (Group 이 구분선을 넣음).
    /// </summary>
    private Button ExpandRow(string title, bool open, string? hint, Action toggle)
    {
        var grid = new Grid { MinHeight = 44, Margin = new Thickness(14, 7, 14, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, Foreground = _p.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        if (!string.IsNullOrEmpty(hint))
            right.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = _p.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        right.Children.Add(new TextBlock
        {
            Text = open ? "\uE70D" : "\uE76C", // ChevronDown / ChevronRight
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 11,
            Foreground = _p.SubText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var b = new Button
        {
            Style = (Style)FindResource("CardLinkButton"),
            Foreground = _p.Text,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = grid,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, title);
        b.Click += (_, _) => toggle();
        return b;
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
