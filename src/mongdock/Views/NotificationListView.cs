using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 맥 알림 센터 같은 알림 목록 (재사용 UI 요소 — 달력 패널 등 다른 카드 안에 붙여 쓴다).
/// - 앱별 묶음: 2개 이상이면 카드가 겹친 스택 모양, 누르면 펼침 (헤더: 앱 이름 · 접기 · 모두 숨기기).
///   펼치기/접기는 겹친 카드가 아래로 풀리듯(높이 + 카드 위치 250ms, QuinticEase out).
/// - 항목 클릭 = 그 앱 열기(INotificationService.Open — 그 알림은 몽독 목록에서 숨김) → <see cref="ItemOpened"/>.
/// - 항목에 마우스를 올리면 × (그 알림만 숨기기). 숨기기는 몽독 화면에서만 — 윈도우 알림 DB 는 그대로.
///   ×·모두 숨기기·모두 지우기는 오른쪽으로 밀려나며 흐려진 뒤(180ms) 높이가 접히고(200ms) 나서 서비스에서 숨긴다.
/// - 맨 위 "모두 지우기": 묶음이 위에서부터 35ms 간격으로 차례로 사라진 뒤 전부 숨김.
/// - 화면에 붙어 있는 동안(Loaded)만 서비스 Changed 를 구독하고 30초마다 "n분 전" 갱신. Unloaded 에서 해제.
///   애니메이션 도중의 다시 그리기는 끝날 때까지 미룬다 (도는 애니메이션이 끊기지 않게).
///
/// 사용: <c>var list = new NotificationListView(services, palette) { Width = 320 }; list.ItemOpened += () => Close(); panel.Children.Add(list);</c>
/// </summary>
internal sealed class NotificationListView : Border
{
    private const int MaxPerGroup = 30;
    private const int MaxGroups = 30;
    /// <summary>"모두 지우기" 때 묶음 사이 시간차 (ms) 와 시간차를 주는 최대 묶음 수.</summary>
    private const double StaggerMs = 35;
    private const int StaggerMax = 8;
    private const double ToggleMs = 250;
    /// <summary>겹친 스택에서 뒤 카드가 아래로 비치는 높이.</summary>
    private const double PeekStep = 6;

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly bool _showHeader;
    private readonly StackPanel _content = new();
    private readonly ScrollViewer _scroll;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _clock;
    /// <summary>진행 중인 애니메이션 수 — 0 이 아니면 Rebuild 를 미룸.</summary>
    private int _animating;
    private bool _pendingRebuild;
    /// <summary>직전 그리기에 알림이 있었는지 (비게 되면 "새 알림 없음" 을 페이드 인).</summary>
    private bool _hadItems;
    private Button? _clearAll;

    /// <summary>항목을 눌러 앱을 열었음 — 패널을 닫을 때 사용.</summary>
    public event Action? ItemOpened;

