using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MyDock.Models;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 맥 스타일 독. 창은 화면 한쪽 가장자리 전체 길이, 실제 패널은 가운데에 가장자리 쪽으로 붙어 있고
/// 나머지는 투명(클릭 통과). 포커스를 뺏지 않는다(MakeOverlay = WS_EX_NOACTIVATE).
/// </summary>
public partial class DockWindow : Window
{
    private readonly AppServices _services;
    private DockLayout _layout;

    // AppBar 등록 상태 (설정 변경 시 재등록 판단용)
    private bool _appBarRegistered;
    private DockEdge _registeredEdge;
    private double _registeredThickness;

    private List<DockItemViewModel> _items = new();
    private readonly Dictionary<string, DockItemView> _views = new();
    /// <summary>알림(깜빡임) 받은 창들. 항목의 알림 점 = 그 항목 창 중 하나라도 여기 있으면.</summary>
    private readonly HashSet<IntPtr> _flashed = new();
    /// <summary>창별 마지막 활성화 순번 (클릭 시 "가장 최근 창" 선택용).</summary>
    private readonly Dictionary<IntPtr, long> _lastActive = new();
    private long _activationCounter;
    /// <summary>핀 아닌 실행 중 앱의 표시 순서 (z-order 로 순서가 흔들리지 않게 처음 본 순서 유지).</summary>
    private readonly List<string> _runningOrder = new();

    private DockLabelWindow? _label;
    private bool _subscribed;

    public DockWindow(AppServices services)
    {
        _services = services;
        _layout = DockLayout.From(services.Settings.Current.Dock);
        InitializeComponent();

        PanelBorder.ContextMenu = new ContextMenu();
        PanelBorder.ContextMenuOpening += OnPanelContextMenuOpening;

        // 패널 길이(호버 확대 포함)나 창 크기/위치가 바뀌면 Offset 위치 다시 계산
        PanelBorder.SizeChanged += (_, _) => UpdatePanelPosition();
        Root.SizeChanged += (_, _) => UpdatePanelPosition();
        LocationChanged += (_, _) => UpdatePanelPosition();

        // 빈 영역/구분선 드래그 → 독 이동
        PanelBorder.MouseLeftButtonDown += OnPanelMouseDown;
        PanelBorder.MouseMove += OnPanelMouseMove;
        PanelBorder.MouseLeftButtonUp += OnPanelMouseUp;
        PanelBorder.LostMouseCapture += OnPanelLostCapture;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    // ───────────────────────── 수명 주기 ─────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _services.DesktopWindows.MakeOverlay(this);
        ApplyLayout();
        ApplyPlacement();

        _services.Windows.WindowsChanged += OnWindowsChanged;
        _services.Windows.WindowFlashed += OnWindowFlashed;
        _services.Windows.WindowActivated += OnWindowActivated;
        _services.Settings.SettingsChanged += OnSettingsChanged;
        _subscribed = true;

        RefreshItems();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_subscribed)
        {
            _services.Windows.WindowsChanged -= OnWindowsChanged;
            _services.Windows.WindowFlashed -= OnWindowFlashed;
            _services.Windows.WindowActivated -= OnWindowActivated;
            _services.Settings.SettingsChanged -= OnSettingsChanged;
            _subscribed = false;
        }
        if (_appBarRegistered)
        {
            _services.DesktopWindows.UnregisterAppBar(this);
            _appBarRegistered = false;
        }
        _label?.Close();
        _label = null;
        _ghost?.Close();
        _ghost = null;
        foreach (var v in _views.Values) v.Detach();
        _views.Clear();
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
        // 그 앱의 알림 점 제거 (같은 앱의 다른 창 알림도 함께)
        var item = _items.FirstOrDefault(i => i.Windows.Any(w => w.Hwnd == hwnd));
        if (item != null) ClearNotification(item);
        else _flashed.Remove(hwnd);
        UpdateStates();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ReapplySettings();

