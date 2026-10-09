using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Mongdock.Converters;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 맥 스타일 독.
/// - 이 창(투명 레이어드)은 아이콘·점·테두리·확대만 그리고, 크기는 "패널 + 확대 여유"뿐이다.
/// - 블러 배경은 패널과 같은 위치·크기의 <see cref="DockBackdropWindow"/> (이 창의 owner → 항상 바로 아래).
/// - 모드: AutoHide(가장자리에 커서가 닿으면 슬라이드 인) / Overlay(항상 보임) 
/// 포커스를 뺏지 않는다(MakeOverlay = WS_EX_NOACTIVATE).
/// </summary>
public partial class DockWindow : Window
{
    private static readonly Brush HitBrush = BrushParser.Frozen(Color.FromArgb(1, 0, 0, 0));
    private static readonly Brush PanelHitBrush = BrushParser.Frozen(Color.FromArgb(1, 255, 255, 255));

    private readonly AppServices _services;
    private DockLayout _layout;
    private DockBackdropWindow _backdrop;
    private DockLabelWindow? _label;
    private DockGhostWindow? _ghost;
    private bool _subscribed;
    private bool _closed;

    // 항목
    private List<DockItemViewModel> _items = new();
    private readonly Dictionary<string, DockItemView> _views = new();
    /// <summary>알림(깜빡임) 받은 창들. 항목의 알림 점 = 그 항목 창 중 하나라도 여기 있으면.</summary>
    private readonly HashSet<IntPtr> _flashed = new();
    /// <summary>창별 마지막 활성화 순번 (클릭 시 "가장 최근 창" 선택용).</summary>
    private readonly Dictionary<IntPtr, long> _lastActive = new();
    private long _activationCounter;
    /// <summary>핀 아닌 실행 중 앱의 표시 순서 (처음 본 순서 유지).</summary>
    private readonly List<string> _runningOrder = new();

    // 배치 (모든 DIP 좌표는 _monitor 기준: px / _monitor.Scale)
    private MonitorInfo _monitor;      // 독이 놓일 모니터 (Dock.Monitor, 없거나 분리되면 주 모니터)
    private Rect _screen = new(0, 0, 1920, 1080);
    private Rect _shownRect;           // 보일 때의 이 창 위치 (DIP)
    private double _baseLength;        // 확대 전 패널 길이

    // 자동 숨김 / 슬라이드
    private readonly DispatcherTimer _pollTimer;
    private int _pollCount;
    private const double SlideMs = 180;
    private double _hide;              // 0 = 보임, 1 = 숨김
    private double _hideFrom, _hideTo;
    private readonly Stopwatch _slideClock = new();
    private bool _sliding;
    private bool _windowsHidden;
    private long _lastInsideTicks;
    private bool _fullscreen;
    private bool _dialogOpen;

    // 확대
    private double? _cursorAlong;      // 패널 중심 기준 커서의 독 방향 좌표 (패널 위가 아니면 null)
    private bool _magnifying;
    private TimeSpan _lastFrame;
    private bool _renderHooked;

    public DockWindow(AppServices services)
    {
        _services = services;
        _layout = BuildLayout();
        _monitor = services.DesktopWindows.ResolveMonitor(services.Settings.Current.Dock.Monitor);
        UiFonts.Apply(services.Settings.Current);
        InitializeComponent();

        PanelBorder.ContextMenu = new ContextMenu();
        // 바깥(다른 앱) 클릭·다른 창 활성화 시 닫힘. 독 패널 안 클릭은 WPF 가 처리
        OutsideClickWatcher.Attach(PanelBorder.ContextMenu, services, () => new[] { OutsideClickWatcher.ScreenRect(PanelBorder) });
        PanelBorder.ContextMenuOpening += OnPanelContextMenuOpening;
        PanelBorder.SizeChanged += (_, _) => SyncBackdrop();

        Root.MouseMove += OnRootMouseMove;
        ItemsHost.LayoutUpdated += (_, _) => UpdateLabelPosition(); // 확대로 아이콘 위치/크기가 바뀐 뒤
        Root.MouseLeave += OnRootMouseLeave;

        // 빈 영역/구분선 드래그 → 독 이동
        PanelBorder.MouseLeftButtonDown += OnPanelMouseDown;
        PanelBorder.MouseMove += OnPanelMouseMove;
        PanelBorder.MouseLeftButtonUp += OnPanelMouseUp;
        PanelBorder.LostMouseCapture += OnPanelLostCapture;

        // 아이콘 드래그로 순서 바꾸기 / 바깥 파일 끌어다 놓기 (DockWindow.ItemDrag.cs)
        InitItemDrag();
        InitTouch(); // 터치: 뗀 뒤 확대 해제 등 (DockWindow.Touch.cs)

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PerfMode.Interval(AutoHidePollMs) };
        _pollTimer.Tick += OnPoll;

        // 블러 배경 창: 먼저 띄우고 이 창의 owner 로 → z-order 가 항상 바로 아래
        _backdrop = new DockBackdropWindow(services);
        _backdrop.Show();
        Owner = _backdrop;

