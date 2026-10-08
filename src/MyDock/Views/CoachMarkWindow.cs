using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 코치마크 말풍선 카드 (맥 느낌, 상태 패널 카드 톤). 앵커 쪽으로 꼬리(삼각형)가 나오고,
/// 단계 표시("2 / 4")·제목·본문·[건너뛰기]·[다음 →/완료]. 포커스를 뺏지 않음(NOACTIVATE, Topmost), 클릭만으로.
/// 창 하나를 재사용하며 단계마다 내용·위치를 바꾼다 (짧은 페이드 + 살짝 미끄러짐).
/// </summary>
internal sealed class CoachMarkWindow : Window
{
    private const double CardWidth = 300;
    /// <summary>그림자·꼬리가 들어갈 카드 바깥 여백.</summary>
    private const double Outer = 22;
    private const double TailLength = 9;
    private const double TailBase = 18;
    /// <summary>앵커(강조 링 바깥)와 꼬리 끝 사이.</summary>
    private const double AnchorGap = 7;
    private const double ScreenMargin = 8;

    private enum Side { None, Below, Above, RightOf, LeftOf }

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly Grid _root;
    private readonly Border _shadow;
    private readonly Border _card;
    private readonly Path _tail;
    private bool _closing;

    public event Action? NextClicked;
    public event Action? SkipClicked;
    public event Action? LinkClicked;