    /// <param name="maxHeight">이보다 길면 안에서 스크롤 (휠).</param>
    /// <param name="showHeader">맨 위 "알림" 제목 줄 표시 (꺼도 "모두 지우기" 줄은 알림이 있을 때 표시).</param>
    public NotificationListView(AppServices services, UiPalette palette, double maxHeight = 420, bool showHeader = true)
    {
        _services = services;
        _p = palette;
        _showHeader = showHeader;
        Background = Brushes.Transparent;
        SetResourceReference(TextElement.FontFamilyProperty, UiFonts.Key);
        TextElement.SetForeground(this, _p.Text);

        Child = _scroll = new ScrollViewer
        {
            MaxHeight = maxHeight,
            // 넘칠 때만 맥 같은 얇은 스크롤바 (Themes/Controls.xaml ThinScrollBar)
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
            Content = _content,
        };
        if (TryFindResource("ThinScrollBar") is Style thin) _scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), thin);

        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => Rebuild();
        Loaded += (_, _) =>
        {
            _services.Notifications.Changed += OnChanged;
            _clock.Start();
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            _services.Notifications.Changed -= OnChanged;
            _clock.Stop();
        };
    }

    /// <summary><c>new NotificationListView(...)</c> 와 같음 (UIElement 로 받고 싶을 때).</summary>
    public static NotificationListView Build(AppServices services, UiPalette palette, double maxHeight = 420, bool showHeader = true)
        => new(services, palette, maxHeight, showHeader);

    private void OnChanged(object? sender, EventArgs e) => Rebuild();

    private void BeginAnim() => _animating++;

    private void EndAnim()
    {
        if (_animating > 0) _animating--;
        if (_animating == 0 && _pendingRebuild) Rebuild();
    }

    private void Rebuild()
    {
        if (_animating > 0)
        {
            _pendingRebuild = true;
            return;
        }
        _pendingRebuild = false;
        try
        {
            _content.Children.Clear();
            _clearAll = null;
            var items = _services.Notifications.Recent;
            bool wasShowing = _hadItems;
            _hadItems = items.Count > 0;

            if (_showHeader || items.Count > 0)
                _content.Children.Add(BuildHeader(items.Count > 0));
            if (items.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = _services.Notifications.IsAvailable ? "새 알림 없음" : "알림을 읽을 수 없음",
                    FontSize = 12.5,
                    Foreground = _p.SubText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 12),
                };
                _content.Children.Add(empty);
                // 방금 비었으면(모두 지우기·마지막 알림 숨김) 문구가 부드럽게 나타나게
                if (wasShowing) Anim.Reveal(empty, _content.ActualWidth);
                return;
            }

            var groups = items
                .GroupBy(i => i.Aumid, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(i => i.Arrival).ToList())
                .OrderByDescending(g => g[0].Arrival)
                .Take(MaxGroups);
            // 사라진 앱의 펼침 상태 정리
            _expanded.IntersectWith(items.Select(i => i.Aumid));
            foreach (var g in groups)
                _content.Children.Add(BuildGroup(g));
        }
        catch (Exception ex)
        {
            Log.Error("알림 목록 그리기 실패", ex);
        }
    }

    /// <summary>"알림" 제목(선택) + 오른쪽 "모두 지우기" (알림이 있을 때만).</summary>
    private UIElement BuildHeader(bool any)
    {
        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 8), LastChildFill = true, MinHeight = 20 };
        if (any)
        {
            _clearAll = LinkButton("모두 지우기", ClearAll);
            DockPanel.SetDock(_clearAll, Dock.Right);
            header.Children.Add(_clearAll);
        }
        header.Children.Add(new TextBlock
        {
            Text = _showHeader ? "알림" : "",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return header;
    }

    /// <summary>현재 묶음 (aumid) 의 알림, 최신 순. 없으면 빈 목록.</summary>
    private List<NotificationItem> GroupOf(string aumid) => _services.Notifications.Recent
        .Where(i => string.Equals(i.Aumid, aumid, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(i => i.Arrival)
        .ToList();

    private FrameworkElement BuildGroup(List<NotificationItem> group)
    {
        string aumid = group[0].Aumid;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Tag = aumid };

        if (group.Count == 1)
        {
            panel.Children.Add(ItemCard(group[0], panel, null));
            return panel;
        }

        if (!_expanded.Contains(aumid))
        {
            // 겹친 스택: 맨 위 카드 + 아래로 비치는 카드 1~2장
            int layers = Math.Min(2, group.Count - 1);
            var stack = new Grid { Cursor = Cursors.Hand, ToolTip = $"알림 {group.Count}개 — 눌러서 펼치기" };
            for (int i = layers; i >= 1; i--)
            {
                stack.Children.Add(new Border
                {
                    Height = 24,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(10 * i, 0, 10 * i, 0),
                    CornerRadius = new CornerRadius(10),
                    Background = _p.Tile,
                    BorderBrush = _p.CardBorder,
                    BorderThickness = new Thickness(0.5),
                    Opacity = i == 1 ? 0.85 : 0.6,
                });
            }
            var top = NotificationUi.Card(_services, _p, group[0], _p.Tile, compactExtra: group.Count - 1);
            top.Margin = new Thickness(0, 0, 0, PeekStep * layers);
            stack.Children.Add(top);
            // 호버 시 묶음 × = 이 앱 알림 전부 숨기기 (밀려나며 사라짐). 카드 × 와 같은 자리·모양
            var closeGroup = NotificationUi.CloseButton(_p, () => HideGroup(panel, aumid));
            closeGroup.Margin = new Thickness(-6, -6, 0, 0);
            closeGroup.ToolTip = "이 묶음 지우기";
            stack.Children.Add(closeGroup);
            stack.Margin = new Thickness(6, 6, 0, 0); // × 가 잘리지 않게
            stack.MouseEnter += (_, _) => NotificationUi.ShowClose(closeGroup, true);
            stack.MouseLeave += (_, _) => NotificationUi.ShowClose(closeGroup, false);
            stack.MouseLeftButtonUp += (_, e) =>
            {
                if (e.Handled) return; // × 클릭
                e.Handled = true;
                Expand(panel, aumid);
            };
            panel.Children.Add(stack);
            return panel;
        }

        // 펼친 묶음: 헤더(앱 이름 · 접기 · 모두 숨기기) + 모든 카드
        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 6), LastChildFill = true };
        var hide = LinkButton("모두 숨기기", () => HideGroup(panel, aumid));
        var collapse = LinkButton("접기", () => Fold(panel, aumid));
        DockPanel.SetDock(hide, Dock.Right);
        DockPanel.SetDock(collapse, Dock.Right);
        header.Children.Add(hide);
        header.Children.Add(collapse);
        header.Children.Add(new TextBlock
        {
            Text = group[0].AppName,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _p.SubText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(header);
        int z = MaxPerGroup + 1;
        foreach (var item in group.Take(MaxPerGroup))
        {
            var card = ItemCard(item, panel, header);
            card.Margin = new Thickness(0, 0, 0, 6);
            Panel.SetZIndex(card, z--); // 위 카드가 앞 (펼칠 때 아래 카드가 위 카드 밑에서 나옴)
            panel.Children.Add(card);
        }
        return panel;
    }

    /// <summary>클릭 = 앱 열기, 호버 시 × = 이 알림 숨기기 (밀려나며 사라짐).</summary>
    private FrameworkElement ItemCard(NotificationItem item, StackPanel groupPanel, FrameworkElement? groupHeader)
    {
        var host = new Grid { Cursor = Cursors.Hand };
        var card = NotificationUi.Card(_services, _p, item, _p.Tile);
        host.Children.Add(card);
        var close = NotificationUi.CloseButton(_p, () => HideItem(item, host, groupPanel, groupHeader));
        close.Margin = new Thickness(-6, -6, 0, 0);
        host.Children.Add(close);
        host.Margin = new Thickness(6, 6, 0, 0); // × 가 잘리지 않게
        host.MouseEnter += (_, _) => NotificationUi.ShowClose(close, true);
        host.MouseLeave += (_, _) => NotificationUi.ShowClose(close, false);
        host.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            _services.Notifications.Open(item);
            ItemOpened?.Invoke();
        };
        return host;
    }

    // ───────────────────────── 숨기기 / 지우기 ─────────────────────────

    /// <summary>× : 카드가 밀려나며 사라진 뒤 숨김. 묶음에 하나만 남게 되면 묶음 헤더도 함께 접음.</summary>
    private void HideItem(NotificationItem item, FrameworkElement host, StackPanel groupPanel, FrameworkElement? groupHeader)
    {
        int cards = groupPanel.Children.Count - (groupHeader != null ? 1 : 0);
        // 묶음의 마지막 카드면 묶음 전체(아래 간격 포함)를 접음
        FrameworkElement target = cards <= 1 ? groupPanel : host;
        BeginAnim();
        Anim.SlideAway(target, () =>
        {
            Safe(() => _services.Notifications.Hide(item));
            EndAnim();
        });
        if (groupHeader != null && cards == 2)
        {
            BeginAnim();
            Anim.Disappear(groupHeader, 140, () => Anim.Collapse(groupHeader, 200, EndAnim));
        }
    }

    /// <summary>묶음 "모두 숨기기": 묶음 전체가 밀려나며 사라진 뒤 그 앱 알림 숨김.</summary>
    private void HideGroup(FrameworkElement groupPanel, string aumid)
    {
        BeginAnim();
        Anim.SlideAway(groupPanel, () =>
        {
            Safe(() => _services.Notifications.HideApp(aumid));
            EndAnim();
        });
    }

    /// <summary>맨 위 "모두 지우기": 묶음이 위에서부터 차례로 사라진 뒤 모든 알림 숨김 (몽독 화면에서만).</summary>
    private void ClearAll()
    {
        var groups = _content.Children.OfType<StackPanel>().Where(g => g.Tag is string).ToList();
        if (_clearAll != null)
        {
            _clearAll.IsHitTestVisible = false;
            Anim.Fade(_clearAll, 0, 150);
        }
        BeginAnim();
        int left = groups.Count;
        void Finish()
        {
            Safe(() => _services.Notifications.HideAll()); // 숨김 파일 저장·Changed 한 번
            EndAnim();
        }
        if (left == 0)
        {
            Finish();
            return;
        }
        for (int i = 0; i < groups.Count; i++)
        {
            Anim.SlideAway(groups[i], () =>
            {
                if (--left == 0) Finish();
            }, delayMs: Math.Min(i, StaggerMax) * StaggerMs);
        }
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("알림 숨기기 실패", ex); }
    }

    // ───────────────────────── 펼치기 / 접기 ─────────────────────────

    /// <summary>
    /// 목록의 index 자리 묶음을 새 요소로 교체. UIElementCollection 의 인덱서 대입은
    /// 기존 자식 연결을 먼저 끊지 않아 ArgumentException 을 던지므로 RemoveAt + Insert.
    /// </summary>
    private void ReplaceChild(int index, UIElement fresh)
    {
        _content.Children.RemoveAt(index);
        _content.Children.Insert(index, fresh);
    }

    /// <summary>겹친 스택 → 펼친 묶음. 카드들이 맨 위 카드 밑에서 아래로 풀려 나옴.</summary>
    private void Expand(StackPanel oldPanel, string aumid)
    {
        _expanded.Add(aumid);
        var group = GroupOf(aumid);
        int index = _content.Children.IndexOf(oldPanel);
        if (group.Count < 2 || index < 0 || _animating > 0)
        {
            Rebuild();
            return;
        }
        double oldHeight = oldPanel.ActualHeight;
        double width = _content.ActualWidth;
        var fresh = (StackPanel)BuildGroup(group);
        ReplaceChild(index, fresh);
        // 펼친 묶음이 목록 아래로 넘치면 보이도록 스크롤 (레이아웃 끝난 뒤)
        void Reveal() => Dispatcher.BeginInvoke(() => fresh.BringIntoView(), DispatcherPriority.Loaded);
        if (!Anim.Enabled || width <= 0)
        {
            Reveal();
            return;
        }

        fresh.Measure(new Size(width, double.PositiveInfinity));
        double newHeight = fresh.DesiredSize.Height - fresh.Margin.Top - fresh.Margin.Bottom;
        BeginAnim();
        Anim.Height(fresh, oldHeight, newHeight, ToggleMs, Anim.QuintOut, () => { EndAnim(); Reveal(); }, clearAtEnd: true);

        // 헤더는 살짝 늦게 페이드 인, 카드는 겹친 자리(맨 위 카드 위치 + 비침 간격)에서 제자리로
        var header = (FrameworkElement)fresh.Children[0];
        Anim.Appear(header, 180, fromY: -4, delayMs: 60);
        double headerHeight = header.DesiredSize.Height;
        double top = headerHeight;
        for (int k = 1; k < fresh.Children.Count; k++)
        {
            var card = (FrameworkElement)fresh.Children[k];
            int n = k - 1;
            double from = -top + Math.Min(n, 2) * PeekStep;
            var (_, shift) = Anim.Transforms(card);
            Anim.SlideFrom(shift, TranslateTransform.YProperty, from, ToggleMs, Anim.QuintOut);
            if (n >= 1)
                card.BeginAnimation(OpacityProperty, Anim.FromTo(n <= 2 ? 0.7 : 0, 1, ToggleMs * 0.8, Anim.EaseOut));
            top += card.DesiredSize.Height;
        }
    }

    /// <summary>펼친 묶음 → 겹친 스택. 카드들이 맨 위 카드 밑으로 모여 들어간 뒤 스택으로 바뀜.</summary>
    private void Fold(StackPanel oldPanel, string aumid)
    {
        _expanded.Remove(aumid);
        var group = GroupOf(aumid);
        int index = _content.Children.IndexOf(oldPanel);
        double width = _content.ActualWidth;
        if (group.Count < 2 || index < 0 || _animating > 0 || !Anim.Enabled || width <= 0)
        {
            Rebuild();
            return;
        }
        var fresh = (FrameworkElement)BuildGroup(group);
        fresh.Measure(new Size(width, double.PositiveInfinity));
        double newHeight = fresh.DesiredSize.Height - fresh.Margin.Top - fresh.Margin.Bottom;

        oldPanel.IsHitTestVisible = false;
        BeginAnim();
        var header = (FrameworkElement)oldPanel.Children[0];
        Anim.Disappear(header, 120);
        for (int k = 1; k < oldPanel.Children.Count; k++)
        {
            var card = (FrameworkElement)oldPanel.Children[k];
            int n = k - 1;
            double y = card.TranslatePoint(new Point(0, 0), oldPanel).Y;
            double to = -y + Math.Min(n, 2) * PeekStep; // 스택에서는 맨 위 카드가 묶음 맨 위 (헤더 없음)
            var (_, shift) = Anim.Transforms(card);
            shift.BeginAnimation(TranslateTransform.YProperty, Anim.To(to, ToggleMs, Anim.QuintOut));
            if (n >= 1)
                card.BeginAnimation(OpacityProperty, Anim.To(n <= 2 ? 0.6 : 0, ToggleMs * 0.8, Anim.EaseOut));
        }
        Anim.Height(oldPanel, oldPanel.ActualHeight, newHeight, ToggleMs, Anim.QuintOut, () =>
        {
            int at = _content.Children.IndexOf(oldPanel);
            if (at >= 0) ReplaceChild(at, fresh);
            EndAnim();
        }, clearAtEnd: false);
    }

    private Button LinkButton(string text, Action onClick)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("CardLinkButton"),
            Content = new TextBlock { Text = text, FontSize = 12 },
            Foreground = _p.SubText,
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 0, 0, 0),
            Focusable = false,
            Cursor = Cursors.Hand,
        };
        b.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return b;
    }
}

