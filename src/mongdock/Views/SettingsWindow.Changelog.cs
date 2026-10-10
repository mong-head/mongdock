using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.Services;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Mongdock.Views;

/// <summary>
/// 설정 창 "변경 내역" 페이지: 맨 위 "주요 업데이트"(버전별 headline + major 항목, 누르면 아래 그 버전으로 스크롤, "주요 기능 둘러보기 ▶"),
/// 그 아래 Changelog.json(앱에 포함)을 최신 버전부터 버전·날짜 머리글 + "새 기능 / 개선 / 고친 문제 / 알려진 한계" 목록으로.
/// 지금 실행 중인 버전에는 "지금 버전" 표시, coach 단계가 있는 버전은 머리글에 "둘러보기 ▶".
/// 정보 페이지에는 "이 버전 둘러보기 ▶ · 변경 내역 보기 ›" 한 줄.
/// 둘러보기를 누르면 설정 창을 숨기고 상단바·독 위 말풍선을 재생, 끝나면 설정 창을 다시 보여 줌.
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
                Text = Loc.T("변경 내역을 읽지 못했어요."),
                Foreground = _p.SubText,
                Margin = new Thickness(4, 0, 0, 12),
            });
        }
        _releaseCards.Clear();
        if (releases.Any(r => r.Majors.Any()))
            body.Children.Add(MajorUpdatesCard(releases));
        foreach (var release in releases)
        {
            var card = ReleaseCard(release, release.Version == WhatsNew.Current);
            _releaseCards[release.VersionText] = card;
            body.Children.Add(card);
        }

        // GitHub 릴리스 페이지는 깃허브판에만 (스토어판은 저장소를 안 보여 줌 — 출시 때 저장소가 비공개로 바뀜)
        if (!AppInfo.IsPackaged)
            body.Children.Add(LinkButton(Loc.T("GitHub 릴리스 전체 보기 ↗"), () => _services.Launcher.OpenFile(UpdateService.ReleasesPageUrl), HorizontalAlignment.Left));
    }

    /// <summary>변경 내역 페이지의 버전 카드 (주요 업데이트에서 누르면 여기로 스크롤). 다시 그릴 때마다 새로.</summary>
    private readonly Dictionary<string, FrameworkElement> _releaseCards = new();

    /// <summary>
    /// 맨 위 "주요 업데이트": 버전마다 "v0.3.0 · headline" + major 항목 짧게 (최신 버전은 강조색).
    /// 버전 묶음을 누르면 아래 그 버전 카드로 스크롤. 오른쪽 위 "주요 기능 둘러보기 ▶".
    /// </summary>
    private Border MajorUpdatesCard(IReadOnlyList<ChangeRelease> releases)
    {
        var stack = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        var title = new TextBlock
        {
            Text = Loc.T("주요 업데이트"),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        if (releases.Any(r => r.Majors.Any(e => e.Coach is not null)))
        {
            var tour = TourLink(Loc.T("주요 기능 둘러보기 ▶"), CoachMarks.BuildMajorTour);
            DockPanel.SetDock(tour, Dock.Right);
            head.Children.Add(tour);
        }
        stack.Children.Add(head);

        var latest = releases[0];
        foreach (var release in releases)
        {
            var majors = release.Majors.Select(e => e.Text).ToList();
            if (majors.Count == 0 && string.IsNullOrEmpty(release.Headline)) continue;
            bool isLatest = release == latest;
            var block = new StackPanel();
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new System.Windows.Documents.Run($"v{release.VersionText}")
            {
                FontWeight = FontWeights.Bold,
                Foreground = isLatest ? _p.Accent : _p.Text,
            });
            if (!string.IsNullOrEmpty(release.Headline))
            {
                line.Inlines.Add(new System.Windows.Documents.Run("  " + CoachMarkWindow.KeepAll(release.Headline))
                {
                    FontWeight = isLatest ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = _p.Text,
                });
            }
            block.Children.Add(line);
            foreach (string item in majors)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = isLatest ? _p.Accent : _p.SubText,
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2, 7, 0, 0),
                });
                var text = new TextBlock
                {
                    Text = CoachMarkWindow.KeepAll(ShortText(item)),
                    FontSize = 12.5,
                    Foreground = _p.SubText,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                block.Children.Add(row);
            }
            // 묶음 전체가 버튼: 누르면 아래 그 버전 카드로
            var button = new Button
            {
                Style = (Style)FindResource("CardLinkButton"),
                Foreground = _p.Text,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(-8, 4, -8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = block,
                ToolTip = Loc.F($"v{release.VersionText} 변경 내역으로"),
            };
            string key = release.VersionText;
            button.Click += (_, _) => ScrollToWhenReady(() => _releaseCards.GetValueOrDefault(key));
            stack.Children.Add(button);
        }

        return new Border
        {
            Background = _p.GroupBackground,
            BorderBrush = _p.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 16),
            Child = stack,
        };
    }

    /// <summary>주요 업데이트 목록용 짧은 문구: 첫 문장만 (". " 앞까지).</summary>
    private static string ShortText(string text)
    {
        int cut = text.IndexOf(". ", StringComparison.Ordinal);
        return cut > 0 ? text[..(cut + 1)] : text;
    }

    /// <summary>"둘러보기 ▶" 같은 작은 링크 버튼 (카드 머리글 오른쪽).</summary>
    private Button TourLink(string text, Func<List<CoachPage>> build)
    {
        var link = new Button
        {
            Style = (Style)FindResource("CardLinkButton"),
            Foreground = _p.Accent,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = text, FontSize = 12.5, Foreground = _p.Accent },
        };
        link.Click += (_, _) => PlayCoach(build);
        return link;
    }

    /// <summary>
    /// 둘러보기 재생: 설정 창을 숨겨(상단바·독을 가리지 않게) 말풍선을 보여 주고, 끝나면(완료·건너뛰기·다른 안내) 설정 창을 다시.
    /// 전체 화면 앱·일시 정지로 끝났으면 포커스를 뺏지 않게 Show 만 (Activate 안 함), 전체 화면 중이면 전체 화면이 끝난 뒤 Show.
    /// 보여 줄 카드가 없으면 창은 그대로. isTour = 첫 설치 둘러보기 (다 보면 FirstRunTourPending 해제).
    /// </summary>
    private void PlayCoach(Func<List<CoachPage>> build, bool isTour = false)
    {
        List<CoachPage> pages;
        try { pages = build(); }
        catch (Exception ex)
        {
            Log.Error("둘러보기 만들기 실패", ex);
            return;
        }
        if (pages.Count == 0) return;
        FlushSlider();
        Hide();
        bool started = CoachMarks.Play(() => pages, reason => Dispatcher.BeginInvoke(() => ReturnFromCoach(reason)), isTour);
        if (!started) Show();
    }

    /// <summary>전체 화면이 끝나길 기다리는 중 (둘러보기가 전체 화면 앱 때문에 닫힘).</summary>
    private EventHandler<bool>? _coachFullscreenWait;

    private void ReturnFromCoach(CoachEndReason reason)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        if (reason == CoachEndReason.Completed)
        {
            Show();
            Activate();
            return;
        }
        if (reason == CoachEndReason.Fullscreen && FullscreenNow())
        {
            if (_coachFullscreenWait is not null) return;
            _coachFullscreenWait = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_closed || FullscreenNow()) return;
                StopFullscreenWait();
                ShowQuietly();
            });
            _services.DesktopWindows.FullscreenAppChanged += _coachFullscreenWait;
            return;
        }
        ShowQuietly();
    }

    private bool FullscreenNow()
    {
        try { return _services.DesktopWindows.IsFullscreenOn(""); }
        catch { return false; }
    }

    private void StopFullscreenWait()
    {
        if (_coachFullscreenWait is null) return;
        _services.DesktopWindows.FullscreenAppChanged -= _coachFullscreenWait;
        _coachFullscreenWait = null;
    }

    /// <summary>포커스를 가져가지 않고 다시 표시 (전체 화면 게임·영상 위로 튀어나와 활성화되지 않게).</summary>
    private void ShowQuietly()
    {
        if (_closed || IsVisible) return;
        bool was = ShowActivated;
        ShowActivated = false;
        try { Show(); }
        finally { ShowActivated = was; }
    }

    /// <summary>정보 페이지 머리글 아래: "이 버전 둘러보기 ▶"(현재 버전에 coach 단계가 있을 때) · "변경 내역 보기 ›".</summary>
    private UIElement AboutCoachLinks()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var current = Changelog.Releases.FirstOrDefault(r => r.Version <= WhatsNew.Current);
        if (current is not null && CoachMarks.HasTour(current))
        {
            var tour = LinkButton(Loc.F($"v{current.VersionText} 둘러보기 ▶"), () => PlayCoach(() => CoachMarks.BuildReleaseTour(current)), HorizontalAlignment.Center);
            row.Children.Add(tour);
        }
        row.Children.Add(ChangelogLinkButton());
        return row;
    }

    /// <summary>버전 하나: 머리글(버전 · 지금 버전 · 둘러보기 ▶ · 날짜) + 종류별 목록.</summary>
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
                Child = new TextBlock { Text = Loc.T("지금 버전"), FontSize = 11, Foreground = _p.AccentText },
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
        if (CoachMarks.HasTour(release))
        {
            var tour = TourLink(Loc.T("둘러보기 ▶"), () => CoachMarks.BuildReleaseTour(release));
            DockPanel.SetDock(tour, Dock.Right);
            head.Children.Add(tour);
        }
        stack.Children.Add(head);

        AddChangeSection(stack, Loc.T("새 기능"), release.Entries.Where(e => e.Kind == ChangeKind.Feature).Select(e => e.Text));
        AddChangeSection(stack, Loc.T("개선"), release.Entries.Where(e => e.Kind == ChangeKind.Improvement).Select(e => e.Text));
        AddChangeSection(stack, Loc.T("고친 문제"), release.Entries.Where(e => e.Kind == ChangeKind.Fix).Select(e => e.Text));
        AddChangeSection(stack, Loc.T("알려진 한계"), release.KnownIssues);

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
            ? Loc.DateFull(d)
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
    private Button ChangelogLinkButton() => LinkButton(Loc.T("변경 내역 보기 ›"), () => GoToPage(Page.Changelog), HorizontalAlignment.Center);

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