    public CoachMarkWindow(AppServices services, UiPalette p)
    {
        _services = services;
        _p = p;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "mongdock CoachMark";
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 13;
        Foreground = p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _shadow = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = p.CardBackground,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Direction = 270, Opacity = p.ShadowOpacity + 0.06 },
        };
        _card = new Border
        {
            Width = CardWidth,
            CornerRadius = new CornerRadius(12),
            Background = p.CardBackground,
            BorderBrush = p.CardBorder,
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(16, 13, 16, 12),
        };
        _tail = new Path
        {
            Fill = p.CardBackground,
            Stroke = p.CardBorder,
            StrokeThickness = 0.75,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _root = new Grid { Margin = new Thickness(Outer) };
        _root.Children.Add(_shadow);
        _root.Children.Add(_card);
        _root.Children.Add(_tail);
        Content = _root;

        SourceInitialized += (_, _) => services.DesktopWindows.MakeOverlay(this);
    }

    /// <summary>단계 표시. anchor = 가리킬 사각형(monitor 기준 DIP), null 이면 그 모니터 가운데.</summary>
    public void ShowPage(CoachPage page, int index, int total, Rect? anchor, MonitorInfo monitor, bool animate)
    {
        if (_closing) return;
        if (animate && IsVisible && Anim.Enabled)
        {
            // 이전 카드는 짧게 흐려진 뒤 새 내용으로
            _root.IsHitTestVisible = false;
            Anim.Disappear(_root, 90, () =>
            {
                if (_closing) return;
                _root.IsHitTestVisible = true;
                Apply(page, index, total, anchor, monitor, animate: true);
            }, ease: Anim.EaseOut);
            return;
        }
        Apply(page, index, total, anchor, monitor, animate);
    }

    private void Apply(CoachPage page, int index, int total, Rect? anchor, MonitorInfo monitor, bool animate)
    {
        _card.Child = BuildBody(page, index, total);
        _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double cw = CardWidth;
        double ch = Math.Max(40, _card.DesiredSize.Height);

        var screen = monitor.Bounds;
        Side side = anchor is { } a0 ? PickSide(a0, screen) : Side.None;
        double cardLeft, cardTop;
        if (anchor is not { } a)
        {
            cardLeft = screen.Left + (screen.Width - cw) / 2;
            cardTop = screen.Top + screen.Height * 0.36 - ch / 2;
        }
        else
        {
            double cx = a.Left + a.Width / 2, cy = a.Top + a.Height / 2;
            switch (side)
            {
                case Side.Below:
                    cardLeft = cx - cw / 2;
                    cardTop = a.Bottom + AnchorGap + TailLength;
                    break;
                case Side.Above:
                    cardLeft = cx - cw / 2;
                    cardTop = a.Top - AnchorGap - TailLength - ch;
                    break;
                case Side.RightOf:
                    cardLeft = a.Right + AnchorGap + TailLength;
                    cardTop = cy - ch / 2;
                    break;
                default:
                    cardLeft = a.Left - AnchorGap - TailLength - cw;
                    cardTop = cy - ch / 2;
                    break;
            }
        }
        // 화면 가장자리에서 잘리지 않게
        cardLeft = Math.Max(screen.Left + ScreenMargin, Math.Min(cardLeft, screen.Right - ScreenMargin - cw));
        cardTop = Math.Max(screen.Top + ScreenMargin, Math.Min(cardTop, screen.Bottom - ScreenMargin - ch));
        cardLeft = Math.Round(cardLeft);
        cardTop = Math.Round(cardTop);

        PlaceTail(side, anchor, cardLeft, cardTop, cw, ch);

        double left = cardLeft - Outer, top = cardTop - Outer;
        Left = left;
        Top = top;
        if (!IsVisible)
        {
            Show(); // 새 창: WPF 가 Left/Top 이 속한 모니터의 DPI 로 만든다
        }
        // 배율이 다른 모니터에 만들어졌으면 옮긴 뒤 다시 배치 (단일 모니터는 아무것도 안 함)
        if (_services.DesktopWindows.EnsureOnMonitor(this, monitor))
        {
            Left = left;
            Top = top;
        }

        if (animate)
        {
            // 앵커 쪽에서 살짝 밀려 나오며 페이드 인 (짧게)
            var (fx, fy) = side switch
            {
                Side.Below => (0.0, -6.0),
                Side.Above => (0.0, 6.0),
                Side.RightOf => (-6.0, 0.0),
                Side.LeftOf => (6.0, 0.0),
                _ => (0.0, 6.0),
            };
            Anim.Appear(_root, 160, fromScale: 0.98, fromX: fx, fromY: fy, origin: new Point(0.5, 0.5));
        }
        else
        {
            _root.BeginAnimation(OpacityProperty, null);
            _root.Opacity = 1;
        }
    }

    /// <summary>앵커가 화면 위쪽이면 아래에, 아래쪽이면 위에, 가운데 높이면(왼쪽·오른쪽 독) 옆에.</summary>
    private static Side PickSide(Rect a, Rect screen)
    {
        if (a.Bottom <= screen.Top + screen.Height * 0.35) return Side.Below;
        if (a.Top >= screen.Top + screen.Height * 0.65) return Side.Above;
        return a.Left + a.Width / 2 < screen.Left + screen.Width / 2 ? Side.RightOf : Side.LeftOf;
    }

    /// <summary>
    /// 꼬리: 카드 테두리 안쪽으로 1px 겹쳐 그 자리의 테두리선을 덮고, 양옆 두 변만 테두리색으로 (열린 도형).
    /// 꼬리 끝은 앵커 가운데를 향하되 카드 모서리 둥근 부분은 피함.
    /// </summary>
    private void PlaceTail(Side side, Rect? anchor, double cardLeft, double cardTop, double cw, double ch)
    {
        if (side == Side.None || anchor is not { } a)
        {
            _tail.Visibility = Visibility.Collapsed;
            return;
        }
        const double corner = 16;
        double half = TailBase / 2;
        Geometry g;
        if (side is Side.Below or Side.Above)
        {
            double x = Math.Clamp(a.Left + a.Width / 2 - cardLeft, corner + half, cw - corner - half);
            _tail.Width = TailBase;
            _tail.Height = TailLength + 1;
            _tail.HorizontalAlignment = HorizontalAlignment.Left;
            if (side == Side.Below)
            {
                _tail.VerticalAlignment = VerticalAlignment.Top;
                _tail.Margin = new Thickness(x - half, -TailLength, 0, 0);
                g = Open(new Point(0, TailLength + 1), new Point(half, 0), new Point(TailBase, TailLength + 1));
            }
            else
            {
                _tail.VerticalAlignment = VerticalAlignment.Bottom;
                _tail.Margin = new Thickness(x - half, 0, 0, -TailLength);
                g = Open(new Point(0, 0), new Point(half, TailLength + 1), new Point(TailBase, 0));
            }
        }
        else
        {
            double y = Math.Clamp(a.Top + a.Height / 2 - cardTop, corner + half, ch - corner - half);
            _tail.Width = TailLength + 1;
            _tail.Height = TailBase;
            _tail.VerticalAlignment = VerticalAlignment.Top;
            if (side == Side.RightOf)
            {
                _tail.HorizontalAlignment = HorizontalAlignment.Left;
                _tail.Margin = new Thickness(-TailLength, y - half, 0, 0);
                g = Open(new Point(TailLength + 1, 0), new Point(0, half), new Point(TailLength + 1, TailBase));
            }
            else
            {
                _tail.HorizontalAlignment = HorizontalAlignment.Right;
                _tail.Margin = new Thickness(0, y - half, -TailLength, 0);
                g = Open(new Point(0, 0), new Point(TailLength + 1, half), new Point(0, TailBase));
            }
        }
        _tail.Data = g;
        _tail.Visibility = Visibility.Visible;
    }

    private static Geometry Open(Point a, Point tip, Point b)
    {
        var fig = new PathFigure { StartPoint = a, IsClosed = false, IsFilled = true };
        fig.Segments.Add(new LineSegment(tip, true));
        fig.Segments.Add(new LineSegment(b, true));
        var g = new PathGeometry { Figures = { fig } };
        g.Freeze();
        return g;
    }

    // ───────────────────────── 내용 ─────────────────────────

    private UIElement BuildBody(CoachPage page, int index, int total)
    {
        var body = new StackPanel();
        if (total > 1)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"{index + 1} / {total}",
                FontSize = 11.5,
                Foreground = _p.SubText,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }
        body.Children.Add(new TextBlock
        {
            Text = page.Title,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(page.Body))
        {
            body.Children.Add(new TextBlock
            {
                Text = page.Body,
                Foreground = _p.SubText,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                Margin = new Thickness(0, 5, 0, 0),
            });
        }
        if (page.Groups is { Count: > 0 } groups)
            body.Children.Add(BuildGroups(groups));
        if (page.ReleaseLink)
        {
            var link = new Button
            {
                Style = (Style)FindResource("CardLinkButton"),
                Foreground = _p.Accent,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(-6, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = new TextBlock { Text = "릴리스 노트 전체 보기 ↗", FontSize = 12.5, Foreground = _p.Accent },
            };
            link.Click += (_, _) => LinkClicked?.Invoke();
            body.Children.Add(link);
        }

        bool last = index >= total - 1;
        var buttons = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!last)
        {
            var skip = new Button
            {
                Style = (Style)FindResource("CardLinkButton"),
                Foreground = _p.Text,
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(-8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = new TextBlock { Text = "건너뛰기", FontSize = 13, Foreground = _p.SubText },
            };
            skip.Click += (_, _) => SkipClicked?.Invoke();
            buttons.Children.Add(skip);
        }
        var next = new Button
        {
            Style = (Style)FindResource("CardButton"),
            Background = _p.Accent,
            Foreground = _p.AccentText,
            Height = 28,
            MinWidth = 76,
            Content = new TextBlock { Text = last ? "완료" : "다음 →", FontSize = 13, FontWeight = FontWeights.SemiBold },
        };
        next.Click += (_, _) => NextClicked?.Invoke();
        Grid.SetColumn(next, 2);
        buttons.Children.Add(next);
        body.Children.Add(buttons);
        return body;
    }

    /// <summary>버전(묶음) 머리글 + 항목 한 줄씩. 길면 얇은 스크롤바.</summary>
    private UIElement BuildGroups(List<(string Header, List<string> Items)> groups)
    {
        var list = new StackPanel();
        bool first = true;
        foreach (var (header, items) in groups)
        {
            list.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = _p.SubText,
                Margin = new Thickness(0, first ? 0 : 8, 0, 3),
            });
            first = false;
            foreach (string item in items)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = _p.Accent, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 1, 0, 0) });
                var text = new TextBlock { Text = item, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                list.Children.Add(row);
            }
        }
        var scroll = new ScrollViewer
        {
            Content = list,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(0, 0, 2, 0),
        };
        if (TryFindResource("ThinScrollBar") is Style thin) scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), thin);
        return new Border
        {
            Background = _p.Tile,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 6, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Child = scroll,
        };
    }

    /// <summary>짧게 흐려진 뒤 닫힘 (애니메이션 꺼짐이면 바로).</summary>
    public void FadeClose()
    {
        if (_closing) return;
        _closing = true;
        _root.IsHitTestVisible = false;
        if (!IsVisible || !Anim.Enabled)
        {
            Close();
            return;
        }
        Anim.Disappear(_root, 110, () =>
        {
            try { Close(); }
            catch (Exception ex) { Log.Warn($"코치마크 닫기 실패: {ex.Message}"); }
        }, ease: Anim.EaseOut);
    }
}

