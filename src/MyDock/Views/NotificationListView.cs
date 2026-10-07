using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 맥 알림 센터 같은 알림 목록 (재사용 UI 요소 — 달력 패널 등 다른 카드 안에 붙여 쓴다).
/// - 앱별 묶음: 2개 이상이면 카드가 겹친 스택 모양, 누르면 펼침 (헤더: 앱 이름 · 접기 · 모두 숨기기).
/// - 항목 클릭 = 그 앱 열기(INotificationService.Open — 그 알림은 몽독 목록에서 숨김) → <see cref="ItemOpened"/>.
/// - 항목에 마우스를 올리면 × (그 알림만 숨기기). 숨기기는 몽독 화면에서만 — 윈도우 알림 DB 는 그대로.
/// - 화면에 붙어 있는 동안(Loaded)만 서비스 Changed 를 구독하고 30초마다 "n분 전" 갱신. Unloaded 에서 해제.
///
/// 사용: <c>var list = new NotificationListView(services, palette) { Width = 320 }; list.ItemOpened += () => Close(); panel.Children.Add(list);</c>
/// </summary>
internal sealed class NotificationListView : Border
{
    private const int MaxPerGroup = 30;
    private const int MaxGroups = 30;

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly bool _showHeader;
    private readonly StackPanel _content = new();
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _clock;

    /// <summary>항목을 눌러 앱을 열었음 — 패널을 닫을 때 사용.</summary>
    public event Action? ItemOpened;

    /// <param name="maxHeight">이보다 길면 안에서 스크롤 (휠).</param>
    /// <param name="showHeader">맨 위 "알림" 제목 줄 표시.</param>
    public NotificationListView(AppServices services, UiPalette palette, double maxHeight = 420, bool showHeader = true)
    {
        _services = services;
        _p = palette;
        _showHeader = showHeader;
        Background = Brushes.Transparent;
        SetResourceReference(TextElement.FontFamilyProperty, UiFonts.Key);
        TextElement.SetForeground(this, _p.Text);

        Child = new ScrollViewer
        {
            MaxHeight = maxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
            Content = _content,
        };

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

    private void Rebuild()
    {
        try
        {
            _content.Children.Clear();
            var items = _services.Notifications.Recent;
            if (_showHeader)
                _content.Children.Add(new TextBlock
                {
                    Text = "알림",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(2, 0, 0, 8),
                });
            if (items.Count == 0)
            {
                _content.Children.Add(new TextBlock
                {
                    Text = _services.Notifications.IsAvailable ? "새 알림 없음" : "알림을 읽을 수 없음",
                    FontSize = 12.5,
                    Foreground = _p.SubText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 12),
                });
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

    private UIElement BuildGroup(List<NotificationItem> group)
    {
        string aumid = group[0].Aumid;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        if (group.Count == 1)
        {
            panel.Children.Add(ItemCard(group[0]));
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
            top.Margin = new Thickness(0, 0, 0, 6 * layers);
            stack.Children.Add(top);
            stack.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _expanded.Add(aumid);
                Rebuild();
            };
            panel.Children.Add(stack);
            return panel;
        }

        // 펼친 묶음: 헤더(앱 이름 · 접기 · 모두 숨기기) + 모든 카드
        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 6), LastChildFill = true };
        var hide = LinkButton("모두 숨기기", () => _services.Notifications.HideApp(aumid));
        var collapse = LinkButton("접기", () =>
        {
            _expanded.Remove(aumid);
            Rebuild();
        });
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
        foreach (var item in group.Take(MaxPerGroup))
        {
            var card = ItemCard(item);
            card.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(card);
        }
        return panel;
    }

    /// <summary>클릭 = 앱 열기, 호버 시 × = 이 알림 숨기기.</summary>
    private FrameworkElement ItemCard(NotificationItem item)
    {
        var host = new Grid { Cursor = Cursors.Hand };
        var card = NotificationUi.Card(_services, _p, item, _p.Tile);
        host.Children.Add(card);
        var close = NotificationUi.CloseButton(_p, () => _services.Notifications.Hide(item));
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
    public static Border Card(AppServices services, UiPalette p, NotificationItem item, Brush background, int compactExtra = 0)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = Icon(services, item);
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
        close.Opacity = show ? 1 : 0;
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