    /// <summary>설정 전체 재적용: 레이아웃/색/크기/위치 + 항목 다시 만들기.</summary>
    private void ReapplySettings()
    {
        CancelDrag();
        _layout = DockLayout.From(_services.Settings.Current.Dock);
        _label?.Hide();
        ApplyLayout();
        ApplyPlacement();
        RefreshItems(rebuildViews: true);
    }

    // ───────────────────────── 레이아웃 / 위치 ─────────────────────────

    private void ApplyLayout()
    {
        var l = _layout;
        PanelBorder.Background = l.Background;
        PanelBorder.BorderBrush = l.Border;
        PanelBorder.CornerRadius = new CornerRadius(l.CornerRadius);
        PanelBorder.Padding = new Thickness(l.Padding);
        ItemsHost.Orientation = l.IsVertical ? Orientation.Vertical : Orientation.Horizontal;

        // 가장자리 쪽 정렬 + 독 방향은 시작 정렬 (실제 위치는 UpdatePanelPosition 이 Margin 으로 결정)
        switch (l.Edge)
        {
            case DockEdge.Left:
                PanelBorder.HorizontalAlignment = HorizontalAlignment.Left;
                PanelBorder.VerticalAlignment = VerticalAlignment.Top;
                break;
            case DockEdge.Bottom:
                PanelBorder.HorizontalAlignment = HorizontalAlignment.Left;
                PanelBorder.VerticalAlignment = VerticalAlignment.Bottom;
                break;
            case DockEdge.Top:
                PanelBorder.HorizontalAlignment = HorizontalAlignment.Left;
                PanelBorder.VerticalAlignment = VerticalAlignment.Top;
                break;
            default:
                PanelBorder.HorizontalAlignment = HorizontalAlignment.Right;
                PanelBorder.VerticalAlignment = VerticalAlignment.Top;
                break;
        }
        UpdatePanelPosition();

        _label?.SetColors(l.Background, l.Border);
    }

    private Rect _screen = new(0, 0, 1920, 1080);

    /// <summary>
    /// Offset(0~1, 화면 전체 길이 기준)에 맞춰 패널을 가장자리를 따라 배치하고, 창 밖으로 안 나가게 클램프.
    /// 화면 기준으로 계산하므로 드래그 고스트 위치와 실제 위치가 일치한다.
    /// </summary>
    private void UpdatePanelPosition()
    {
        var l = _layout;
        double windowLen = l.IsVertical ? Root.ActualHeight : Root.ActualWidth;
        double panelLen = l.IsVertical ? PanelBorder.ActualHeight : PanelBorder.ActualWidth;
        if (windowLen <= 0 || PresentationSource.FromVisual(this) == null) return;

        double offset = ClampOffset(_services.Settings.Current.Dock.Offset);
        var origin = ToScreenDip(new Point(0, 0)); // 창 시작점의 화면 좌표(DIP)
        double windowStart = l.IsVertical ? origin.Y : origin.X;
        double screenCenter = l.IsVertical
            ? _screen.Top + offset * _screen.Height
            : _screen.Left + offset * _screen.Width;
        double start = screenCenter - windowStart - panelLen / 2;
        start = Math.Round(Math.Clamp(start, 0, Math.Max(0, windowLen - panelLen)));

        double m = l.EdgeMargin;
        var margin = l.Edge switch
        {
            DockEdge.Left => new Thickness(m, start, 0, 0),
            DockEdge.Bottom => new Thickness(start, 0, 0, m),
            DockEdge.Top => new Thickness(start, m, 0, 0),
            _ => new Thickness(0, start, m, 0),
        };
        if (PanelBorder.Margin != margin) PanelBorder.Margin = margin;
    }

    private static double ClampOffset(double v) => double.IsNaN(v) ? 0.5 : Math.Clamp(v, 0, 1);