/// <summary>
/// 앵커 주변 강조 링 (accent 색 둥근 사각형 + 부드럽게 퍼지며 사라지는 맥박, Anim.Enabled 일 때만).
/// 클릭은 통과 (WS_EX_TRANSPARENT) — 링이 앵커 위에 있어도 앵커를 바로 눌러 볼 수 있다.
/// </summary>
internal sealed class CoachRingWindow : Window
{
    /// <summary>링이 앵커보다 바깥으로 나오는 거리.</summary>
    private const double Outset = 3;
    /// <summary>맥박이 퍼질 여유.</summary>
    private const double Pad = 12;

    public CoachRingWindow(AppServices services, Rect anchor, MonitorInfo monitor, double radius, Brush accent)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "mongdock CoachRing";

        var ring = anchor;
        ring.Inflate(Outset, Outset);
        // 화면 가장자리(상단바 버튼 등)에 붙은 앵커는 링 테두리가 화면 밖으로 나가 잘려 보이므로 화면 안쪽으로 당김
        var screen = monitor.Bounds;
        const double edge = 1;
        double l = Math.Max(ring.Left, screen.Left + edge), t = Math.Max(ring.Top, screen.Top + edge);
        double r = Math.Min(ring.Right, screen.Right - edge), b = Math.Min(ring.Bottom, screen.Bottom - edge);
        if (r - l >= 8 && b - t >= 8) ring = new Rect(l, t, r - l, b - t);
        double left = Math.Round(ring.Left - Pad), top = Math.Round(ring.Top - Pad);
        Width = Math.Ceiling(ring.Width + Pad * 2);
        Height = Math.Ceiling(ring.Height + Pad * 2);