/// <summary>배너와 목록이 함께 쓰는 알림 카드 그리기 도우미.</summary>
internal static class NotificationUi
{
    private const string IconFontName = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly FontFamily IconFont = new(IconFontName);
    private static readonly Dictionary<string, (DateTime Stamp, ImageSource? Image)> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 둥근 카드: [아이콘] 앱 이름 · 시간 / 제목(굵게) / 본문 2줄 말줄임 [+ 오른쪽 작은 이미지].
    /// compactExtra > 0 이면 앱 이름 옆에 "+n" (겹친 묶음의 맨 위 카드).
    /// </summary>
    /// <param name="iconOverride">앱 아이콘 대신 쓸 이미지 (몽독 자체 알림 — 업데이트 배너 등).</param>
    public static Border Card(AppServices services, UiPalette p, NotificationItem item, Brush background, int compactExtra = 0, ImageSource? iconOverride = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = iconOverride is null ? Icon(services, item) : new Image { Source = iconOverride, Width = 36, Height = 36 };
        icon.Margin = new Thickness(0, 1, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        var head = new DockPanel { LastChildFill = true };
        var time = new TextBlock
        {
            Text = FormatTime(item.Arrival),
            FontSize = 11.5,
            Foreground = p.SubText,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(time, Dock.Right);
        head.Children.Add(time);
        head.Children.Add(new TextBlock
        {
            Text = compactExtra > 0 ? $"{item.AppName}  +{compactExtra}" : item.AppName,
            FontSize = 11.5,
            Foreground = p.SubText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(head);

        string? title = item.Title;
        string body = string.Join("\n", item.Lines);
        if (string.IsNullOrEmpty(title)) title = item.Attribution;
        if (!string.IsNullOrEmpty(title))
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
        if (body.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                LineHeight = 17,
                MaxHeight = 34, // 2줄
                Margin = new Thickness(0, 1, 0, 0),
            });
        grid.Children.Add(text);

        if (item.ImagePath is { } imgPath && LoadImage(imgPath, 120) is { } img)
        {
            var thumb = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                ClipToBounds = true,
                Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill },
            };
            Grid.SetColumn(thumb, 2);
            grid.Children.Add(thumb);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = background,
            BorderBrush = p.CardBorder,
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(12, 10, 12, 10),
            Child = grid,
        };
    }

    /// <summary>앱 아이콘 (36px). 토스트가 로고 대체 이미지(보낸 사람 사진 등)를 주면 그것 + 오른쪽 아래 작은 앱 아이콘.</summary>
    private static FrameworkElement Icon(AppServices services, NotificationItem item)
    {
        const double size = 36;
        ImageSource appIcon;
        try { appIcon = services.Icons.GetIcon(item.ToPin(), services.Settings.Current.Dock.IconStyle); }
        catch (Exception ex)
        {
            Log.Warn($"알림 앱 아이콘 실패: {item.Aumid} ({ex.Message})");
            appIcon = new DrawingImage();
        }

        if (item.AppLogoPath is { } logoPath && LoadImage(logoPath, 96) is { } logo)
        {
            var g = new Grid { Width = size, Height = size };
            var img = new Image { Source = logo, Stretch = Stretch.UniformToFill, Width = size, Height = size };
            img.Clip = item.AppLogoCircle
                ? new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2)
                : new RectangleGeometry(new Rect(0, 0, size, size), 8, 8);
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            g.Children.Add(img);
            var badge = new Image
            {
                Source = appIcon,
                Width = 16,
                Height = 16,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -3, -3),
            };
            RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.HighQuality);
            g.Children.Add(badge);
            return g;
        }

        var icon = new Image { Source = appIcon, Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        return icon;
    }

    /// <summary>작은 원형 × (호버 때만 보임). 클릭은 카드 클릭으로 번지지 않음.</summary>
    public static Border CloseButton(UiPalette p, Action onClick)
    {
        var b = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = p.CardBackground,
            BorderBrush = p.CardBorder,
            BorderThickness = new Thickness(0.75),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            Opacity = 0,
            IsHitTestVisible = false,
            ToolTip = "닫기",
            Child = new TextBlock
            {
                Text = "",
                FontFamily = IconFont,
                FontSize = 8,
                Foreground = p.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return b;
    }

    public static void ShowClose(Border close, bool show)
    {
        Anim.Fade(close, show ? 1 : 0, show ? 100 : 120);
        close.IsHitTestVisible = show;
    }

    /// <summary>"지금" / "n분 전" / "오후 3:12" / "어제" / "10월 5일".</summary>
    public static string FormatTime(DateTime local)
    {
        var now = DateTime.Now;
        var diff = now - local;
        if (diff < TimeSpan.FromMinutes(1)) return "지금";
        if (diff < TimeSpan.FromHours(1)) return $"{(int)diff.TotalMinutes}분 전";
        if (local.Date == now.Date) return local.ToString("tt h:mm", CultureInfo.GetCultureInfo("ko-KR"));
        if (local.Date == now.Date.AddDays(-1)) return "어제";
        return local.ToString("M월 d일", CultureInfo.GetCultureInfo("ko-KR"));
    }

    /// <summary>로컬 이미지 파일 (축소 디코드, Frozen). 실패 시 null. 경로+수정 시각으로 캐시.</summary>
    public static ImageSource? LoadImage(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path)) return null;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            string key = path + "|" + decodeWidth;
            if (ImageCache.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit.Image;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 파일 핸들을 바로 닫음
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            if (ImageCache.Count > 64) ImageCache.Clear();
            ImageCache[key] = (stamp, bmp);
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"알림 이미지 읽기 실패 ({ex.GetType().Name})");
            return null;
        }
    }
}