        SourceInitialized += OnSourceInitialized;
        DpiChanged += OnWindowDpiChanged;
        Loaded += (_, _) => ApplyMode(initial: true);
        Closed += OnClosed;
    }

    // ───────────────────────── 수명 주기 ─────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _services.DesktopWindows.MakeOverlay(this);

        _services.Windows.WindowsChanged += OnWindowsChanged;
        _services.Windows.WindowFlashed += OnWindowFlashed;
        _services.Windows.WindowActivated += OnWindowActivated;
        _services.Settings.SettingsChanged += OnSettingsChanged;
        _services.DesktopWindows.DisplayChanged += OnDisplayChanged;
        _services.DesktopWindows.FullscreenAppChanged += OnFullscreenChanged;
        SystemTheme.Changed += OnSystemThemeChanged;
        AppState.Changed += OnSettingsChanged; // 일시 정지/해제
        DockState.CoachPinChanged += OnCoachPinChanged;
        _subscribed = true;

        ApplyAll();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _pollTimer.Stop();
        _launchTimer?.Stop();
        UnhookRender();
        if (_subscribed)
        {
            _services.Windows.WindowsChanged -= OnWindowsChanged;
            _services.Windows.WindowFlashed -= OnWindowFlashed;
            _services.Windows.WindowActivated -= OnWindowActivated;
            _services.Settings.SettingsChanged -= OnSettingsChanged;
            _services.DesktopWindows.DisplayChanged -= OnDisplayChanged;
            _services.DesktopWindows.FullscreenAppChanged -= OnFullscreenChanged;
            SystemTheme.Changed -= OnSystemThemeChanged;
            AppState.Changed -= OnSettingsChanged;
            DockState.CoachPinChanged -= OnCoachPinChanged;
            _subscribed = false;
        }
        DockState.VisiblePanel = Rect.Empty;
        _label?.Close();
        _ghost?.Close();
        _escTimer.Stop();
        _dragIcon?.Close();
        _picker?.Close();
        foreach (var v in _views.Values) v.Detach();
        _views.Clear();
        _backdrop.Close();
    }

    // ───────────────────────── 서비스 이벤트 ─────────────────────────

    private void OnWindowsChanged(object? sender, EventArgs e) => RefreshItems();

    private void OnWindowFlashed(object? sender, IntPtr hwnd)
    {
        // 이미 보고 있는 창이면 알림 점 불필요
        if (hwnd == _services.Windows.ForegroundWindow) return;
        _flashed.Add(hwnd);
        UpdateStates();
    }

    private void OnWindowActivated(object? sender, IntPtr hwnd)
    {
        _lastActive[hwnd] = ++_activationCounter;
        var item = _items.FirstOrDefault(i => i.Windows.Any(w => w.Hwnd == hwnd));
        if (item != null) ClearNotification(item);
        else _flashed.Remove(hwnd);
        UpdateStates();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplyAll();

    /// <summary>
    /// 해상도·배율(DPI)·작업 영역 변경. 화면 크기(DIP)·트리거 영역·창 위치를 전부 다시 계산하고,
    /// 옛 좌표로 떠 있던 말풍선·창 선택 패널은 닫는다. 자동 숨김이면 숨긴 상태(트리거 대기)로 되돌린다.
    /// </summary>
    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        var oldScreen = _screen;
        _label?.Hide();
        Place();
        // 작업 영역만 바뀐 경우(다른 AppBar 등)는 위치만 갱신. 화면 크기(배율·해상도)가 바뀌었을 때만 상태 초기화
        if (_screen == oldScreen) return;
        _picker?.Close();
        RecreateHelperWindows();
        ResetMagnification();
        if (_layout.Mode == DockMode.AutoHide && !_fullscreen && DockActive)
            ApplyMode(initial: true); // 슬라이드 중이던 옛 좌표 상태를 버리고 깨끗하게 (숨김 + 트리거 대기)
    }

    /// <summary>블러 창·이름 말풍선·드래그 고스트를 새로 만듦 (새 DPI 로 생성되도록).</summary>
    private void RecreateHelperWindows()
    {
        _label?.Close();
        _label = null;
        _ghost?.Close();
        _ghost = null;
        CancelItemDrag();
        _dragIcon?.Close();
        _dragIcon = null;

        var old = _backdrop;
        var fresh = new DockBackdropWindow(_services) { Topmost = !_fullscreen };
        fresh.Show();
        fresh.Hide();          // 핸들만 만들고 숨김 → SyncBackdrop 이 필요할 때 보임
        _backdrop = fresh;
        Owner = fresh;         // z-order: 독 창은 항상 블러 창 바로 위
        old.Close();
        if (_layout.Blur) _backdrop.Apply(_layout.Tint);
        SyncBackdrop();
    }

    /// <summary>이 창의 DPI 가 바뀜 (배율 변경·다른 모니터). 레이아웃이 끝난 뒤 다시 배치.</summary>
    private void OnWindowDpiChanged(object sender, DpiChangedEventArgs e)
        => Dispatcher.BeginInvoke(() => OnDisplayChanged(this, EventArgs.Empty), DispatcherPriority.Loaded);

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        // SystemEvents 는 다른 스레드에서 올 수 있음
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closed && _services.Settings.Current.Dock.Theme == DockTheme.System) ApplyAll();
        });
    }

    private void OnFullscreenChanged(object? sender, bool anyFullscreen)
    {
        // 전체 화면 앱이 독이 있는 모니터에 있을 때만 (다른 모니터의 전체 화면은 무시)
        bool fullscreen = anyFullscreen && _services.DesktopWindows.IsFullscreenOn(_monitor.DeviceName);
        if (fullscreen == _fullscreen) return;
        _fullscreen = fullscreen;
        Topmost = !fullscreen;
        _backdrop.Topmost = !fullscreen;
        if (fullscreen)
        {
            _pollTimer.Stop(); // 전체 화면 동안은 트리거 확인 불필요 (끝나면 ApplyMode 가 다시 시작)
            StopSlide();
            _hide = _hideTo = 1;
            HideWindows();
        }
        else
        {
            // 전체 화면 판정이 배율 전환 중 잠깐 켜졌다 꺼지는 경우도 있으므로 화면 크기부터 다시 계산
            Place();
            ApplyMode(initial: true);
        }
    }

    // ───────────────────────── 전체 적용 ─────────────────────────

    /// <summary>설정 전체 재적용: 레이아웃/색/크기 → 항목 → 공간 예약 → 위치 → 모드.</summary>
    private void ApplyAll()
    {
        if (_closed) return;
        CancelDrag();
        CancelItemDrag();
        EndFileDrag();
        _picker?.Close();
        _layout = BuildLayout();
        _pollTimer.Interval = PerfMode.Interval(AutoHidePollMs);
        UiTheme.Apply(_services.Settings.Current); // 메뉴 색 (라이트/다크)
        UiFonts.Apply(_services.Settings.Current);
        _label?.Hide();
        _cursorAlong = null;
        Root.Background = null;
        ApplyLayout();
        RefreshItems(rebuildViews: true, place: false);
        Place();
        if (!DockActive)
        {
            Deactivate();
            return;
        }
        bool wasInactive = _inactive;
        _inactive = false;
        ApplyMode(initial: wasInactive); // 다시 켜지면 시작할 때처럼 (자동 숨김이면 숨긴 상태로)
    }

    /// <summary>독을 쓰는 중인지: Dock.Enabled 이고 일시 정지가 아님.</summary>
    private bool DockActive => _services.Settings.Current.Dock.Enabled && !AppState.Paused;

    private bool _inactive;

    /// <summary>독 끄기/일시 정지: 창 숨김, 폴링 정지, 열린 메뉴·패널 닫기.</summary>
    private void Deactivate()
    {
        _inactive = true;
        _pollTimer.Stop();
        StopSlide();
        _hide = _hideTo = 1;
        if (PanelBorder.ContextMenu?.IsOpen == true) PanelBorder.ContextMenu.IsOpen = false;
        _picker?.Close();
        _ghost?.Hide();
        CancelItemDrag();
        HideWindows();
    }

    private void ApplyLayout()
    {
        var l = _layout;
        PanelBorder.BorderBrush = l.Border;
        PanelBorder.CornerRadius = new CornerRadius(l.CornerRadius);
        PanelBorder.Padding = new Thickness(l.Padding);
        ItemsHost.Orientation = l.IsVertical ? Orientation.Vertical : Orientation.Horizontal;

        // 가장자리 쪽에 붙이고, 독 방향은 가운데 (창이 패널 중심에 맞춰 놓이므로 확대가 양쪽으로 고르게 퍼짐)
        double m = l.EdgeMargin;
        switch (l.Edge)
        {
            case DockEdge.Left:
                PanelBorder.HorizontalAlignment = HorizontalAlignment.Left;
                PanelBorder.VerticalAlignment = VerticalAlignment.Center;
                PanelBorder.Margin = new Thickness(m, 0, 0, 0);
                break;
            case DockEdge.Bottom:
                PanelBorder.HorizontalAlignment = HorizontalAlignment.Center;
                PanelBorder.VerticalAlignment = VerticalAlignment.Bottom;
                PanelBorder.Margin = new Thickness(0, 0, 0, m);
                break;
            case DockEdge.Top:
                PanelBorder.HorizontalAlignment = HorizontalAlignment.Center;
                PanelBorder.VerticalAlignment = VerticalAlignment.Top;
                PanelBorder.Margin = new Thickness(0, m, 0, 0);
                break;
            default:
                PanelBorder.HorizontalAlignment = HorizontalAlignment.Right;
                PanelBorder.VerticalAlignment = VerticalAlignment.Center;
                PanelBorder.Margin = new Thickness(0, 0, m, 0);
                break;
        }

        if (l.Blur)
        {
            PanelBorder.Background = PanelHitBrush; // 클릭은 받되 보이지 않게 (배경은 블러 창)
            _backdrop.Apply(l.Tint);
            SyncBackdrop();
        }
        else
        {
            PanelBorder.Background = l.SolidBackground;
            _backdrop.Disable();
            _backdrop.Hide();
        }

        _label?.SetColors(l.LabelBackground, l.LabelForeground, l.LabelBorder);
    }

    // ───────────────────────── 위치 ─────────────────────────

    /// <summary>
    /// 가장자리 기준선과 독 방향 범위: 작업 영역(상단바 아래). 독은 공간을 예약하지 않는다 (#17: "공간 차지" 모드 삭제).
    /// forGhost 는 드래그 미리보기용 (지금은 같은 영역).
    /// </summary>
    private void GetFrame(DockEdge edge, bool forGhost, out double edgeLine, out double alongStart, out double alongEnd)
    {
        Rect a = GetPlacementArea();

        edgeLine = edge switch
        {
            DockEdge.Left => a.Left,
            DockEdge.Top => a.Top,
            DockEdge.Bottom => a.Bottom,
            _ => a.Right,
        };
        bool vertical = edge is DockEdge.Left or DockEdge.Right;
        alongStart = vertical ? a.Top : a.Left;
        alongEnd = vertical ? a.Bottom : a.Right;
    }

    /// <summary>
    /// 작업 영역(작업표시줄/다른 AppBar 제외). 상단바가 공간 예약을 안 하면 상단바 높이만큼 내린다.
    /// </summary>
    private Rect GetPlacementArea()
    {
        var s = _monitor.Bounds;
        Rect a = _monitor.WorkArea;
        if (a.IsEmpty || a.Width <= 0 || a.Height <= 0) a = s;

        var top = _services.Settings.Current.TopBar;
        // 상단바가 이 모니터에 있을 때만 (모든 모니터 표시이거나 주 모니터)
        if (top.Enabled && !top.ReserveSpace && (top.ShowOnAllMonitors || _monitor.IsPrimary))
        {
            double minTop = s.Top + Math.Max(0, top.Height);
            if (a.Top < minTop) a = new Rect(a.Left, minTop, a.Width, Math.Max(0, a.Bottom - minTop));
        }
        return a;
    }

    /// <summary>패널 중심의 독 방향 화면 좌표 (Offset 은 화면 전체 길이 기준, 범위 안으로 클램프).</summary>
    private double PanelCenter(DockEdge edge, double offset, double length, double alongStart, double alongEnd)
    {
        bool vertical = edge is DockEdge.Left or DockEdge.Right;
        double c = vertical ? _screen.Top + offset * _screen.Height : _screen.Left + offset * _screen.Width;
        double min = alongStart + length / 2, max = alongEnd - length / 2;
        return min > max ? (alongStart + alongEnd) / 2 : Math.Clamp(c, min, max);
    }

    private double ComputeBaseLength()
    {
        double len = _layout.Padding * 2 + 2;
        foreach (var v in _views.Values) len += v.BaseLength;
        return len + _dropGapLength; // 파일을 끌어 오는 동안 열어 둔 빈 칸
    }

    /// <summary>창(아이콘 영역)을 보일 위치에 배치하고 슬라이드 상태를 반영.</summary>
    private void Place()
    {
        if (_closed) return;
        var l = _layout;
        RefreshMonitor();
        _screen = _monitor.Bounds;
        _baseLength = ComputeBaseLength();

        GetFrame(l.Edge, false, out double edgeLine, out double start, out double end);
        CheckFit(end - start); // 화면보다 길면 아이콘 자동 축소 (DockWindow.Fit.cs)
        double center = PanelCenter(l.Edge, ClampOffset(_services.Settings.Current.Dock.Offset), _baseLength, start, end);
        double len = _baseLength + l.GrowthRoom * 2;
        double t = l.WindowThickness;

        _shownRect = l.Edge switch
        {
            DockEdge.Left => new Rect(edgeLine, center - len / 2, t, len),
            DockEdge.Bottom => new Rect(center - len / 2, edgeLine - t, len, t),
            DockEdge.Top => new Rect(center - len / 2, edgeLine, len, t),
            _ => new Rect(edgeLine - t, center - len / 2, t, len),
        };
        ApplySlidePosition();
    }

    private static double ClampOffset(double v) => double.IsNaN(v) ? 0.5 : Math.Clamp(v, 0, 1);

    /// <summary>
    /// 독 모니터를 다시 찾고(설정 변경·분리/재연결), 독 창·블러 창의 DPI 가 그 모니터와 다르면 물리 px 로 그 모니터로 옮긴다.
    /// 그래야 이후 Left/Top(그 모니터 기준 DIP)이 정확하다. 단일 모니터·같은 DPI 면 아무것도 안 함.
    /// </summary>
    private void RefreshMonitor()
    {
        var mon = _services.DesktopWindows.ResolveMonitor(_services.Settings.Current.Dock.Monitor);
        if (!string.Equals(mon.DeviceName, _monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            Log.Info($"독 모니터 변경 → {mon}");
        _monitor = mon;
        DockState.Monitor = mon.DeviceName;
        _services.DesktopWindows.EnsureOnMonitor(this, mon);
        _services.DesktopWindows.EnsureOnMonitor(_backdrop, mon);
    }

    /// <summary>슬라이드 진행도(_hide)에 따라 창 위치 갱신 + 블러 창 동기화.</summary>
    private void ApplySlidePosition()
    {
        var l = _layout;
        double dist = l.EdgeMargin + l.PanelCross + 6;
        var (vx, vy) = l.Edge switch
        {
            DockEdge.Left => (-1.0, 0.0),
            DockEdge.Bottom => (0.0, 1.0),
            DockEdge.Top => (0.0, -1.0),
            _ => (1.0, 0.0),
        };
        double k = _hide * dist; // _hide 는 이미 easing 적용된 진행도
        var r = _shownRect;
        Left = Math.Round(r.Left + vx * k);
        Top = Math.Round(r.Top + vy * k);
        if (Width != r.Width) Width = r.Width;
        if (Height != r.Height) Height = r.Height;
        // 위쪽 독은 상단바 아래로 미끄러져 들어가는 대신 흐려지며 사라짐
        Opacity = _windowsHidden ? 0 : l.Edge == DockEdge.Top ? 1 - _hide : 1;
        SyncBackdrop();
        UpdateLabelPosition();
    }

    /// <summary>블러 창을 패널 영역에 정확히 맞춤 (+ 상단바 샘플링용 공유 영역 갱신).</summary>
    private void SyncBackdrop()
    {
        if (_closed || PanelBorder.ActualWidth <= 0) return;
        var p = PanelBorder.TranslatePoint(new Point(0, 0), Root);
        var rect = new Rect(Left + p.X, Top + p.Y, PanelBorder.ActualWidth, PanelBorder.ActualHeight);
        // 아크릴은 창 Opacity 를 따르지 않으므로 페이드(위쪽 독) 중에는 블러 창을 아예 숨김
        bool wantBackdrop = _layout.Blur && !_windowsHidden && IsVisible
                            && !(_layout.Edge == DockEdge.Top && (_hide > 0.001 || _hideTo > 0));
        if (wantBackdrop)
        {
            _backdrop.SetRect(rect);
            if (!_backdrop.IsVisible)
            {
                _backdrop.Show();
                // 숨은 동안 다른 DPI 모니터로 못 옮겨졌으면 보인 뒤 맞춤 (단일 모니터는 아무것도 안 함)
                if (_services.DesktopWindows.EnsureOnMonitor(_backdrop, _monitor)) _backdrop.SetRect(rect);
            }
        }
        else if (_backdrop.IsVisible)
        {
            _backdrop.Hide();
        }
        DockState.VisiblePanel = _windowsHidden || _hide > 0.99 ? Rect.Empty : rect;
    }

    /// <summary>
    /// 코치마크(새로운 기능·둘러보기)가 가리킬 독 패널의 화면 사각형 (독 모니터 기준 DIP) + 그 모니터.
    /// 독이 꺼져 있거나 숨어 있으면(자동 숨김으로 들어가 있음·전체 화면·일시 정지) null.
    /// </summary>
    public (Rect Rect, MonitorInfo Monitor)? GetAnchorRect(CoachAnchor anchor)
    {
        if (anchor != CoachAnchor.Dock || _closed || !DockActive || _fullscreen || !IsVisible || _windowsHidden || _hide > 0.01) return null;
        if (PanelBorder.ActualWidth < 1 || PanelBorder.ActualHeight < 1) return null;
        try
        {
            var p = PanelBorder.TranslatePoint(new Point(0, 0), Root);
            return (new Rect(Left + p.X, Top + p.Y, PanelBorder.ActualWidth, PanelBorder.ActualHeight), _monitor);
        }
        catch (InvalidOperationException) { return null; }
    }

    // ───────────────────────── 모드 / 자동 숨김 ─────────────────────────

    private void ApplyMode(bool initial)
    {
        if (_closed || !IsLoaded) return;
        if (!DockActive)
        {
            Deactivate();
            return;
        }
        if (_fullscreen)
        {
            HideWindows();
            return;
        }

        if (_layout.Mode == DockMode.AutoHide)
        {
            if (initial)
            {
                // 시작 시에는 숨긴 상태
                StopSlide();
                _hide = _hideTo = 1;
                HideWindows(soft: true);
            }
            _lastInsideTicks = Environment.TickCount64;
            _pollTimer.Start();
        }
        else
        {
            _pollTimer.Stop();
            SetHidden(false, animate: !initial);
        }
    }

    /// <summary>
    /// 둘러보기 고정: 켜지면 자동 숨김 독을 바로(애니메이션 없이 — 말풍선이 곧바로 독을 가리켜야 함) 보이게,
    /// 풀리면 커서가 독 위가 아니면 평소처럼 미끄러져 숨음 (둘러보기의 "독은 평소엔 숨어 있어요" 카드에서 한 번 보여 줌).
    /// </summary>
    private void OnCoachPinChanged()
    {
        if (_closed || _fullscreen || !DockActive || _layout.Mode != DockMode.AutoHide) return;
        if (DockState.CoachPinned)
        {
            SetHidden(false, animate: false);
            _lastInsideTicks = Environment.TickCount64;
        }
        else
        {
            // 시연: 커서가 독 근처(바로 위의 [다음] 버튼)에 있어도 한 번은 숨김. 숨은 뒤엔 가장자리 2px 에 닿아야 다시 나타남
            SetHidden(true, animate: true);
        }
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (_closed || _fullscreen || _layout.Mode != DockMode.AutoHide) return;
        if (DockState.CoachPinned)
        {
            if (_hideTo >= 1) SetHidden(false, animate: false);
            _lastInsideTicks = Environment.TickCount64;
            return;
        }
        bool menuOpen = PanelBorder.ContextMenu?.IsOpen == true;
        if (_dragArmed || AnyItemDrag || menuOpen || _dialogOpen || _picker != null)
        {
            _lastInsideTicks = Environment.TickCount64;
            return;
        }

        // 배율·해상도가 바뀌었는데 DisplayChanged 가 늦거나 안 오면 옛 화면 크기로 트리거를 판정하게 됨
        // → 화면 크기를 확인해 달라졌으면 바로 다시 배치. 모니터 조회는 매 틱(60ms)이 아니라 약 0.5초마다
        if (++_pollCount % 8 == 0)
        try
        {
            var mon = _services.DesktopWindows.ResolveMonitor(_services.Settings.Current.Dock.Monitor);
            var screen = mon.Bounds;
            if (!screen.IsEmpty && (screen != _screen
                                    || !string.Equals(mon.DeviceName, _monitor.DeviceName, StringComparison.OrdinalIgnoreCase)))
                OnDisplayChanged(this, EventArgs.Empty);
        }
        catch { }

        Point? cursor;
        try { cursor = _services.DesktopWindows.GetCursorPosition(_monitor); }
        catch { return; }

        if (_hideTo >= 1)
        {
            // 독 모니터 밖(null)이면 트리거 판정 안 함
            if (cursor is Point c && InTriggerZone(c)) SetHidden(false, animate: true);
            return;
        }

        var inside = _shownRect;
        inside.Inflate(4, 4);
        // 터치를 뗀 뒤엔 커서가 독 위에 남아 있어도 바깥으로 봄 (진짜 마우스가 움직이면 다시 판정)
        if (!TouchReleasedAt(cursor) && ((cursor is Point p && inside.Contains(p)) || IsMouseOver))
            _lastInsideTicks = Environment.TickCount64;
        else if (Environment.TickCount64 - _lastInsideTicks > Math.Max(0, _services.Settings.Current.Dock.AutoHideDelayMs))
            SetHidden(true, animate: true);
    }

    /// <summary>커서가 독 가장자리 2px 이내 + 패널 길이 범위(±여유)에 있는지.</summary>
    private bool InTriggerZone(Point c)
    {
        const double edgeBand = 2, slack = 24;
        var s = _screen;
        var r = _shownRect;
        bool vertical = _layout.IsVertical;
        double center = vertical ? r.Top + r.Height / 2 : r.Left + r.Width / 2;
        double along = vertical ? c.Y : c.X;
        if (Math.Abs(along - center) > _baseLength / 2 + slack) return false;

        return _layout.Edge switch
        {
            DockEdge.Left => c.X <= s.Left + edgeBand,
            DockEdge.Bottom => c.Y >= s.Bottom - 1 - edgeBand,
            DockEdge.Top => c.Y <= s.Top + edgeBand,
            _ => c.X >= s.Right - 1 - edgeBand,
        };
    }

    private void SetHidden(bool hidden, bool animate)
    {
        double target = hidden ? 1 : 0;
        animate &= Anim.Enabled; // 시스템 애니메이션 끔·가벼운 모드면 미끄러지지 않고 바로
        if (!animate)
        {
            StopSlide();
            _hide = _hideTo = target;
            if (hidden) HideWindows(soft: true);
            else
            {
                ShowWindows();
                ApplySlidePosition();
            }
            return;
        }
        if (_hideTo == target && (_sliding || _hide == target)) return;

        if (!hidden)
        {
            _lastInsideTicks = Environment.TickCount64;
            ShowWindows();
        }
        else
        {
            _label?.Hide();
            ResetMagnification();
        }
        _hideFrom = _hide;
        _hideTo = target;
        SyncBackdrop(); // 위쪽 독 페이드 아웃: 블러 창을 먼저 숨김
        _slideClock.Restart();
        _sliding = true;
        HookRender();
    }

    private void StopSlide()
    {
        _sliding = false;
        _slideClock.Reset();
    }

    private void ShowWindows()
    {
        if (!DockActive) return;
        if (!_windowsHidden && IsVisible) return;
        _windowsHidden = false;
        Root.IsHitTestVisible = true;
        ApplySlidePosition();               // 투명도도 여기서 복원
        if (!IsVisible)
        {
            Show();
            // 숨은 동안 독 모니터가 바뀌어 DPI 가 안 맞으면 보인 뒤 그 모니터로 옮기고 다시 배치 (단일 모니터는 아무것도 안 함)
            if (_services.DesktopWindows.EnsureOnMonitor(this, _monitor)) ApplySlidePosition();
        }
        SyncBackdrop(); // 블러 창 표시 여부는 SyncBackdrop 에서 결정 (이 창이 보인 뒤)
    }

    /// <summary>
    /// 독 숨김. soft = 자동 숨김: 창은 띄워 둔 채 완전히 투명 + 클릭 통과
    /// (숨긴(Hide) 창은 WM_DPICHANGED 를 못 받아 배율이 바뀐 뒤 옛 배율로 배치되는 문제가 있었음).
    /// soft 가 아니면(전체 화면 앱·일시 정지·독 끄기) 실제로 Hide.
    /// </summary>
    private void HideWindows(bool soft = false)
    {
        CancelItemDrag();
        EndFileDrag();
        _windowsHidden = true;
        _label?.Hide();
        _picker?.Close();
        ResetMagnification();
        if (_backdrop.IsVisible) _backdrop.Hide();
        if (soft)
        {
            Root.IsHitTestVisible = false;
            Opacity = 0;
            if (!IsVisible && IsLoaded) Show();
        }
        else if (IsVisible)
        {
            Hide();
        }
        DockState.VisiblePanel = Rect.Empty;
    }

    // ───────────────────────── 렌더 루프 (슬라이드 + 확대) ─────────────────────────

    private void HookRender()
    {
        if (_renderHooked) return;
        _renderHooked = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void UnhookRender()
    {
        if (!_renderHooked) return;
        _renderHooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
        if (now == _lastFrame && _lastFrame != TimeSpan.Zero) return; // 같은 프레임 중복 호출
        _lastFrame = now;

        bool active = false;

        if (_sliding)
        {
            double t = Math.Min(1, _slideClock.Elapsed.TotalMilliseconds / SlideMs);
            double eased = 1 - Math.Pow(1 - t, 3); // ease-out, 바운스 없음
            _hide = _hideFrom + (_hideTo - _hideFrom) * eased;
            ApplySlidePosition();
            if (t >= 1)
            {
                _sliding = false;
                _hide = _hideTo;
                if (_hideTo >= 1) HideWindows(soft: true);
            }
            else active = true;
        }

        if (_magnifying)
        {
            _magnifying = StepMagnification(dt);
            active |= _magnifying;
        }

        if (!active) UnhookRender();
    }

    // ───────────────────────── 확대 (맥식 물결) ─────────────────────────

    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        if (HandleTouchMove(e)) return; // 터치 이동은 확대 안 함 (DockWindow.Touch.cs)
        if (_dragArmed || _itemDragging || _fileDragOver || _windowsHidden || _hideTo >= 1) return;
        var p = e.GetPosition(PanelBorder);
        double along = _layout.IsVertical ? p.Y - PanelBorder.ActualHeight / 2 : p.X - PanelBorder.ActualWidth / 2;

        // 패널 밖(확대 영역)에서 처음 들어온 건 무시: 확대는 패널에 들어온 뒤부터
        if (_cursorAlong == null && !PanelBorder.IsMouseOver) return;
        _cursorAlong = along;
        Root.Background = HitBrush; // 확대 중에는 튀어나온 아이콘 위도 독 영역으로
        _lastInsideTicks = Environment.TickCount64;
        _magnifying = true;
        HookRender();
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragArmed || _itemDragging) return;
        ResetMagnification(animate: true);
    }

    private void ResetMagnification(bool animate = false)
    {
        _cursorAlong = null;
        Root.Background = null;
        if (animate)
        {
            _magnifying = true;
            HookRender();
        }
        else
        {
            foreach (var v in _views.Values) v.ApplyScale(1);
        }
    }

    /// <summary>목표 배율로 부드럽게 수렴. 아직 움직이는 중이면 true.</summary>
    private bool StepMagnification(double dt)
    {
        var l = _layout;
        double k = 1 - Math.Exp(-dt * 20);
        bool moving = false;

        // 확대 전 기준 위치(패널 중심 기준)로 각 아이콘 중심 계산 → 확대에 따라 흔들리지 않음
        double pos = -(_baseLength - l.Padding * 2 - 2) / 2;
        foreach (UIElement child in ItemsHost.Children)
        {
            if (child is not DockItemView v) continue;
            double c = pos + v.BaseLength / 2;
            pos += v.BaseLength;
            if (v.Item.IsSeparator) continue;

            double target = 1;
            if (_cursorAlong is double cur && l.HoverScale > 1)
            {
                double d = Math.Abs(cur - c);
                if (l.Wave)
                {
                    double r = l.WaveRadius;
                    target = 1 + (l.HoverScale - 1) * Math.Max(0, Math.Cos(Math.PI * Math.Min(d, r) / (2 * r)));
                }
                else if (d <= v.BaseLength / 2)
                {
                    target = l.HoverScale;
                }
            }

            double next = v.Scale + (target - v.Scale) * k;
            if (Math.Abs(target - next) < 0.002) next = target;
            else moving = true;
            v.ApplyScale(next);
        }
        return moving;
    }

    // ───────────────────────── 항목 구성 ─────────────────────────

    private void RefreshItems(bool rebuildViews = false, bool place = true)
    {
        // 아이콘/파일 드래그 중에는 뷰를 다시 만들지 않고 끝난 뒤 반영 (ApplyAll 은 드래그를 먼저 취소하고 rebuild 로 옴)
        if (!rebuildViews && AnyItemDrag)
        {
            _refreshPending = true;
            return;
        }
        var settings = _services.Settings.Current;
        var windows = settings.Dock.ShowWindowsFromAllDesktops
            ? _services.Windows.Windows
            : _services.Windows.Windows.Where(w => w.OnCurrentDesktop).ToList();
        var style = settings.Dock.IconStyle;
        var old = rebuildViews ? new Dictionary<string, DockItemViewModel>() : _items.ToDictionary(i => i.Id);
        var list = new List<DockItemViewModel>();
        var matched = new HashSet<IntPtr>();

        // 1) 핀
        for (int i = 0; i < settings.Pins.Count; i++)
        {
            var pin = settings.Pins[i];
            if (pin.Kind == PinKind.Separator)
            {
                string sid = $"sep:{i}";
                list.Add(old.GetValueOrDefault(sid) is { Pin: var p } existing && ReferenceEquals(p, pin)
                    ? existing
                    : new DockItemViewModel(sid, pin, isSeparator: true, "", null));
                continue;
            }

            var pinWindows = windows.Where(w => SafeMatches(pin, w)).ToList();
            foreach (var w in pinWindows) matched.Add(w.Hwnd);

            string id = $"pin:{i}:{pin.Kind}:{pin.Target}:{pin.IconPath}";
            var vm = old.GetValueOrDefault(id);
            if (vm == null || !ReferenceEquals(vm.Pin, pin))
                vm = new DockItemViewModel(id, pin, false, PinDisplayName(pin), SafeIcon(() => _services.Icons.GetIcon(pin, style)));
            vm.Windows = pinWindows;
            list.Add(vm);
        }

        // 2) 핀에 없는 실행 중 앱 (같은 앱의 여러 창은 하나로)
        if (settings.Dock.ShowRunningApps)
        {
            var groups = new Dictionary<string, List<AppWindowInfo>>();
            foreach (var w in windows)
            {
                if (matched.Contains(w.Hwnd)) continue;
                string key = SafeAppKey(w);
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new List<AppWindowInfo>();
                g.Add(w);
            }

            _runningOrder.RemoveAll(k => !groups.ContainsKey(k));
            foreach (var k in groups.Keys)
                if (!_runningOrder.Contains(k)) _runningOrder.Add(k);

            if (_runningOrder.Count > 0)
            {
                const string sepId = "sep:running";
                list.Add(old.GetValueOrDefault(sepId) ?? new DockItemViewModel(sepId, null, isSeparator: true, "", null));
                foreach (var key in _runningOrder)
                {
                    var wins = groups[key];
                    string id = "app:" + key;
                    var vm = old.GetValueOrDefault(id)
                             ?? new DockItemViewModel(id, null, false, AppNames.Get(wins[0]), SafeIcon(() => _services.Icons.GetIcon(wins[0], style)));
                    vm.Windows = wins;
                    list.Add(vm);
                }
            }
        }
        else
        {
            _runningOrder.Clear();
        }

        // 사라진 창 정리
        var alive = new HashSet<IntPtr>(windows.Select(w => w.Hwnd));
        _flashed.RemoveWhere(h => !alive.Contains(h));
        foreach (var h in _lastActive.Keys.Where(h => !alive.Contains(h)).ToList()) _lastActive.Remove(h);

        bool structureChanged = rebuildViews || !list.Select(i => i.Id).SequenceEqual(_items.Select(i => i.Id));
        _items = list;
        UpdateStates();
        if (structureChanged)
        {
            RebuildViews(rebuildViews);
            if (place) Place();
        }
    }

    private void UpdateStates()
    {
        foreach (var item in _items)
        {
            if (item.IsSeparator) continue;
            item.IsRunning = item.Windows.Count > 0;
            item.RunningElsewhereOnly = item.IsRunning && item.Windows.All(w => !w.OnCurrentDesktop);
            item.HasNotification = item.Windows.Any(w => _flashed.Contains(w.Hwnd));
            UpdateLaunching(item);
        }
    }

    private void RebuildViews(bool recreateAll)
    {
        _label?.Hide();
        if (recreateAll)
        {
            foreach (var v in _views.Values) v.Detach();
            _views.Clear();
        }

        ItemsHost.Children.Clear();
        var keep = new HashSet<string>();
        foreach (var item in _items)
        {
            keep.Add(item.Id);
            if (!_views.TryGetValue(item.Id, out var view) || !ReferenceEquals(view.Item, item))
            {
                view?.Detach();
                view = new DockItemView(item, _layout);
                view.HoverStarted += OnItemHoverStarted;
                view.HoverEnded += OnItemHoverEnded;
                view.Pressed += OnItemPressed;
                _views[item.Id] = view;
            }
            ItemsHost.Children.Add(view);
        }
        foreach (var id in _views.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _views[id].Detach();
            _views.Remove(id);
        }
    }

    private static string PinDisplayName(PinItem pin)
    {
        if (!string.IsNullOrWhiteSpace(pin.Name)) return pin.Name;
        if (pin.Kind == PinKind.Exe) return System.IO.Path.GetFileNameWithoutExtension(pin.Target);
        return pin.Target;
    }

    /// <summary>실행할 대상이 있는 핀인지 (Target 이 빈 Exe/Aumid 핀은 실행·창 매칭 모두 안 함).</summary>
    internal static bool CanLaunch(PinItem pin)
        => pin.Kind != PinKind.Separator && !string.IsNullOrWhiteSpace(pin.Target);

    /// <summary>창으로 핀을 만들 수 있는지 (관리자 권한 창 등 경로를 못 읽은 창은 불가).</summary>
    internal static bool CanPin(AppWindowInfo w)
        => !string.IsNullOrWhiteSpace(w.ProcessPath) || !string.IsNullOrWhiteSpace(w.Aumid);

    private bool SafeMatches(PinItem pin, AppWindowInfo w)
    {
        // 빈 Target 핀이 경로를 못 읽은 창("")과 우연히 같다고 판정되지 않게
        if (!CanLaunch(pin) || !CanPin(w)) return false;
        try { return _services.Windows.Matches(pin, w); }
        catch { return false; }
    }

    private string SafeAppKey(AppWindowInfo w)
    {
        try { return _services.Windows.GetAppKey(w); }
        catch { return w.ProcessPath.ToLowerInvariant(); }
    }

    private static ImageSource? SafeIcon(Func<ImageSource> get)
    {
        try { return get(); }
        catch (Exception ex)
        {
            Log.Error("독 아이콘 로드 실패", ex);
            return null;
        }
    }

    // ───────────────────────── 클릭 ─────────────────────────

    private void OnItemClicked(object? sender, EventArgs e)
    {
        if (sender is not DockItemView { Item: var item } || item.IsSeparator) return;
        _label?.Hide();
        ClearNotification(item);
        UpdateStates();

        try
        {
            if (item.Windows.Count == 0)
            {
                // 창이 하나도 없을 때만, 그리고 실행 대상이 있는 핀만 실행 (빈 경로 Launch 금지)
                if (item.Pin != null && CanLaunch(item.Pin)) LaunchFromDock(item, item.Pin);
                else if (item.Pin != null) Log.Warn($"실행 대상이 비어 있는 핀 '{item.Pin.Name}' — 실행 안 함");
                return;
            }

            if (item.Windows.Count > 1 && _services.Settings.Current.Dock.MultiWindowClick == MultiWindowClick.Picker)
            {
                ShowWindowPicker(sender as DockItemView, item);
                return;
            }

            var fg = _services.Windows.ForegroundWindow;
            if (item.Windows.Count > 1 && item.Windows.Any(w => w.Hwnd == fg))
            {
                // 이미 이 앱이 앞에 있고 창이 여러 개 → 다음 창으로 순환 (hwnd 기준 고정 순서)
                var cycle = item.Windows.OrderBy(w => (long)w.Hwnd).ToList();
                int idx = cycle.FindIndex(w => w.Hwnd == fg);
                _services.Launcher.Activate(cycle[(idx + 1) % cycle.Count].Hwnd);
                return;
            }

            // 가장 최근에 활성화된 창 (기록 없으면 목록 순서 = 보통 z-order 위쪽)
            var target = item.Windows
                .Select((w, i) => (w, i))
                .OrderByDescending(x => _lastActive.GetValueOrDefault(x.w.Hwnd))
                .ThenBy(x => x.i)
                .First().w;
            _services.Launcher.ToggleActivate(target.Hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("독 아이콘 클릭 처리 실패", ex);
        }
    }

    // ───────────────────────── 창 선택 패널 ─────────────────────────

    private WindowPickerWindow? _picker;
    private DockItemViewModel? _pickerItem;

    private void ShowWindowPicker(DockItemView? view, DockItemViewModel item)
    {
        // 같은 아이콘을 다시 누르면 닫기 (토글)
        bool same = _pickerItem == item && _picker != null;
        _picker?.Close();
        if (same || view == null) return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var a = toDip.Transform(view.PointToScreen(new Point(0, 0)));
        var b = toDip.Transform(view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight)));

        // 현재 데스크톱 창 먼저, 그다음 데스크톱 번호 순
        var windows = item.Windows.OrderByDescending(w => w.OnCurrentDesktop).ThenBy(w => w.DesktopIndex).ToList();
        var picker = new WindowPickerWindow(_services, UiTheme.Palette(_services.Settings.Current), windows,
            item.Icon, new Rect(a, b), _layout.Edge, _monitor);
        picker.Closed += (_, _) =>
        {
            if (_picker != picker) return;
            _picker = null;
            _pickerItem = null;
            _lastInsideTicks = Environment.TickCount64;
        };
        _picker = picker;
        _pickerItem = item;
        picker.Show();
    }

    private void ClearNotification(DockItemViewModel item)
    {
        foreach (var w in item.Windows) _flashed.Remove(w.Hwnd);
    }

    // ───────────────────────── 이름 말풍선 ─────────────────────────

    private DockItemView? _labelView;
    private const double LabelGap = 10;

    private void OnItemHoverStarted(object? sender, EventArgs e)
    {
        if (sender is not DockItemView view || string.IsNullOrEmpty(view.Item.Name)) return;
        if (PanelBorder.ContextMenu?.IsOpen == true || _dragArmed || _itemDragging || _fileDragOver || _hideTo >= 1) return;
        if (LabelAnchor(view) is not Point anchor) return;

        // 숨은 말풍선은 다른 DPI 모니터로 옮겨도 DPI 가 안 바뀔 수 있어, DPI 가 다르면 새로 만든다 (새 창은 WPF 가 그 모니터 DPI 로 만듦)
        if (_label != null && !MatchesMonitorDpi(_label))
        {
            _label.Close();
            _label = null;
        }
        if (_label == null)
        {
            _label = new DockLabelWindow(_services);
            _label.SetColors(_layout.LabelBackground, _layout.LabelForeground, _layout.LabelBorder);
        }
        _labelView = view;
        _label.ShowAt(view.Item.Name, anchor, _layout.Edge);
        // 배율이 다른 모니터끼리 DIP 가 겹쳐 엉뚱한 모니터에 만들어졌으면 옮긴 뒤 다시 (단일 모니터는 아무것도 안 함)
        if (_services.DesktopWindows.EnsureOnMonitor(_label, _monitor)) _label.MoveTo(anchor);
    }

    /// <summary>창의 DPI 가 독 모니터 배율과 같은지 (핸들이 아직 없으면 true — 만들 때 WPF 가 맞춤).</summary>
    private bool MatchesMonitorDpi(Window w)
    {
        if (new System.Windows.Interop.WindowInteropHelper(w).Handle == IntPtr.Zero) return true;
        return Math.Abs(VisualTreeHelper.GetDpi(w).DpiScaleX - _monitor.Scale) < 0.001;
    }

    private void OnItemHoverEnded(object? sender, EventArgs e)
    {
        if (sender == _labelView) _labelView = null;
        _label?.Hide();
    }

    /// <summary>
    /// 말풍선 기준점(화면 DIP): 확대된 아이콘의 안쪽 끝 + 간격.
    /// 아이콘은 가장자리 쪽 변을 기준으로 IconSize×Scale 만큼 안쪽으로 커지므로 그 끝을 계산한다.
    /// </summary>
    private Point? LabelAnchor(DockItemView view)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null || !view.IsVisible) return null;

        double extent = _layout.IconSize * view.Scale + LabelGap;
        double w = view.ActualWidth, h = view.ActualHeight;
        Point local = _layout.Edge switch
        {
            DockEdge.Left => view.TranslatePoint(new Point(0, h / 2), this) + new Vector(extent, 0),
            DockEdge.Bottom => view.TranslatePoint(new Point(w / 2, h), this) - new Vector(0, extent),
            DockEdge.Top => view.TranslatePoint(new Point(w / 2, 0), this) + new Vector(0, extent),
            _ => view.TranslatePoint(new Point(w, h / 2), this) - new Vector(extent, 0),
        };
        return source.CompositionTarget.TransformFromDevice.Transform(PointToScreen(local));
    }

    /// <summary>확대/슬라이드 중 말풍선이 아이콘을 따라가게.</summary>
    private void UpdateLabelPosition()
    {
        if (_label == null || _labelView == null || !_label.IsVisible) return;
        if (LabelAnchor(_labelView) is Point anchor) _label.MoveTo(anchor);
    }

    // ───────────────────────── 드래그로 독 이동 ─────────────────────────
    // 빈 영역·패딩·"실행 중 앱" 앞 자동 구분선을 끌면 독 자체를 옮긴다.
    // 아이콘·핀 구분선을 끌면 순서 바꾸기 (DockWindow.ItemDrag.cs) — 누른 대상이 달라 서로 겹치지 않는다.

    private const double DragThreshold = 6;
    private bool _dragArmed;
    private bool _dragging;
    private Point _dragStart;          // 화면 DIP
    private double _dragGrabDelta;     // 커서와 패널 중심의 독 방향 거리 (같은 방향 가장자리면 유지)
    private DockEdge _dragEdge;
    private double _dragOffset;

    private Point ToScreenDip(Point local)
    {
        var source = PresentationSource.FromVisual(this);
        var p = PointToScreen(local);
        return source?.CompositionTarget?.TransformFromDevice.Transform(p) ?? p;
    }

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 아이콘·핀 구분선은 자체 처리(e.Handled, 순서 바꾸기 드래그) → 여기 오는 건 빈 영역/패딩/자동 구분선
        _dragStart = ToScreenDip(e.GetPosition(this));
        var panelCenter = ToScreenDip(PanelBorder.TranslatePoint(
            new Point(PanelBorder.ActualWidth / 2, PanelBorder.ActualHeight / 2), this));
        _dragGrabDelta = _layout.IsVertical ? _dragStart.Y - panelCenter.Y : _dragStart.X - panelCenter.X;
        _dragging = false;
        // 버튼을 이 창 위에서 누른 상태라 NOACTIVATE 창이어도 캡처가 창 밖까지 유지된다 (실측 확인)
        _dragArmed = PanelBorder.CaptureMouse();
        e.Handled = true;
    }

    private void OnPanelMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelDrag();
            return;
        }

        var p = ToScreenDip(e.GetPosition(this));
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) < DragThreshold && Math.Abs(p.Y - _dragStart.Y) < DragThreshold) return;
            _dragging = true;
            _label?.Hide();
            ResetMagnification();
        }

        ComputeDragTarget(p, out _dragEdge, out _dragOffset);
        if (_ghost != null && !_ghost.IsVisible && !MatchesMonitorDpi(_ghost))
        {
            _ghost.Close();
            _ghost = null;
        }
        _ghost ??= new DockGhostWindow(_services);
        var ghostRect = GhostRect(_dragEdge, _dragOffset);
        _ghost.ShowAt(ghostRect, _layout);
        if (_services.DesktopWindows.EnsureOnMonitor(_ghost, _monitor)) _ghost.ShowAt(ghostRect, _layout);
    }

    private void OnPanelMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragArmed) return;
        bool commit = _dragging;
        var edge = _dragEdge;
        double offset = _dragOffset;
        CancelDrag();
        e.Handled = true;
        if (!commit) return;

        var dock = _services.Settings.Current.Dock;
        if (dock.Edge == edge && Math.Abs(dock.Offset - offset) < 0.0005) return;
        dock.Edge = edge;
        dock.Offset = Math.Round(offset, 4);
        // 저장 → SettingsChanged → ApplyAll 에서 공간 예약을 놓을 때 한 번만 갱신
        _services.Settings.Save();
    }

    private void OnPanelLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragArmed) CancelDrag();
    }

    private void CancelDrag()
    {
        bool wasArmed = _dragArmed;
        _dragArmed = false;
        _dragging = false;
        _ghost?.Hide();
        if (wasArmed && PanelBorder.IsMouseCaptured) PanelBorder.ReleaseMouseCapture();
    }

    /// <summary>커서에서 가장 가까운 화면 가장자리 + 그 가장자리를 따라 패널 중심 위치(0~1).</summary>
    private void ComputeDragTarget(Point p, out DockEdge edge, out double offset)
    {
        var s = _screen;
        double dl = p.X - s.Left, dr = s.Right - p.X, dt = p.Y - s.Top, db = s.Bottom - p.Y;
        double min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        edge = min == dr ? DockEdge.Right : min == dl ? DockEdge.Left : min == db ? DockEdge.Bottom : DockEdge.Top;

        bool vertical = edge is DockEdge.Left or DockEdge.Right;
        double delta = vertical == _layout.IsVertical ? _dragGrabDelta : 0;
        offset = vertical
            ? (p.Y - delta - s.Top) / Math.Max(1, s.Height)
            : (p.X - delta - s.Left) / Math.Max(1, s.Width);
        offset = ClampOffset(offset);
    }

    /// <summary>놓았을 때 패널이 차지할 화면 영역(DIP).</summary>
    private Rect GhostRect(DockEdge edge, double offset)
    {
        var l = _layout;
        GetFrame(edge, true, out double edgeLine, out double start, out double end);
        double len = _baseLength;
        double c = PanelCenter(edge, offset, len, start, end);
        double cross = l.PanelCross, m = l.EdgeMargin;
        return edge switch
        {
            DockEdge.Left => new Rect(edgeLine + m, c - len / 2, cross, len),
            DockEdge.Bottom => new Rect(c - len / 2, edgeLine - m - cross, len, cross),
            DockEdge.Top => new Rect(c - len / 2, edgeLine + m, len, cross),
            _ => new Rect(edgeLine - m - cross, c - len / 2, cross, len),
        };
    }

    // ───────────────────────── 오른쪽 클릭 메뉴 ─────────────────────────

    private void OnPanelContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _label?.Hide();
        // 아이콘 드래그를 오른쪽 클릭으로 취소한 직후면 메뉴를 띄우지 않음
        if (_itemArmed || Environment.TickCount64 < _suppressContextUntil)
        {
            e.Handled = true;
            return;
        }
        var menu = PanelBorder.ContextMenu!;
        menu.Items.Clear();

        var view = FindItemView(e.OriginalSource as DependencyObject);
        if (view == null || view.Item.IsAutoSeparator) BuildEmptyAreaMenu(menu);
        else if (view.Item.Pin != null) BuildPinMenu(menu, view.Item);
        else BuildRunningMenu(menu, view.Item);

        if (menu.Items.Count == 0) e.Handled = true;
    }

    private static DockItemView? FindItemView(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is DockItemView v) return v;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void BuildPinMenu(ContextMenu menu, DockItemViewModel item)
    {
        var pin = item.Pin!;
        var pins = _services.Settings.Current.Pins;
        int index = pins.IndexOf(pin);

        if (!item.IsSeparator)
        {
            AddWindowList(menu, item);
            AddNewWindowItem(menu, pin);
            if (item.IsRunning)
                menu.Items.Add(Item("창 닫기", () => CloseAll(item)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("아이콘 변경…", () => ChangeIcon(pin)));
            menu.Items.Add(DockMenus.Item("기본 아이콘으로", () => ModifyPins(_ => pin.IconPath = null), enabled: pin.IconPath != null));
            menu.Items.Add(new Separator());
        }

        bool vertical = _layout.IsVertical;
        menu.Items.Add(DockMenus.Item(vertical ? "위로 이동" : "왼쪽으로 이동", () => MovePin(pin, -1), enabled: index > 0));
        menu.Items.Add(DockMenus.Item(vertical ? "아래로 이동" : "오른쪽으로 이동", () => MovePin(pin, +1),
            enabled: index >= 0 && index < pins.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(item.IsSeparator ? "구분선 제거" : "독에서 제거", () => ModifyPins(p => p.Remove(pin))));
    }

    /// <summary>맥 독처럼 메뉴 맨 위에 그 앱의 창 목록 (제목 + 다른 데스크톱이면 "— 데스크톱 N", 활성 창 ✓).</summary>
    private void AddWindowList(ContextMenu menu, DockItemViewModel item)
    {
        if (item.Windows.Count == 0) return;
        var fg = _services.Windows.ForegroundWindow;
        foreach (var w in item.Windows.OrderByDescending(w => w.OnCurrentDesktop).ThenBy(w => w.DesktopIndex))
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            string title = string.IsNullOrWhiteSpace(w.Title) ? item.Name : w.Title;
            header.Children.Add(new TextBlock { Text = title.Length > 48 ? title[..47] + "…" : title });
            if (!w.OnCurrentDesktop)
            {
                header.Children.Add(new TextBlock
                {
                    Text = w.DesktopIndex > 0 ? $"  — 데스크톱 {w.DesktopIndex}" : "  — 다른 데스크톱",
                    Opacity = 0.55,
                });
            }
            var hwnd = w.Hwnd;
            var mi = new MenuItem { Header = header, IsChecked = hwnd == fg };
            mi.Click += (_, _) =>
            {
                try { _services.Launcher.Activate(hwnd); }
                catch (Exception ex) { Log.Error("창 전환 실패", ex); }
            };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
    }

    /// <summary>"새 창 열기" — 프로필이 있는 앱(크롬 등)은 "새 창 ›" 하위 메뉴에 프로필 목록.</summary>
    private void AddNewWindowItem(ContextMenu menu, PinItem pin)
    {
        if (!CanLaunch(pin)) return; // 실행 경로를 모르면 "새 창" 없음
        IReadOnlyList<AppProfile> profiles;
        try { profiles = _services.Launcher.GetProfiles(pin); }
        catch (Exception ex)
        {
            Log.Error("프로필 목록 조회 실패", ex);
            profiles = Array.Empty<AppProfile>();
        }

        if (profiles.Count == 0)
        {
            menu.Items.Add(Item("새 창 열기", () => _services.Launcher.Launch(pin)));
            return;
        }

        var parent = new MenuItem { Header = "새 창" };
        foreach (var profile in profiles)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = profile.Avatar != null
                    ? new ImageBrush(profile.Avatar) { Stretch = Stretch.UniformToFill }
                    : BrushParser.Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            });
            header.Children.Add(new TextBlock { Text = profile.Name, VerticalAlignment = VerticalAlignment.Center });
            var p = profile;
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                try { _services.Launcher.LaunchProfile(pin, p); }
                catch (Exception ex) { Log.Error("프로필 새 창 실패", ex); }
            };
            parent.Items.Add(mi);
        }
        menu.Items.Add(parent);
    }

    private void BuildRunningMenu(ContextMenu menu, DockItemViewModel item)
    {
        AddWindowList(menu, item);
        // 관리자 권한 창처럼 경로를 못 읽은 앱은 "새 창"·"독에 고정" 을 숨김 (빈 경로 핀/실행 방지)
        bool pinnable = item.Windows.Count > 0 && CanPin(item.Windows[0]);
        if (pinnable)
        {
            PinItem? tempPin = null;
            try { tempPin = _services.Windows.CreatePin(item.Windows[0]); }
            catch (Exception ex) { Log.Error("임시 핀 생성 실패", ex); }
            if (tempPin != null) AddNewWindowItem(menu, tempPin);
        }
        if (pinnable)
        {
            menu.Items.Add(Item("독에 고정", () =>
            {
                if (item.Windows.Count == 0) return;
                var newPin = _services.Windows.CreatePin(item.Windows[0]);
                ModifyPins(p => p.Add(newPin));
            }));
        }
        if (item.IsRunning)
            menu.Items.Add(Item("창 닫기", () => CloseAll(item)));
    }

    private void BuildEmptyAreaMenu(ContextMenu menu)
    {
        var dock = _services.Settings.Current.Dock;
        menu.Items.Add(DockMenus.SettingsWindow(_services, $"{AppInfo.Name} 설정…"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("구분선 추가", () => ModifyPins(p => p.Add(new PinItem { Kind = PinKind.Separator, Name = "" }))));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.DockPosition(_services));
        menu.Items.Add(DockMenus.DockBehavior(_services));
        menu.Items.Add(DockMenus.DockThemeMenu(_services));
        menu.Items.Add(DockMenus.Item("가운데로 정렬", () =>
        {
            _services.Settings.Current.Dock.Offset = 0.5;
            _services.Settings.Save();
        }, enabled: Math.Abs(dock.Offset - 0.5) > 0.0005));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.HideDock(_services));
        menu.Items.Add(DockMenus.Pause());
        menu.Items.Add(DockMenus.HideTaskbar(_services));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.StartWithWindows(_services));
        menu.Items.Add(DockMenus.OpenSettings(_services));
        menu.Items.Add(DockMenus.Quit());
    }

    private static MenuItem Item(string header, Action action) => DockMenus.Item(header, action);

    // ───────────────────────── 메뉴 동작 ─────────────────────────

    private void CloseAll(DockItemViewModel item)
        => ConfirmCardWindow.CloseWindows(_services, item.Name, item.Windows.ToList());

    private void MovePin(PinItem pin, int delta)
    {
        ModifyPins(pins =>
        {
            int i = pins.IndexOf(pin);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= pins.Count) return;
            (pins[i], pins[j]) = (pins[j], pins[i]);
        });
    }

    private void ChangeIcon(PinItem pin)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"{PinDisplayName(pin)} 아이콘 선택",
            Filter = "아이콘 이미지 (*.png;*.ico)|*.png;*.ico|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
        };
        bool ok;
        _dialogOpen = true; // 대화상자가 떠 있는 동안 자동 숨김 안 함
        try { ok = dlg.ShowDialog() == true; }
        finally
        {
            _dialogOpen = false;
            _lastInsideTicks = Environment.TickCount64;
        }
        if (!ok) return;
        string copied = _services.Settings.ImportIcon(dlg.FileName);
        ModifyPins(_ => pin.IconPath = copied);
    }

    /// <summary>핀 목록 수정 → 저장. Save 가 SettingsChanged 를 올려 ApplyAll 로 다시 그려진다.</summary>
    private void ModifyPins(Action<List<PinItem>> change)
    {
        change(_services.Settings.Current.Pins);
        _services.Settings.Save();
    }
}