    private void ApplyPlacement()
    {
        var dock = _services.Settings.Current.Dock;
        double t = _layout.Thickness;
        _screen = _services.DesktopWindows.GetPrimaryScreenBounds();

        if (_appBarRegistered && (!dock.ReserveSpace || _registeredEdge != _layout.Edge || _registeredThickness != t))
        {
            _services.DesktopWindows.UnregisterAppBar(this);
            _appBarRegistered = false;
        }

        if (dock.ReserveSpace)
        {
            if (!_appBarRegistered)
            {
                _services.DesktopWindows.RegisterAppBar(this, _layout.Edge, t);
                _appBarRegistered = true;
                _registeredEdge = _layout.Edge;
                _registeredThickness = t;
            }
        }
        else
        {
            // AppBar 없이 작업 영역 가장자리에 직접 배치
            var a = GetPlacementArea();
            switch (_layout.Edge)
            {
                case DockEdge.Left:
                    SetBounds(a.Left, a.Top, t, a.Height);
                    break;
                case DockEdge.Bottom:
                    SetBounds(a.Left, a.Bottom - t, a.Width, t);
                    break;
                case DockEdge.Top:
                    SetBounds(a.Left, a.Top, a.Width, t);
                    break;
                default:
                    SetBounds(a.Right - t, a.Top, t, a.Height);
                    break;
            }
        }
        UpdatePanelPosition();
    }

    /// <summary>
    /// AppBar 없이 놓을 때 쓰는 영역: 작업 영역(작업표시줄/다른 AppBar 제외).
    /// 상단바가 공간 예약을 안 하면 상단바 높이만큼 내린다.
    /// </summary>
    private Rect GetPlacementArea()
    {
        var s = _services.DesktopWindows.GetPrimaryScreenBounds();
        Rect a;
        try { a = _services.DesktopWindows.GetPrimaryWorkArea(); }
        catch { a = s; }
        if (a.IsEmpty || a.Width <= 0 || a.Height <= 0) a = s;

        var top = _services.Settings.Current.TopBar;
        if (top.Enabled && !top.ReserveSpace)
        {
            double minTop = s.Top + Math.Max(0, top.Height);
            if (a.Top < minTop) a = new Rect(a.Left, minTop, a.Width, Math.Max(0, a.Bottom - minTop));
        }
        return a;
    }

    private void SetBounds(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private Point ToScreenDip(Point local)
    {
        var source = PresentationSource.FromVisual(this);
        var p = PointToScreen(local);
        return source?.CompositionTarget?.TransformFromDevice.Transform(p) ?? p;
    }

    // ───────────────────────── 드래그로 독 이동 ─────────────────────────

    private const double DragThreshold = 6;
    private bool _dragArmed;
    private bool _dragging;
    private Point _dragStart;          // 화면 DIP
    private double _dragGrabDelta;     // 커서와 패널 중심의 독 방향 거리 (같은 방향 가장자리면 유지)
    private DockEdge _dragEdge;
    private double _dragOffset;
    private DockGhostWindow? _ghost;

    private void OnPanelMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 아이콘은 자체 처리(e.Handled) → 여기 오는 건 빈 영역/패딩/구분선
        _dragStart = ToScreenDip(e.GetPosition(this));
        var panelCenter = ToScreenDip(PanelBorder.TranslatePoint(
            new Point(PanelBorder.ActualWidth / 2, PanelBorder.ActualHeight / 2), this));
        _dragGrabDelta = _layout.IsVertical ? _dragStart.Y - panelCenter.Y : _dragStart.X - panelCenter.X;
        _dragging = false;
        // NOACTIVATE(백그라운드) 창이어도 버튼을 이 창 위에서 누른 상태라 SetCapture 가 창 밖까지 유지된다
        _dragArmed = PanelBorder.CaptureMouse();
        e.Handled = true;
    }

