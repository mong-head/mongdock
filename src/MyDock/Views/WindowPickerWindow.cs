using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyDock.Converters;
using MyDock.Models;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 창이 여러 개인 앱의 독 아이콘을 눌렀을 때 아이콘 옆(독 안쪽)에 뜨는 창 선택 패널.
/// 창마다 카드 = DWM 실시간 썸네일(비율 유지) + 제목 + 프로필(창 아이콘 + 이름) + 다른 데스크톱이면 "데스크톱 N" 배지.
/// 다른 데스크톱(cloaked)·최소화 창은 썸네일이 비므로 큰 앱 아이콘을 대신 보여준다.
/// DWM 썸네일은 레이어드 창에 그려지지 않을 수 있어 AllowsTransparency=False 창 + EnableBlur(DWM 둥근 모서리·틴트)로 카드 모양을 낸다.
/// 포커스를 뺏지 않는 창이고, 바깥 클릭·다른 창 활성화 시 닫힌다.
/// </summary>
internal sealed class WindowPickerWindow : Window
{
    private const double ThumbW = 220, ThumbH = 140;

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly Rect _anchor;          // 독 아이콘 화면 영역 (DIP)
    private readonly DockEdge _edge;
    private readonly ScrollViewer _scroll;
    private readonly OutsideClickWatcher _watch;
    private readonly List<(AppWindowInfo Window, Border Slot)> _slots = new();
    private readonly Dictionary<IntPtr, IWindowThumbnail> _thumbs = new();
    private bool _placed;

    public WindowPickerWindow(AppServices services, UiPalette palette, IReadOnlyList<AppWindowInfo> windows,
        ImageSource? appIcon, Rect anchorDip, DockEdge edge)
    {
        _services = services;
        _p = palette;
        _anchor = anchorDip;
        _edge = edge;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;          // DWM 썸네일용 (레이어드 창 불가)
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "MyDock Windows";
        FontFamily = new FontFamily("Segoe UI Variable Text, Noto Sans KR, Malgun Gothic");
        FontSize = 13;
        Foreground = _p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Left = -32000;
        Top = -32000;

        var screen = services.DesktopWindows.GetPrimaryScreenBounds();
        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            MaxWidth = Math.Max(ThumbW + 40, screen.Width * 0.7 - 24),
        };
        foreach (var w in windows) wrap.Children.Add(BuildCard(w, appIcon));

        _scroll = new ScrollViewer
        {
            Content = wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = screen.Height * 0.7 - 24,
            Focusable = false,
        };
        _scroll.ScrollChanged += (_, _) => UpdateThumbnails();

        // 블러가 안 되는 환경 대비 반투명 카드색을 깔아 둠 (블러가 되면 그 위에 틴트)
        Content = new Border
        {
            Background = _p.CardBackground,
            Padding = new Thickness(10),
            Child = _scroll,
        };

