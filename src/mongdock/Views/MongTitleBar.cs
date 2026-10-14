using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 몽독 창 제목 줄 A안 (#25 §8): 윈도우 기본 제목 줄을 없애고(WindowChrome) 본문 색이 창 맨 위까지, 왼쪽 위에 맥처럼 동그라미 3개
/// (닫기 #F09595 · 최소화 #FAC775 · 최대화 #97C459, 다크는 조금 어둡게, 창이 비활성이면 회색, 근처에 마우스를 올리면 ✕ − ⤢).
/// 끌어서 옮기기·두 번 눌러 최대화·가장자리 크기 조절·Alt+Space 는 WindowChrome 이 그대로, 최대화 동그라미는 WM_NCHITTEST 에서
/// HTMAXBUTTON 이라 마우스를 올리면 윈도우 11 스냅 레이아웃이 뜸. 크기를 못 바꾸는 창은 최대화 동그라미를 흐리게·누를 수 없게.
/// 쓰는 법: 창 생성자에서 <see cref="Install"/> 한 번, 내용 Grid 를 만들 때마다 <see cref="Overlay"/> 를 맨 위에 얹음(내용은 위 <see cref="Height"/> 만큼 비움).
/// </summary>
internal sealed class MongTitleBar
{
    /// <summary>끌어 옮기는 위쪽 띠 높이 (DIP). 이 안에는 누를 것을 두지 않음.</summary>
    public const double Height = 36;
    private const double Dot = 12, Gap = 8, Left = 14;

    private static readonly Dictionary<Window, MongTitleBar> Bars = new();

    private readonly Window _w;
    private readonly bool _canMax;
    private UiPalette _p;
    private Ellipse? _close, _min, _max;
    private TextBlock? _closeGlyph, _minGlyph, _maxGlyph;
    private bool _hover;
    private bool? _previewActive;

    /// <summary>시험 그림(report-test)용: 활성/비활성·호버 모양을 강제.</summary>
    internal static void Preview(Window w, bool active, bool hover)
    {
        if (!Bars.TryGetValue(w, out var bar)) return;
        bar._previewActive = active;
        bar._hover = hover;
        bar.Paint();
    }
    private readonly DispatcherTimer _poll;

    private MongTitleBar(Window w, UiPalette p)
    {
        _w = w;
        _p = p;
        _canMax = w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(80) };
        _poll.Tick += (_, _) => UpdateHover();
    }

    /// <summary>창 생성자에서 한 번: 기본 제목 줄 없애기 + 비활성 회색·스냅 레이아웃 처리.</summary>
    public static void Install(Window w, UiPalette p)
    {
        var bar = new MongTitleBar(w, p);
        Bars[w] = bar;
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = Height,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1), // DWM 그림자·둥근 모서리 유지
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
        });
        w.Activated += (_, _) => bar.Paint();
        w.Deactivated += (_, _) => bar.Paint();
        w.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(w) is HwndSource src) src.AddHook(bar.Hook);
        };
        // 마우스 올림 확인은 보이고 최소화가 아닐 때만 (최소화해 둔 설정 창이 초당 12번 깨지 않게 — 0.5.0 검토 13번)
        void SyncPoll() { if (w.IsVisible && w.WindowState != WindowState.Minimized) bar._poll.Start(); else bar._poll.Stop(); }
        w.IsVisibleChanged += (_, _) => SyncPoll();
        w.Closed += (_, _) => { bar._poll.Stop(); Bars.Remove(w); };
        w.StateChanged += (_, _) =>
        {
            SyncPoll();
            // 최대화하면 WindowChrome 창이 화면 밖으로 테두리만큼 넘침 — 그만큼 안쪽 여백
            if (w.Content is FrameworkElement c) c.Margin = w.WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
    }

    /// <summary>내용 Grid 위에 얹을 동그라미 3개 (+ 가운데 제목, title 이 null 이면 생략). 행·열 전체에 걸쳐 놓을 것.</summary>
    public static UIElement Overlay(Window w, UiPalette p, string? title)
    {
        var bar = Bars[w];
        bar._p = p;
        var layer = new Grid { VerticalAlignment = VerticalAlignment.Top, Height = Height };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Left, 0, 0, 0) };
        (bar._close, bar._closeGlyph) = bar.Button(dots, "", Loc.T("닫기"), () => w.Close(), enabled: true);
        (bar._min, bar._minGlyph) = bar.Button(dots, "", Loc.T("최소화"), () => w.WindowState = WindowState.Minimized, enabled: w.ResizeMode != ResizeMode.NoResize);
        (bar._max, bar._maxGlyph) = bar.Button(dots, "", Loc.T("최대화"), bar.ToggleMax, enabled: bar._canMax);
        layer.Children.Add(dots);
        if (!string.IsNullOrEmpty(title))
            layer.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                Foreground = p.SoftAccentText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            });
        Grid.SetColumnSpan(layer, 99);
        Grid.SetRowSpan(layer, 99);
        bar.Paint();
        return layer;
    }

    private (Ellipse, TextBlock) Button(Panel host, string glyph, string tip, Action click, bool enabled)
    {
        var cell = new Grid { Width = Dot, Height = Dot, Margin = new Thickness(0, 0, Gap, 0), ToolTip = tip, Cursor = Cursors.Arrow };
        var dot = new Ellipse { Width = Dot, Height = Dot };
        var mark = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 7,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0x3A, 0x22, 0x22)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        cell.Children.Add(dot);
        cell.Children.Add(mark);
        cell.IsEnabled = enabled;
        WindowChrome.SetIsHitTestVisibleInChrome(cell, true);
        cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (enabled) click(); };
        host.Children.Add(cell);
        return (dot, mark);
    }

    private void ToggleMax() => _w.WindowState = _w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>색: 활성 = 몽독 파스텔(다크는 조금 어둡게), 비활성 = 회색. 못 누르는 버튼은 흐리게.</summary>
    private void Paint()
    {
        if (_close is null || _min is null || _max is null) return;
        bool light = _p.IsLight, active = _previewActive ?? _w.IsActive || _hover;
        if (_previewActive is not null) active = _previewActive.Value || _hover;
        Brush Pick(string lightHex, string darkHex) => Converters.BrushParser.Frozen(Converters.BrushParser.Hex(active ? (light ? lightHex : darkHex) : (light ? "#FFD3D1DA" : "#FF54525C")));
        _close.Fill = Pick("#FFF09595", "#FFD98282");
        _min.Fill = Pick("#FFFAC775", "#FFDDAD5E");
        _max.Fill = Pick("#FF97C459", "#FF80AA48");
        _min.Opacity = ((FrameworkElement)_min.Parent).IsEnabled ? 1 : 0.4;
        _max.Opacity = _canMax ? 1 : 0.4;
        foreach (var g in new[] { _closeGlyph, _minGlyph, _maxGlyph }) if (g is not null) g.Opacity = _hover && active ? 1 : 0;
        if (_maxGlyph is not null) _maxGlyph.Opacity = _hover && _canMax ? 1 : 0;
    }

    /// <summary>동그라미 근처에 마우스 (최대화 동그라미는 비클라이언트라 WPF 마우스 이벤트가 오지 않아 위치로 봄).</summary>
    private void UpdateHover()
    {
        if (_close is null || !_w.IsVisible) return;
        bool over = false;
        try
        {
            var p = Mouse.GetPosition(_w);
            double right = Left + 3 * Dot + 2 * Gap + 6;
            over = p.X >= Left - 6 && p.X <= right && p.Y >= 0 && p.Y <= Height;
        }
        catch (InvalidOperationException) { }
        if (over == _hover || _previewActive is not null) return;
        _hover = over;
        Paint();
    }

    // ── 스냅 레이아웃: 최대화 동그라미 위를 HTMAXBUTTON 으로 ──

    private const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, HTMAXBUTTON = 9;

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_canMax || _max is null) return IntPtr.Zero;
        switch (msg)
        {
            case WM_NCHITTEST:
                if (OverMax(lParam)) { handled = true; return new IntPtr(HTMAXBUTTON); }
                break;
            case WM_NCLBUTTONDOWN:
                if (wParam.ToInt32() == HTMAXBUTTON) { handled = true; return IntPtr.Zero; } // 누름은 먹고 뗄 때 동작
                break;
            case WM_NCLBUTTONUP:
                if (wParam.ToInt32() == HTMAXBUTTON) { handled = true; ToggleMax(); return IntPtr.Zero; }
                break;
        }
        return IntPtr.Zero;
    }

    private bool OverMax(IntPtr lParam)
    {
        if (_max is null || !_max.IsVisible) return false;
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF)), y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        try
        {
            var p = _max.PointFromScreen(new Point(x, y));
            return p.X >= -2 && p.Y >= -2 && p.X <= Dot + 2 && p.Y <= Dot + 2;
        }
        catch (InvalidOperationException) { return false; }
    }
}
