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
    private ContextMenu? _openMenu;
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
        if (!Layered)
        {
            // 큰 판(앱 모음): 레이어드 창 대신 일반 창 + DWM 둥근 모서리 — 첫 프레임 합성이 무거워
            // "투명하다가 확 뜨는" 일이 없게. 창 배경 = 카드 색, 그림자·여백은 DWM 이.
            AllowsTransparency = false;
            Background = P.CardBackground;
            _card.Margin = new Thickness(0);
            _card.Effect = null;
            _card.CornerRadius = new CornerRadius(8); // DWM 둥근 모서리(8px)와 같게 — 얇은 테두리가 모서리에서 끊기지 않게
            _card.BorderThickness = new Thickness(1);
            _card.BorderBrush = P.Divider;
            _card.Padding = new Thickness(14, 14, 14, 8);
        }

        _watch = new OutsideClickWatcher(services, () => new[] { new Rect(Left, Top, ActualWidth, ActualHeight), Inflate(_anchor) }
            .Concat(_openMenu is { IsOpen: true } m ? OutsideClickWatcher.MenuAreas(m) : Enumerable.Empty<Rect>()), CloseAnimated)
        {
            LogName = title, // 닫는 이유(바깥 클릭 위치·활성화된 창)를 로그에
            ActivationGraceMs = 600,
        };
        SourceInitialized += (_, _) =>
        {
            Services.DesktopWindows.MakeOverlay(this);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _watch.IgnoreHwnd = hwnd; // 열면서 Activate 한 자기 자신으로 닫히지 않게
            if (!Layered) DwmCard(hwnd);
        };
        SizeChanged += (_, _) => Place();
        _showClock = System.Diagnostics.Stopwatch.StartNew();
        if (!Layered) _card.Opacity = 0.5;
        // 일반 창(앱 모음): 첫 프레임이 실제로 화면에 나간 뒤 0.5→1.0 120ms (빈 구간 없이 바로 보이고 살짝 차오름)
        ContentRendered += (_, _) =>
        {
            FirstFrameMs = _showClock.ElapsedMilliseconds;
            if (Layered) return;
            _card.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(120)));
        };
        Loaded += (_, _) =>
        {
            Place();
            if (Layered)
                Anim.Appear(_card, 150, fromX: _edge switch { DockEdge.Left => -8, DockEdge.Right => 8, _ => 0 },
                    fromY: _edge switch { DockEdge.Bottom => 8, DockEdge.Top => -8, _ => 0 });
            Activate();
            Keyboard.Focus(this);
            _watch.Start();
        };
        Deactivated += (_, _) =>
        {
            if (Dragging || KeepOpenOnDeactivate) return;
            // 열린 직후 잠깐의 활성화 변화(누른 독·이전 창이 포커스를 다시 가져가는 것)로는 닫지 않음 — 바깥 클릭은 OutsideClickWatcher 가
            if (_showClock.ElapsedMilliseconds < 600)
            {
                Log.Info($"{title}: 열린 직후 비활성화 무시 ({_showClock.ElapsedMilliseconds}ms)");
                return;
            }
            Log.Info($"{title}: 닫음 — 비활성화");
            CloseAnimated();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseAnimated(); } };
        Closed += (_, _) => _watch.Stop();
    }

    /// <summary>true 면 독 버튼 옆이 아니라 모니터 작업 영역 정가운데 (앱 모음 판).</summary>
    protected virtual bool Centered => false;

    /// <summary>false 면 레이어드(투명) 창 대신 일반 창 + DWM 둥근 모서리 (큰 판 — 첫 프레임이 가벼움).</summary>
    protected virtual bool Layered => true;

    private readonly System.Diagnostics.Stopwatch _showClock;

    /// <summary>창을 만든 뒤 첫 프레임이 그려지기까지 (열기 시간 로그).</summary>
    public long FirstFrameMs { get; private set; } = -1;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>일반 창을 카드처럼: 윈도우 11 둥근 모서리, 얇은 테두리 색, 기본 창 열기 애니메이션 끔(우리 페이드와 겹치지 않게).</summary>
    private void DwmCard(IntPtr hwnd)
    {
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
            // 윈도우 시스템 테두리(강조색·활성/비활성에 따라 바뀜)는 끔 — 테두리는 몽독 판과 같은 얇은 선(_card)
            int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref none, sizeof(int));
            // 테두리 없는 창에도 DWM 그림자 (프레임을 1px 넓힘 — 창 배경이 불투명이라 보이지 않음)
            var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            int off = 1;
            DwmSetWindowAttribute(hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref off, sizeof(int));
            int dark = P.IsLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        }
        catch (Exception ex) { Log.Warn($"판 창 모양 설정 실패: {ex.GetType().Name}"); }
    }

    /// <summary>확인 카드 등 몽독 창을 띄우는 동안 판을 닫지 않음.</summary>
    protected bool KeepOpenOnDeactivate { get; set; }

    protected void SetBody(UIElement body) => _card.Child = body;

    /// <summary>독 버튼 자리: 확대된 아이콘은 칸보다 크게(화면 안쪽으로) 그려지므로 아이콘 크기만큼 넉넉히 — 그 버튼을 다시 눌러 닫을 때 바깥 클릭으로 먼저 닫히지 않게.</summary>
    private static Rect Inflate(Rect r)
    {
        double grow = Math.Max(r.Width, r.Height);
        r.Inflate(grow, grow);
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
        return ThinScroll(grid, Math.Max(200, Monitor.WorkArea.Height * 0.6));
    }

    /// <summary>몽독 다른 창과 같은 얇은 겹침형 스크롤바 (넘칠 때만, Themes/Controls.xaml OverlayScrollViewer·ThinScrollBar).</summary>
    protected ScrollViewer ThinScroll(object content, double maxHeight)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = maxHeight,
            Focusable = false,
        };
        if (TryFindResource("OverlayScrollViewer") is Style overlay) scroll.Style = overlay;
        if (TryFindResource("ThinScrollBar") is Style thin) scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), thin);
        return scroll;
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
        if (menu is not null)
        {
            menu.Opened += (_, _) => _openMenu = menu;
            menu.Closed += (_, _) => { if (_openMenu == menu) _openMenu = null; };
        }
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
    private void DragOut(DependencyObject source, string path) => DragData(source, new DataObject(DataFormats.FileDrop, new[] { path }));

    /// <summary>끌기 시작 (끝날 때까지 돌아오지 않음). 끄는 동안 판은 닫지 않음.</summary>
    protected void DragData(DependencyObject source, DataObject data, DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link)
    {
        Dragging = true;
        try
        {
            DragDrop.DoDragDrop(source, data, allowed);
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
        if (Centered)
        {
            Left = Math.Round(work.Left + Math.Max(0, (work.Width - w) / 2));
            Top = Math.Round(work.Top + Math.Max(0, (work.Height - h) / 2));
            return;
        }
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
        if (!Layered)
        {
            Close(); // 일반 창은 바로 (반투명으로 사라지는 중간 상태 없이)
            return;
        }
        Anim.Disappear(_card, 120, () => Dispatcher.BeginInvoke(Close));
    }

    protected bool IsClosing => _closing;
}