        _watch = new OutsideClickWatcher(services, InsideAreas, Close);
        SourceInitialized += (_, _) =>
        {
            _services.DesktopWindows.MakeOverlay(this);
            try
            {
                var c = ((SolidColorBrush)_p.CardBackground).Color;
                _services.DesktopWindows.EnableBlur(this, Color.FromArgb(0xE6, c.R, c.G, c.B)); // DWM 둥근 모서리 + 틴트
            }
            catch (Exception ex) { Log.Error("창 선택 패널 블러 실패", ex); }
        };
        SizeChanged += (_, _) => { Place(); UpdateThumbnails(); };
        Loaded += (_, _) =>
        {
            Place();
            Dispatcher.BeginInvoke(UpdateThumbnails, DispatcherPriority.Loaded);
            _watch.Start();
        };
        Closed += (_, _) =>
        {
            _watch.Stop();
            foreach (var t in _thumbs.Values) t.Dispose();
            _thumbs.Clear();
        };
    }

    private IEnumerable<Rect> InsideAreas()
    {
        var self = new Rect(Left, Top, ActualWidth, ActualHeight);
        var anchor = _anchor;
        anchor.Inflate(2, 2);
        return new[] { self, anchor };
    }

    /// <summary>아이콘 옆(독 안쪽 방향)에 배치, 화면 안으로 클램프.</summary>
    private void Place()
    {
        if (ActualWidth <= 0) return;
        var work = _services.DesktopWindows.GetPrimaryWorkArea();
        if (work.IsEmpty || work.Width <= 0) work = _services.DesktopWindows.GetPrimaryScreenBounds();
        const double gap = 12;
        double w = ActualWidth, h = ActualHeight;
        double cx = _anchor.Left + _anchor.Width / 2, cy = _anchor.Top + _anchor.Height / 2;
        double left, top;
        switch (_edge)
        {
            case DockEdge.Left: left = _anchor.Right + gap; top = cy - h / 2; break;
            case DockEdge.Bottom: left = cx - w / 2; top = _anchor.Top - gap - h; break;
            case DockEdge.Top: left = cx - w / 2; top = _anchor.Bottom + gap; break;
            default: left = _anchor.Left - gap - w; top = cy - h / 2; break;
        }
        left = Math.Clamp(left, work.Left + 6, Math.Max(work.Left + 6, work.Right - w - 6));
        top = Math.Clamp(top, work.Top + 6, Math.Max(work.Top + 6, work.Bottom - h - 6));
        Left = Math.Round(left);
        Top = Math.Round(top);
        _placed = true;
    }

    // ───────────────────────── 카드 ─────────────────────────

    private UIElement BuildCard(AppWindowInfo w, ImageSource? appIcon)
    {
        bool elsewhere = !w.OnCurrentDesktop;
        bool liveThumb = !elsewhere && !w.IsMinimized;

        // 썸네일 자리 (DWM 이 이 영역에 그림) 또는 큰 앱 아이콘
        var slot = new Border
        {
            Width = ThumbW,
            Height = ThumbH,
            CornerRadius = new CornerRadius(6),
            Background = _p.Tile,
        };
        if (liveThumb)
        {
            _slots.Add((w, slot));
        }
        else
        {
            var g = new Grid();
            g.Children.Add(new Image
            {
                Source = appIcon,
                Width = 64,
                Height = 64,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            string? badge = elsewhere
                ? (w.DesktopIndex > 0 ? $"데스크톱 {w.DesktopIndex}" : "다른 데스크톱")
                : "최소화됨";
            g.Children.Add(new Border
            {
                Background = elsewhere ? _p.Accent : _p.CircleOff,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 2, 8, 3),
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = badge,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = elsewhere ? _p.AccentText : _p.CircleOffGlyph,
                },
            });
            slot.Child = g;
        }

        var stack = new StackPanel { Width = ThumbW };
        stack.Children.Add(slot);
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(w.Title) ? AppNames.Get(w) : w.Title,
            FontSize = 13,
            Margin = new Thickness(2, 7, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // 프로필: 창 아이콘(프로필 아바타 배지) + 프로필 이름, 다른 데스크톱이면 오른쪽에 "데스크톱 N"
        var info = new DockPanel { Margin = new Thickness(2, 3, 2, 0), LastChildFill = true };
        ImageSource? winIcon = null;
        string? profile = null;
        try { winIcon = _services.Icons.GetWindowIcon(w.Hwnd); } catch { }
        try { profile = _services.Windows.GetProfileName(w); } catch { }
        if (winIcon != null || !string.IsNullOrEmpty(profile))
        {
            if (winIcon != null)
            {
                info.Children.Add(new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(8),
                    Background = new ImageBrush(winIcon) { Stretch = Stretch.UniformToFill },
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            info.Children.Add(new TextBlock
            {
                Text = profile ?? AppNames.Get(w),
                FontSize = 12,
                Foreground = _p.SubText,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(info);
        }

        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("CardButton"),
            Background = Brushes.Transparent,
            Foreground = _p.Text,
            Padding = new Thickness(8),
            Margin = new Thickness(2),
            Content = stack,
            Cursor = Cursors.Arrow,
        };
        var hwnd = w.Hwnd;
        button.Click += (_, _) =>
        {
            Close();
            try { _services.Launcher.Activate(hwnd); } // 다른 데스크톱이면 이동까지 백엔드가 처리
            catch (Exception ex) { Log.Error("창 선택 실패", ex); }
        };
        return button;
    }

    // ───────────────────────── DWM 썸네일 ─────────────────────────

    /// <summary>각 썸네일 자리의 창 내 위치(DIP)에 비율 유지해서 그림. 스크롤로 가려진 자리는 숨김.</summary>
    private void UpdateThumbnails()
    {
        if (!IsLoaded || !_placed) return;
        var viewport = new Rect(_scroll.TranslatePoint(new Point(0, 0), this), new Size(_scroll.ViewportWidth, _scroll.ViewportHeight));

        foreach (var (w, slot) in _slots)
        {
            var origin = slot.TranslatePoint(new Point(0, 0), this);
            var area = new Rect(origin, new Size(slot.ActualWidth, slot.ActualHeight));
            bool visible = viewport.Contains(area.TopLeft) && viewport.Contains(area.BottomRight);

            if (!_thumbs.TryGetValue(w.Hwnd, out var thumb))
            {
                if (!visible) continue;
                try { thumb = _services.DesktopWindows.CreateThumbnail(this, w.Hwnd, area); }
                catch (Exception ex)
                {
                    Log.Error("창 미리보기 생성 실패", ex);
                    thumb = null;
                }
                if (thumb == null) continue;
                _thumbs[w.Hwnd] = thumb;
            }

            thumb.Update(visible ? Fit(area, thumb.SourceSize) : new Rect(0, 0, 0, 0)); // 가려지면 크기 0 으로 숨김
        }
    }

    /// <summary>원본 비율을 유지해 영역 안 가운데에 맞춤.</summary>
    private static Rect Fit(Rect area, Size source)
    {
        if (source.Width <= 0 || source.Height <= 0) return area;
        double scale = Math.Min(area.Width / source.Width, area.Height / source.Height);
        double w = source.Width * scale, h = source.Height * scale;
        return new Rect(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }
}
