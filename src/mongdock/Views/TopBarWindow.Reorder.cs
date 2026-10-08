using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mongdock.Services;
using R = Mongdock.Views.TopBarRightOrder;

namespace Mongdock.Views;

/// <summary>
/// 상단바 오른쪽 아이콘 순서 바꾸기 (맥 ⌘+끌기 대신 마우스만으로 — 원격(StarDesk)에서 수식키가 안 넘어올 수 있음).
/// 아이콘을 0.4초 길게 누르면 순서 바꾸기 모드: 아이콘이 살짝 커지고 반투명해져 커서를 따라 좌우로 움직이고,
/// 다른 아이콘들이 비켜선다. 놓으면 TopBar.RightItemsOrder 에 저장. 끄는 중 오른쪽 클릭 = 취소. 시계는 맨 오른쪽 고정.
/// 짧게 누르면 기존 클릭, 0.4초 안에 움직이면(트레이 아이콘이면 트레이 끌기) 길게 누르기 취소.
/// 트레이 영역(TrayArea + ⌃)은 한 덩어리로만 움직인다.
///
/// 터치: 이 구역은 윈도우 "누르고 있기"(손 떼면 오른쪽 클릭) 제스처를 끈다 — 켜 두면 손가락을 대는 순간의 누름이
/// 제스처 판정이 끝날 때까지 승격되지 않아 0.4초 길게 누르기가 시작되지 않고, 떼면 메뉴까지 같이 뜬다.
/// 대신 터치로 길게 누른 뒤 움직이지 않고 떼면 그 아이콘의 오른쪽 클릭(트레이 아이콘 = 앱 메뉴, 나머지 = 상단바 메뉴).
/// </summary>
public partial class TopBarWindow
{
    private const double LongPressMs = 400;
    /// <summary>길게 누르는 동안 이만큼(DIP) 움직이면 길게 누르기 아님 (트레이 아이콘 끌기 문턱 6 보다 작게).</summary>
    private const double PressSlop = 4;
    private const double ReorderShiftMs = 140;

    private DispatcherTimer? _pressTimer;
    private FrameworkElement? _pressItem;
    private Point _pressAt;

    private FrameworkElement? _reorderItem;
    private List<(FrameworkElement El, double Left, double Width)> _reorderOthers = new();
    private double _reorderLeft, _reorderWidth, _reorderStart, _reorderEnd;
    private int _reorderOriginalIndex, _reorderTarget;
    private bool _reorderEnding;
    private bool _reorderSwallowRightUp, _reorderSwallowLeftUp;
    private bool _pressTouch;          // 지금 길게 누르기가 터치로 시작됐는지
    private object? _pressSource;      // 누른 바로 그 요소 (트레이 아이콘 찾기용)
    private bool _reorderMoved;        // 순서 바꾸기 중 실제로 움직였는지

    private Dictionary<string, FrameworkElement> RightItems() => new()
    {
        [R.Desktops] = DesktopButtons,
        [R.NetSpeed] = NetSpeed,
        [R.Tray] = TrayArea,
        [R.Bluetooth] = BluetoothButton,
        [R.Wifi] = WifiButton,
        [R.Volume] = VolumeButton,
        [R.Search] = SearchButton,
        [R.ControlCenter] = QuickSettingsButton,
        [R.Ime] = ImeButton,
        [R.Battery] = BatteryButton,
    };

    internal bool IsReordering => _reorderItem != null;

    /// <summary>RightSection 자식 순서를 설정값대로 (같으면 아무것도 안 함). 시계는 항상 마지막.</summary>
    private void UpdateRightOrder()
    {
        if (_reorderItem != null) return; // 끄는 중엔 그대로 (끝나면 다시)
        var map = RightItems();
        var want = R.Resolve(_services.Settings.Current.TopBar.RightItemsOrder).Select(k => map[k]).ToList();
        want.Add(ClockButton);
        var kids = RightSection.Children;
        bool same = kids.Count == want.Count;
        for (int i = 0; same && i < want.Count; i++) same = ReferenceEquals(kids[i], want[i]);
        if (same) return;
        kids.Clear();
        foreach (var el in want) kids.Add(el);
        Remeasure(RightSection);
    }