        var grid = new Grid { Margin = new Thickness(Pad) };
        var pulse = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        grid.Children.Add(pulse);
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
        });
        Content = grid;

        if (Anim.Enabled)
        {
            // 어느 크기든 약 9px 만큼 퍼지게 (큰 독 패널도 과하게 커지지 않음) — RenderTransform·Opacity 만 (GPU 합성)
            double sx = 1 + 18 / Math.Max(10, ring.Width);
            double sy = 1 + 18 / Math.Max(10, ring.Height);
            var scale = new ScaleTransform(1, 1);
            pulse.RenderTransform = scale;
            var span = TimeSpan.FromMilliseconds(1400);
            DoubleAnimation Make(double from, double to) => new(from, to, new Duration(TimeSpan.FromMilliseconds(1100)))
            {
                EasingFunction = Anim.EaseOut,
            };
            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, Duration = span };
            var ax = Make(1, sx);
            var ay = Make(1, sy);
            var ao = Make(0.65, 0);
            Storyboard.SetTarget(ax, pulse);
            Storyboard.SetTargetProperty(ax, new PropertyPath("RenderTransform.ScaleX"));
            Storyboard.SetTarget(ay, pulse);
            Storyboard.SetTargetProperty(ay, new PropertyPath("RenderTransform.ScaleY"));
            Storyboard.SetTarget(ao, pulse);
            Storyboard.SetTargetProperty(ao, new PropertyPath(OpacityProperty));
            sb.Children.Add(ax);
            sb.Children.Add(ay);
            sb.Children.Add(ao);
            Loaded += (_, _) => sb.Begin();
            Closed += (_, _) => sb.Stop();
            // 링 자체도 처음엔 살짝 흐려졌다 나타남
            Opacity = 0;
            Loaded += (_, _) => BeginAnimation(OpacityProperty, Anim.FromTo(0, 1, 160, Anim.EaseOut));
        }

        SourceInitialized += (_, _) =>
        {
            services.DesktopWindows.MakeOverlay(this);
            MakeClickThrough();
        };
        Left = left;
        Top = top;
        Loaded += (_, _) =>
        {
            if (services.DesktopWindows.EnsureOnMonitor(this, monitor))
            {
                Left = left;
                Top = top;
            }
        };
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private void MakeClickThrough()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT);
        }
        catch (Exception ex) { Log.Warn($"강조 링 클릭 통과 설정 실패: {ex.Message}"); }
    }
}
