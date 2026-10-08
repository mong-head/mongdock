using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyDock.Services;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace MyDock.Views;

/// <summary>
/// 설정 창 "변경 내역" 페이지: Changelog.json(앱에 포함)을 최신 버전부터 버전·날짜 머리글 + "새 기능 / 개선 / 고친 문제 / 알려진 한계" 목록으로.
/// 지금 실행 중인 버전에는 "지금 버전" 표시. 정보 페이지에는 이 페이지로 가는 링크 한 줄.
/// </summary>
internal sealed partial class SettingsWindow
{
    private void BuildChangelog(Panel body)
    {
        var releases = Changelog.Releases;
        if (releases.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "변경 내역을 읽지 못했어요.",
                Foreground = _p.SubText,
                Margin = new Thickness(4, 0, 0, 12),
            });
        }
        foreach (var release in releases)
            body.Children.Add(ReleaseCard(release, release.Version == WhatsNew.Current));

        body.Children.Add(LinkButton("GitHub 릴리스 전체 보기 ↗", () => _services.Launcher.OpenFile(WhatsNew.ReleasesUrl), HorizontalAlignment.Left));
    }

    /// <summary>버전 하나: 머리글(버전 · 지금 버전 · 날짜) + 종류별 목록.</summary>
    private Border ReleaseCard(ChangeRelease release, bool current)
    {
        var stack = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };

        var head = new DockPanel { LastChildFill = false };
        var title = new TextBlock
        {
            Text = $"v{release.VersionText}",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        if (current)
        {
            var badge = new Border
            {
                Background = _p.Accent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(8, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "지금 버전", FontSize = 11, Foreground = _p.AccentText },
            };
            DockPanel.SetDock(badge, Dock.Left);
            head.Children.Add(badge);
        }
        if (!string.IsNullOrWhiteSpace(release.Date))
        {
            var date = new TextBlock
            {
                Text = DateText(release.Date),
                FontSize = 12,
                Foreground = _p.SubText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(date, Dock.Right);
            head.Children.Add(date);
        }
        stack.Children.Add(head);

        AddChangeSection(stack, "새 기능", release.Entries.Where(e => e.Kind == ChangeKind.Feature).Select(e => e.Text));
        AddChangeSection(stack, "개선", release.Entries.Where(e => e.Kind == ChangeKind.Improvement).Select(e => e.Text));
        AddChangeSection(stack, "고친 문제", release.Entries.Where(e => e.Kind == ChangeKind.Fix).Select(e => e.Text));
        AddChangeSection(stack, "알려진 한계", release.KnownIssues);

        return new Border
        {
            Background = _p.GroupBackground,
            BorderBrush = current ? _p.Accent : _p.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 16),
            Child = stack,
        };
    }

    /// <summary>"2026-10-08" → "2026년 10월 8일". 못 읽으면 그대로.</summary>
    private static string DateText(string date) =>
        DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? $"{d.Year}년 {d.Month}월 {d.Day}일"
            : date;

    /// <summary>소제목 + 점 목록 (단어 단위 줄바꿈). 항목이 없으면 아무것도 안 넣음.</summary>
    private void AddChangeSection(Panel stack, string heading, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        stack.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = _p.SubText,
            Margin = new Thickness(0, 12, 0, 4),
        });
        foreach (string item in list)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            // 점은 첫 줄 가운데 높이에 (여러 줄로 감겨도)
            row.Children.Add(new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = _p.Accent,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 8, 0, 0),
            });
            var text = new TextBlock
            {
                Text = CoachMarkWindow.KeepAll(item),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            stack.Children.Add(row);
        }
    }

    /// <summary>정보 페이지 머리글 아래 "변경 내역 보기" 링크 한 줄.</summary>
    private Button ChangelogLinkButton() => LinkButton("변경 내역 보기 ›", () => GoToPage(Page.Changelog), HorizontalAlignment.Center);

    private Button LinkButton(string text, Action action, HorizontalAlignment align)
    {
        var link = new Button
        {
            Style = (Style)FindResource("CardLinkButton"),
            Foreground = _p.Accent,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = align,
            Content = new TextBlock { Text = text, FontSize = 12.5, Foreground = _p.Accent },
        };
        link.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"'{text}' 실행 실패", ex); }
        };
        return link;
    }

    /// <summary>다른 페이지로 (사이드바 클릭과 같음). 클릭 처리 도중 컨트롤을 갈아 끼우지 않게 한 박자 늦게.</summary>
    private void GoToPage(Page page)
    {
        if (_page == page) return;
        FlushSlider();
        _page = page;
        _scroll = null;
        QueueRebuild();
    }
}