    private void HookReorder()
    {
        Stylus.SetIsPressAndHoldEnabled(RightSection, false);
        RightSection.PreviewMouseLeftButtonDown += OnRightPressDown;
        RightSection.PreviewMouseMove += OnRightPressMove;
        RightSection.PreviewMouseLeftButtonUp += OnRightPressUp;
        RightSection.PreviewMouseRightButtonDown += OnRightReorderRightDown;
        RightSection.PreviewMouseRightButtonUp += OnRightReorderRightUp;
        RightSection.LostMouseCapture += OnRightLostCapture;
    }

    /// <summary>클릭된 요소가 속한 RightSection 의 바로 아래 항목.</summary>
    private FrameworkElement? RightItemFrom(object source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d != null)
        {
            var parent = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            if (ReferenceEquals(parent, RightSection)) return d as FrameworkElement;
            d = parent;
        }
        return null;
    }

    private void OnRightPressDown(object sender, MouseButtonEventArgs e)
    {
        if (_reorderItem != null) { e.Handled = true; return; }
        CancelPress();
        if (TrayIconDrag.IsActive) return;
        var item = RightItemFrom(e.OriginalSource);
        if (item == null || ReferenceEquals(item, ClockButton)) return;
        _pressItem = item;
        _pressTouch = TouchSupport.IsTouch(e);
        _pressSource = e.OriginalSource;
        _pressAt = e.GetPosition(RightSection);
        if (_pressTimer == null)
        {
            _pressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressMs) };
            _pressTimer.Tick += (_, _) => OnLongPress();
        }
        _pressTimer.Start();
        // e.Handled 는 그대로 — 짧게 누르면 원래 클릭
    }

    private void CancelPress()
    {
        _pressTimer?.Stop();
        _pressItem = null;
    }

    private void OnLongPress()
    {
        _pressTimer?.Stop();
        var item = _pressItem;
        _pressItem = null;
        if (item == null || _closed || TrayIconDrag.IsActive || Mouse.LeftButton != MouseButtonState.Pressed) return;
        if (PresentationSource.FromVisual(RightSection) is null || !item.IsVisible) return;
        var pos = Mouse.GetPosition(RightSection);
        if (Math.Abs(pos.X - _pressAt.X) > PressSlop || Math.Abs(pos.Y - _pressAt.Y) > PressSlop) return;
        BeginReorder(item);
    }

    private static (double Left, double Width) Slot(FrameworkElement el)
    {
        double x = VisualTreeHelper.GetOffset(el).X - el.Margin.Left;
        return (x, el.ActualWidth + el.Margin.Left + el.Margin.Right);
    }

    private void BeginReorder(FrameworkElement item)
    {
        // 보이는 항목들(시계 제외)의 원래 자리
        var visible = RightSection.Children.OfType<FrameworkElement>()
            .Where(c => !ReferenceEquals(c, ClockButton) && c.Visibility == Visibility.Visible && c.ActualWidth > 0)
            .ToList();
        int index = visible.IndexOf(item);
        if (index < 0) return;
        _panel?.Close(); // 패널 위치가 아이콘 기준이라 닫음
        if (!RightSection.CaptureMouse()) return; // 버튼은 캡처를 잃어 클릭이 나가지 않음

        _reorderItem = item;
        _reorderMoved = false;
        _reorderEnding = false;
        _reorderSwallowLeftUp = _reorderSwallowRightUp = false;
        _reorderOriginalIndex = _reorderTarget = index;
        (_reorderLeft, _reorderWidth) = Slot(item);
        _reorderOthers = visible.Where(c => !ReferenceEquals(c, item)).Select(c =>
        {
            var (l, w) = Slot(c);
            return (c, l, w);
        }).ToList();
        _reorderStart = Slot(visible[0]).Left;
        var last = Slot(visible[^1]);
        _reorderEnd = last.Left + last.Width;

        Panel.SetZIndex(item, 10);
        item.RenderTransformOrigin = new Point(0.5, 0.5);
        var (scale, _) = Anim.Transforms(item);
        if (Anim.Enabled)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(1.08, 110, Anim.EaseOut));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(1.08, 110, Anim.EaseOut));
            item.BeginAnimation(OpacityProperty, Anim.To(0.7, 110));
        }
        else
        {
            scale.ScaleX = scale.ScaleY = 1.08;
            item.Opacity = 0.7;
        }
    }

    private void OnRightPressMove(object sender, MouseEventArgs e)
    {
        if (_reorderItem == null)
        {
            if (_pressItem == null) return;
            var p = e.GetPosition(RightSection);
            if (e.LeftButton != MouseButtonState.Pressed
                || Math.Abs(p.X - _pressAt.X) > PressSlop || Math.Abs(p.Y - _pressAt.Y) > PressSlop)
                CancelPress();
            return;
        }
        e.Handled = true;
        if (_reorderEnding) return;
        double dx = e.GetPosition(RightSection).X - _pressAt.X;
        if (Math.Abs(dx) > PressSlop) _reorderMoved = true;
        // 구역 밖(시계 위 포함)으로는 못 나가게
        dx = Math.Clamp(dx, _reorderStart - _reorderLeft, _reorderEnd - (_reorderLeft + _reorderWidth));
        var (_, shift) = Anim.Transforms(_reorderItem);
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = dx;

        double center = _reorderLeft + _reorderWidth / 2 + dx;
        int target = _reorderOthers.Count(o => o.Left + o.Width / 2 < center);
        if (target == _reorderTarget) return;
        _reorderTarget = target;
        double x = _reorderStart;
        for (int i = 0; i < _reorderOthers.Count; i++)
        {
            if (i == target) x += _reorderWidth;
            var o = _reorderOthers[i];
            ReorderShift(o.El, x - o.Left);
            x += o.Width;
        }
    }

    private static void ReorderShift(UIElement el, double x)
    {
        var (_, shift) = Anim.Transforms(el);
        if (Math.Abs(shift.X - x) < 0.5 && !shift.HasAnimatedProperties) return;
        if (!Anim.Enabled)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = x;
            return;
        }
        shift.BeginAnimation(TranslateTransform.XProperty, Anim.To(x, ReorderShiftMs, Anim.EaseOut));
    }

    private void OnRightPressUp(object sender, MouseButtonEventArgs e)
    {
        if (_reorderSwallowLeftUp)
        {
            _reorderSwallowLeftUp = false;
            e.Handled = true;
            ReleaseReorderCaptureIfIdle(e);
            return;
        }
        if (_reorderItem == null)
        {
            CancelPress();
            return;
        }
        e.Handled = true;
        var item = _reorderItem;
        bool touchMenu = _pressTouch && !_reorderMoved;
        EndReorder(commit: true);
        // 터치로 길게 누르고 그대로 뗌 = 오른쪽 클릭 (시스템 누르고 있기 제스처를 끈 대신)
        if (touchMenu) Dispatcher.BeginInvoke(() => TouchRightClick(item), DispatcherPriority.Input);
    }

    /// <summary>터치 길게 누르기 → 오른쪽 클릭: 트레이 아이콘이면 그 앱의 메뉴, 아니면 상단바 메뉴.</summary>
    private void TouchRightClick(FrameworkElement item)
    {
        if (_closed) return;
        DependencyObject? d = _pressSource as DependencyObject;
        while (d != null && d is not TrayIconButton && !ReferenceEquals(d, RightSection))
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is TrayIconButton tray)
        {
            tray.SendRightClick();
            return;
        }
        if (ContextMenu is { } menu)
        {
            // 손가락 아래가 아니라 아이콘 바로 아래에 (닫히면 마우스 오른쪽 클릭용 기본값으로 되돌림)
            menu.PlacementTarget = item;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            RoutedEventHandler? restore = null;
            restore = (_, _) =>
            {
                menu.Closed -= restore;
                menu.ClearValue(System.Windows.Controls.ContextMenu.PlacementTargetProperty);
                menu.ClearValue(System.Windows.Controls.ContextMenu.PlacementProperty);
            };
            menu.Closed += restore;
            menu.IsOpen = true;
        }
    }

    /// <summary>끄는 중 오른쪽 클릭 = 취소 (상단바는 포커스가 없어 Esc 가 오지 않음).</summary>
    private void OnRightReorderRightDown(object sender, MouseButtonEventArgs e)
    {
        CancelPress();
        if (_reorderItem == null) return;
        e.Handled = true;
        _reorderSwallowRightUp = true;
        _reorderSwallowLeftUp = e.LeftButton == MouseButtonState.Pressed;
        EndReorder(commit: false, keepCapture: true);
    }

    private void OnRightReorderRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_reorderSwallowRightUp) return;
        _reorderSwallowRightUp = false;
        e.Handled = true; // 상단바 오른쪽 클릭 메뉴가 뜨지 않게
        ReleaseReorderCaptureIfIdle(e);
    }

    private void ReleaseReorderCaptureIfIdle(MouseButtonEventArgs e)
    {
        if (_reorderSwallowLeftUp || _reorderSwallowRightUp) return;
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed) return;
        if (RightSection.IsMouseCaptured)
        {
            _reorderEnding = true;
            RightSection.ReleaseMouseCapture();
            _reorderEnding = false;
        }
    }

    private void OnRightLostCapture(object sender, MouseEventArgs e)
    {
        // 자식 버튼이 캡처를 잃은 것(길게 누르기로 RightSection 이 가져감)도 여기로 올라옴 → 무시
        if (!ReferenceEquals(e.OriginalSource, RightSection)) return;
        _reorderSwallowLeftUp = _reorderSwallowRightUp = false;
        if (_reorderItem == null || _reorderEnding) return;
        EndReorder(commit: false); // 다른 창이 캡처를 가져감 → 취소
    }

    private void EndReorder(bool commit, bool keepCapture = false)
    {
        var item = _reorderItem;
        if (item == null) return;
        _reorderEnding = true;
        try
        {
            if (!keepCapture && RightSection.IsMouseCaptured) RightSection.ReleaseMouseCapture();
            _reorderItem = null;

            // 미리 보기 되돌림 (바로 아래에서 실제 순서를 바꾸므로 보이는 위치는 그대로)
            foreach (UIElement c in RightSection.Children)
            {
                if (c.RenderTransform is not TransformGroup) continue;
                var (scale, shift) = Anim.Transforms(c);
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.X = 0;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = 1;
            }
            item.BeginAnimation(OpacityProperty, null);
            item.Opacity = 1;
            Panel.SetZIndex(item, 0);

            int target = _reorderTarget;
            var others = _reorderOthers;
            _reorderOthers = new();
            if (commit && target != _reorderOriginalIndex && others.Count > 0)
            {
                var map = RightItems();
                string? key = map.FirstOrDefault(kv => ReferenceEquals(kv.Value, item)).Key;
                if (key != null)
                {
                    var top = _services.Settings.Current.TopBar;
                    var order = R.Resolve(top.RightItemsOrder);
                    order.Remove(key);
                    int at;
                    if (target < others.Count)
                    {
                        string before = map.First(kv => ReferenceEquals(kv.Value, others[target].El)).Key;
                        at = order.IndexOf(before);
                    }
                    else
                    {
                        string after = map.First(kv => ReferenceEquals(kv.Value, others[^1].El)).Key;
                        at = order.IndexOf(after) + 1;
                    }
                    order.Insert(Math.Clamp(at, 0, order.Count), key);
                    top.RightItemsOrder = order;
                    Log.Info($"상단바 오른쪽 순서 바꿈: {string.Join(",", order)}");
                    UpdateRightOrder();
                    try { _services.Settings.Save(); }
                    catch (Exception ex) { Log.Error("상단바 순서 저장 실패", ex); }
                }
            }
        }
        finally
        {
            _reorderEnding = false;
        }
        UpdateRightOrder();
        SyncTrayIcons(); // 끄는 동안 미뤄 둔 트레이 갱신
    }
}
