using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독 아이콘 옆(독 안쪽)에 뜨는 격자 판의 공통 틀 — 독 폴더(#24-B)·독 휴지통(#24-C).
/// 머리줄(이름 + 아이콘 바꾸기 연필 + 오른쪽 버튼), 썸네일 칸(4~5열), 아래 링크.
/// 닫기: 바깥 클릭·다른 창 활성화·Esc. 열기·닫기는 페이드 + 살짝 올라오기 150ms (되튐 없음). Esc 를 받으려고 열릴 때 활성화함.
/// </summary>
internal abstract class DockStackPanel : Window
{
    protected const double Cell = 92, Thumb = 56;

    protected readonly AppServices Services;
    protected readonly UiPalette P;
    protected readonly MonitorInfo Monitor;
    private readonly Rect _anchor;
    private readonly DockEdge _edge;
    private readonly OutsideClickWatcher _watch;
    private readonly Border _card;
    private bool _closing;
    private Point _pressAt;
    private string? _pressKey;
    protected bool Dragging;

    /// <summary>머리줄 연필: 아이콘 바꾸기 카드를 열어 달라는 요청 (판은 닫힘).</summary>
    public event Action? EditIconRequested;

    protected DockStackPanel(AppServices services, UiPalette palette, Rect anchorDip, DockEdge edge, MonitorInfo monitor, string title)
    {
        Services = services;
        P = palette;
        _anchor = anchorDip;
        _edge = edge;
        Monitor = monitor;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = title;
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 12;
        Foreground = P.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Left = -32000;
        Top = -32000;

        _card = new Border
        {
            Background = P.CardBackground,
            BorderBrush = P.Divider,
            BorderThickness = new Thickness(0.75),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 12, 12, 8),
            Margin = new Thickness(16),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = P.ShadowOpacity },
        };
        Content = _card;

        _watch = new OutsideClickWatcher(services, () => new[] { new Rect(Left, Top, ActualWidth, ActualHeight), Inflate(_anchor) }, CloseAnimated);
        SourceInitialized += (_, _) =>
        {
            Services.DesktopWindows.MakeOverlay(this);
            _watch.IgnoreHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle; // 열면서 Activate 한 자기 자신으로 닫히지 않게
        };
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) =>
        {
            Place();
            Anim.Appear(_card, 150, fromX: _edge switch { DockEdge.Left => -8, DockEdge.Right => 8, _ => 0 },
                fromY: _edge switch { DockEdge.Bottom => 8, DockEdge.Top => -8, _ => 0 });
            Activate();
            Keyboard.Focus(this);
            _watch.Start();
        };
        Deactivated += (_, _) => { if (!Dragging && !KeepOpenOnDeactivate) CloseAnimated(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseAnimated(); } };
        Closed += (_, _) => _watch.Stop();
    }

    /// <summary>확인 카드 등 몽독 창을 띄우는 동안 판을 닫지 않음.</summary>
    protected bool KeepOpenOnDeactivate { get; set; }

    protected void SetBody(UIElement body) => _card.Child = body;

    private static Rect Inflate(Rect r)
    {
        r.Inflate(2, 2);
        return r;
    }

    // ───────────────────────── 조각 ─────────────────────────

    /// <summary>머리줄: 이름 + 작은 연필(아이콘 바꾸기 — 메뉴에만 숨기지 않음) + 오른쪽 끝 요소(예 [비우기…]).</summary>
    protected UIElement Header(string title, UIElement? right = null)
    {
        var dock = new DockPanel { Margin = new Thickness(4, 0, 4, 8), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var pencil = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("아이콘 바꾸기"),
            Child = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = P.SubText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        pencil.MouseEnter += (_, _) => pencil.Background = P.Hover;
        pencil.MouseLeave += (_, _) => pencil.Background = Brushes.Transparent;
        pencil.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseAnimated();
            EditIconRequested?.Invoke();
        };
        left.Children.Add(pencil);
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);
        if (right is not null)
        {
            if (right is FrameworkElement fe) fe.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);
        }
        return dock;
    }

    /// <summary>칸들을 4~5열 격자로 (화면 60% 넘으면 스크롤).</summary>
    protected UIElement Grid(IReadOnlyList<UIElement> cells)
    {
        int cols = cells.Count <= 8 ? 4 : 5;
        var grid = new WrapPanel { Width = cols * Cell };
        foreach (var c in cells) grid.Children.Add(c);
        return new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(200, Monitor.WorkArea.Height * 0.6),
            Focusable = false,
        };
    }

    /// <summary>
    /// 썸네일 칸 하나. click = 누름(null 이면 누름 없음), dragPath = 다른 앱으로 끌어낼 파일(null 이면 끌기 없음).
    /// 썸네일은 판을 먼저 보여 준 뒤 하나씩 (느린 미리보기가 판을 늦추지 않게, 닫히면 멈춤).
    /// </summary>
    protected Border MakeCell(string name, string thumbPath, string tooltip, Action? click, string? dragPath, ContextMenu? menu)
    {
        var image = new Image { Width = Thumb, Height = Thumb, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Dispatcher.BeginInvoke(() => { if (!_closing) image.Source = DockFolderService.Thumbnail(thumbPath, 96); }, DispatcherPriority.Background);

        var text = new TextBlock
        {
            Text = name,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 32,
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var stack = new StackPanel { Width = Cell - 8 };
        stack.Children.Add(image);
        stack.Children.Add(text);
        var cell = new Border
        {
            Width = Cell - 4,
            Margin = new Thickness(2),
            Padding = new Thickness(2, 6, 2, 6),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Child = stack,
            ToolTip = tooltip,
            Cursor = click is null ? Cursors.Arrow : Cursors.Hand,
            ContextMenu = menu,
        };
        string key = thumbPath;
        cell.MouseEnter += (_, _) => cell.Background = P.Tile;
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        cell.MouseLeftButtonDown += (_, e) =>
        {
            _pressAt = e.GetPosition(this);
            _pressKey = key;
            cell.CaptureMouse();
            e.Handled = true;
        };
        cell.MouseMove += (_, e) =>
        {
            if (dragPath is null || _pressKey != key || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressAt.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            cell.ReleaseMouseCapture();
            _pressKey = null;
            DragOut(cell, dragPath);
        };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            bool pressed = _pressKey == key;
            cell.ReleaseMouseCapture();
            _pressKey = null;
            if (pressed) click?.Invoke();
            e.Handled = true;
        };
        return cell;
    }

    /// <summary>다른 앱·바탕 화면으로 끌어 놓기 (복사·이동은 놓는 쪽과 윈도우가 정함). 끄는 동안 판은 닫지 않음.</summary>
    private void DragOut(DependencyObject source, string path)
    {
        Dragging = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            Log.Warn($"판에서 끌어 놓기 실패: {ex.GetType().Name}");
        }
        finally
        {
            Dragging = false;
        }
    }

    protected Button Link(string text, Action click)
    {
        var link = new Button
        {
            Style = (Style)Application.Current.FindResource("CardLinkButton"),
            Foreground = P.Accent,
            Padding = new Thickness(6, 6, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new TextBlock { Text = text, FontSize = 12 },
        };
        link.Click += (_, _) => click();
        return link;
    }

    protected TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = P.SubText,
        Margin = new Thickness(4, 8, 4, 12),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    // ───────────────────────── 위치·닫기 ─────────────────────────

    /// <summary>아이콘 옆(독 안쪽)에, 작업 영역 안으로.</summary>
    private void Place()
    {
        if (ActualWidth <= 0 || _closing) return;
        Services.DesktopWindows.EnsureOnMonitor(this, Monitor);
        var work = Monitor.WorkArea;
        if (work.IsEmpty || work.Width <= 0) work = Monitor.Bounds;
        const double gap = 2; // 카드 여백(16)이 이미 있음
        double w = ActualWidth, h = ActualHeight;
        double cx = _anchor.Left + _anchor.Width / 2, cy = _anchor.Top + _anchor.Height / 2;
        double left, top;
        switch (_edge)
        {
            case DockEdge.Left: left = _anchor.Right + gap; top = cy - h / 2; break;
            case DockEdge.Top: left = cx - w / 2; top = _anchor.Bottom + gap; break;
            case DockEdge.Right: left = _anchor.Left - gap - w; top = cy - h / 2; break;
            default: left = cx - w / 2; top = _anchor.Top - gap - h; break;
        }
        Left = Math.Round(Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - w)));
        Top = Math.Round(Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h)));
    }

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _watch.Stop();
        Anim.Disappear(_card, 120, () => Dispatcher.BeginInvoke(Close));
    }

    protected bool IsClosing => _closing;
}