    private void OnPanelMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragArmed) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
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
        }

        ComputeDragTarget(p, out _dragEdge, out _dragOffset);
        _ghost ??= new DockGhostWindow(_services);
        _ghost.ShowAt(GhostRect(_dragEdge, _dragOffset), _layout);
    }

    private void OnPanelMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
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
        // 저장 → LocalChanged → ReapplySettings 에서 AppBar 를 놓을 때 한 번만 재등록
        _services.Settings.Save();
    }

    private void OnPanelLostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 놓기 전에 캡처를 잃으면(다른 창 활성화 등) 취소
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
        // 같은 방향 가장자리면 잡은 지점 유지, 방향이 바뀌면 커서 = 패널 중심
        double delta = vertical == _layout.IsVertical ? _dragGrabDelta : 0;
        offset = vertical
            ? (p.Y - delta - s.Top) / Math.Max(1, s.Height)
            : (p.X - delta - s.Left) / Math.Max(1, s.Width);
        offset = ClampOffset(offset);
    }

    /// <summary>놓았을 때 패널이 차지할 화면 영역(DIP) 추정.</summary>
    private Rect GhostRect(DockEdge edge, double offset)
    {
        var l = _layout;
        var s = _screen;
        var a = GetPlacementArea();
        if (_appBarRegistered)
        {
            // 작업 영역에서 빠져 있는 독 자신의 예약 공간을 되돌림
            double t = _registeredThickness;
            a = _registeredEdge switch
            {
                DockEdge.Left => new Rect(a.Left - t, a.Top, a.Width + t, a.Height),
                DockEdge.Right => new Rect(a.Left, a.Top, a.Width + t, a.Height),
                DockEdge.Top => new Rect(a.Left, a.Top - t, a.Width, a.Height + t),
                _ => new Rect(a.Left, a.Top, a.Width, a.Height + t),
            };
        }
        double len = l.IsVertical ? PanelBorder.ActualHeight : PanelBorder.ActualWidth;
        double cross = l.PanelCross;
        double m = l.EdgeMargin;

        if (edge is DockEdge.Left or DockEdge.Right)
        {
            double top = Math.Clamp(s.Top + offset * s.Height - len / 2, a.Top, Math.Max(a.Top, a.Bottom - len));
            double left = edge == DockEdge.Left ? a.Left + m : a.Right - m - cross;
            return new Rect(left, top, cross, len);
        }
        else
        {
            double left = Math.Clamp(s.Left + offset * s.Width - len / 2, a.Left, Math.Max(a.Left, a.Right - len));
            double top = edge == DockEdge.Top ? a.Top + m : a.Bottom - m - cross;
            return new Rect(left, top, len, cross);
        }
    }

    // ───────────────────────── 항목 구성 ─────────────────────────

    private void RefreshItems(bool rebuildViews = false)
    {
        var settings = _services.Settings.Current;
        var tracker = _services.Windows;
        var windows = tracker.Windows;
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
                vm = new DockItemViewModel(id, pin, false, PinDisplayName(pin), SafeIcon(() => _services.Icons.GetIcon(pin)));
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
                             ?? new DockItemViewModel(id, null, false, AppNames.Get(wins[0]), SafeIcon(() => _services.Icons.GetIcon(wins[0])));
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
        if (structureChanged) RebuildViews(rebuildViews);
    }

    private void UpdateStates()
    {
        foreach (var item in _items)
        {
            if (item.IsSeparator) continue;
            item.IsRunning = item.Windows.Count > 0;
            item.HasNotification = item.Windows.Any(w => _flashed.Contains(w.Hwnd));
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
                view.Clicked += OnItemClicked;
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

    private bool SafeMatches(PinItem pin, AppWindowInfo w)
    {
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
        catch { return null; }
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
                if (item.Pin != null) _services.Launcher.Launch(item.Pin);
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
            System.Diagnostics.Debug.WriteLine($"[MyDock] dock click failed: {ex}");
        }
    }

    private void ClearNotification(DockItemViewModel item)
    {
        foreach (var w in item.Windows) _flashed.Remove(w.Hwnd);
    }

    // ───────────────────────── 이름 말풍선 ─────────────────────────

    private void OnItemHoverStarted(object? sender, EventArgs e)
    {
        if (sender is not DockItemView view || string.IsNullOrEmpty(view.Item.Name)) return;
        if (PanelBorder.ContextMenu?.IsOpen == true || _dragArmed) return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        const double gap = 8;

        // 창 안 좌표에서 기준점 계산 → 화면 물리 픽셀 → DIP
        var center = view.TranslatePoint(new Point(view.ActualWidth / 2, view.ActualHeight / 2), this);
        Point local = _layout.Edge switch
        {
            // 확대된 아이콘이 들어가는 창의 안쪽 끝에서 조금 더 바깥
            DockEdge.Left => new Point(ActualWidth + gap, center.Y),
            DockEdge.Bottom => new Point(center.X, -gap),
            DockEdge.Top => new Point(center.X, ActualHeight + gap),
            _ => new Point(-gap, center.Y),
        };
        var screen = fromDevice.Transform(PointToScreen(local));

        if (_label == null)
        {
            _label = new DockLabelWindow(_services);
            _label.SetColors(_layout.Background, _layout.Border);
        }
        _label.ShowAt(view.Item.Name, screen, _layout.Edge);
    }

    private void OnItemHoverEnded(object? sender, EventArgs e) => _label?.Hide();

    // ───────────────────────── 오른쪽 클릭 메뉴 ─────────────────────────

    private void OnPanelContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _label?.Hide();
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
            menu.Items.Add(Item("새 창 열기", () => _services.Launcher.Launch(pin)));
            if (item.IsRunning)
                menu.Items.Add(Item("창 닫기", () => CloseAll(item)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("아이콘 변경…", () => ChangeIcon(pin)));
            var reset = Item("기본 아이콘으로", () => ModifyPins(_ => pin.IconPath = null));
            reset.IsEnabled = pin.IconPath != null;
            menu.Items.Add(reset);
            menu.Items.Add(new Separator());
        }

        bool vertical = _layout.IsVertical;
        var up = Item(vertical ? "위로 이동" : "왼쪽으로 이동", () => MovePin(pin, -1));
        up.IsEnabled = index > 0;
        var down = Item(vertical ? "아래로 이동" : "오른쪽으로 이동", () => MovePin(pin, +1));
        down.IsEnabled = index >= 0 && index < pins.Count - 1;
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(item.IsSeparator ? "구분선 제거" : "독에서 제거", () => ModifyPins(p => p.Remove(pin))));
    }

    private void BuildRunningMenu(ContextMenu menu, DockItemViewModel item)
    {
        menu.Items.Add(Item("독에 고정", () =>
        {
            if (item.Windows.Count == 0) return;
            var newPin = _services.Windows.CreatePin(item.Windows[0]);
            ModifyPins(p => p.Add(newPin));
        }));
        if (item.IsRunning)
            menu.Items.Add(Item("창 닫기", () => CloseAll(item)));
    }

    private void BuildEmptyAreaMenu(ContextMenu menu)
    {
        menu.Items.Add(Item("구분선 추가", () => ModifyPins(p => p.Add(new PinItem { Kind = PinKind.Separator, Name = "" }))));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.DockPosition(_services));
        var dock = _services.Settings.Current.Dock;
        menu.Items.Add(DockMenus.Item("가운데로 정렬", () =>
        {
            _services.Settings.Current.Dock.Offset = 0.5;
            _services.Settings.Save();
        }, enabled: Math.Abs(dock.Offset - 0.5) > 0.0005));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.OpenSettings(_services));
        menu.Items.Add(DockMenus.Quit());
    }

    private static MenuItem Item(string header, Action action) => DockMenus.Item(header, action);

    // ───────────────────────── 메뉴 동작 ─────────────────────────

    private void CloseAll(DockItemViewModel item)
    {
        foreach (var w in item.Windows.ToList()) _services.Launcher.Close(w.Hwnd);
    }

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
        // 독 창은 NOACTIVATE 라 owner 로 쓰지 않음 (대화상자가 독 뒤에 숨는 것 방지용으로 Topmost 아님)
        if (dlg.ShowDialog() != true) return;
        string copied = _services.Settings.ImportIcon(dlg.FileName);
        ModifyPins(_ => pin.IconPath = copied);
    }

    /// <summary>핀 목록 수정 → 저장 → 즉시 다시 그림 (SettingsChanged 가 와도 같은 결과라 무해).</summary>
    private void ModifyPins(Action<List<PinItem>> change)
    {
        change(_services.Settings.Current.Pins);
        _services.Settings.Save();
        RefreshItems(rebuildViews: true);
    }
}
