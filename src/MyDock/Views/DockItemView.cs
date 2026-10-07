using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MyDock.Models;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 독 항목 하나의 화면 (코드로 구성). 아이콘 + 실행 중 점(가장자리 쪽) + 알림 점(아이콘 모서리).
/// 호버 시 아이콘은 가장자리 반대쪽으로 확대되고, 슬롯은 독 방향으로 늘어나 이웃 아이콘과 겹치지 않는다.
/// 바운스/흔들림 애니메이션은 없다.
/// </summary>
internal sealed class DockItemView : Grid
{
    private static readonly Duration HoverDuration = new(TimeSpan.FromMilliseconds(110));

    private readonly DockLayout _layout;
    private readonly ScaleTransform? _scale;
    private readonly Ellipse? _runningDot;
    private readonly Ellipse? _notifyDot;

    public DockItemViewModel Item { get; }

    /// <summary>호버 시작/종료 (이름 말풍선 표시용).</summary>
    public event EventHandler? HoverStarted;
    public event EventHandler? HoverEnded;
    /// <summary>왼쪽 클릭.</summary>
    public event EventHandler? Clicked;

    public DockItemView(DockItemViewModel item, DockLayout layout)
    {
        Item = item;
        _layout = layout;
        Focusable = false;
        Background = Brushes.Transparent; // 슬롯 전체를 클릭 영역으로

        if (item.IsSeparator)
        {
            BuildSeparator();
            return;
        }

        double icon = layout.IconSize;
        double half = layout.Spacing / 2;
        if (layout.IsVertical)
        {
            Width = icon;
            Height = icon;
            Margin = new Thickness(0, half, 0, half);
        }
        else
        {
            Width = icon;
            Height = icon;
            Margin = new Thickness(half, 0, half, 0);
        }

        // 확대되는 아이콘 묶음 (아이콘 + 알림 점)
        var iconHost = new Grid
        {
            Width = icon,
            Height = icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = layout.ScaleOrigin,
            IsHitTestVisible = false,
        };
        _scale = new ScaleTransform(1, 1);
        iconHost.RenderTransform = _scale;

        var image = new Image { Source = item.Icon, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        iconHost.Children.Add(image);

        double n = layout.NotificationSize;
        _notifyDot = new Ellipse
        {
            Width = n,
            Height = n,
            Fill = layout.Notification,
            Stroke = layout.Background,
            StrokeThickness = 1,
            VerticalAlignment = VerticalAlignment.Top,
            // 왼쪽 독은 가장자리 쪽(왼쪽 위), 그 외는 오른쪽 위
            HorizontalAlignment = layout.Edge == DockEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = new Thickness(-n * 0.15),
            Visibility = Visibility.Collapsed,
        };
        iconHost.Children.Add(_notifyDot);
        Children.Add(iconHost);

        // 실행 중 표시 점: 패딩 영역 가운데 (가장자리 쪽)
        double d = layout.IndicatorSize;
        double offset = -(layout.Padding / 2 + d / 2);
        _runningDot = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = layout.Indicator,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        switch (layout.Edge)
        {
            case DockEdge.Left:
                _runningDot.HorizontalAlignment = HorizontalAlignment.Left;
                _runningDot.VerticalAlignment = VerticalAlignment.Center;
                _runningDot.Margin = new Thickness(offset, 0, 0, 0);
                break;
            case DockEdge.Bottom:
                _runningDot.HorizontalAlignment = HorizontalAlignment.Center;
                _runningDot.VerticalAlignment = VerticalAlignment.Bottom;
                _runningDot.Margin = new Thickness(0, 0, 0, offset);
                break;
            case DockEdge.Top:
                _runningDot.HorizontalAlignment = HorizontalAlignment.Center;
                _runningDot.VerticalAlignment = VerticalAlignment.Top;
                _runningDot.Margin = new Thickness(0, offset, 0, 0);
                break;
            default:
                _runningDot.HorizontalAlignment = HorizontalAlignment.Right;
                _runningDot.VerticalAlignment = VerticalAlignment.Center;
                _runningDot.Margin = new Thickness(0, 0, offset, 0);
                break;
        }
        Children.Add(_runningDot);

        UpdateDots();
        item.PropertyChanged += OnItemPropertyChanged;
        Unloaded += (_, _) => SetHover(false, animate: false);
    }

    /// <summary>뷰를 버릴 때 VM 구독 해제.</summary>
    public void Detach() => Item.PropertyChanged -= OnItemPropertyChanged;

    private void BuildSeparator()
    {
        double icon = _layout.IconSize;
        double gap = Math.Max(4, _layout.Spacing + 2);
        var line = new Rectangle { Fill = _layout.Border, IsHitTestVisible = false, SnapsToDevicePixels = true };
        if (_layout.IsVertical)
        {
            line.Width = icon * 0.7;
            line.Height = 1;
            Margin = new Thickness(0, gap, 0, gap);
        }
        else
        {
            line.Width = 1;
            line.Height = icon * 0.7;
            Margin = new Thickness(gap, 0, gap, 0);
        }
        // 구분선도 오른쪽 클릭(제거/이동) 가능하도록 위아래 여백 포함 영역을 히트 영역으로
        Children.Add(line);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateDots();

    private void UpdateDots()
    {
        if (_runningDot != null) _runningDot.Visibility = Item.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        if (_notifyDot != null) _notifyDot.Visibility = Item.HasNotification ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (Item.IsSeparator) return;
        SetHover(true, animate: true);
        HoverStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (Item.IsSeparator) return;
        SetHover(false, animate: true);
        HoverEnded?.Invoke(this, EventArgs.Empty);
    }

    private bool _pressed;

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // 구분선은 처리하지 않음 → 패널로 올라가 독 드래그 이동 시작점이 됨
        if (Item.IsSeparator) return;
        _pressed = true;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Item.IsSeparator) return;
        if (_pressed && IsMouseOver) Clicked?.Invoke(this, EventArgs.Empty);
        _pressed = false;
        e.Handled = true;
    }

    /// <summary>확대/복원. 아이콘은 RenderTransform, 슬롯은 독 방향 길이만 늘림.</summary>
    public void SetHover(bool on, bool animate)
    {
        if (_scale == null) return;
        double s = on ? _layout.HoverScale : 1.0;
        double len = _layout.IconSize * s;
        var lengthProp = _layout.IsVertical ? HeightProperty : WidthProperty;

        if (!animate || _layout.HoverScale <= 1.0)
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BeginAnimation(lengthProp, null);
            _scale.ScaleX = _scale.ScaleY = s;
            SetValue(lengthProp, len);
            return;
        }

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var scaleAnim = new DoubleAnimation(s, HoverDuration) { EasingFunction = ease };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        BeginAnimation(lengthProp, new DoubleAnimation(len, HoverDuration) { EasingFunction = ease });
    }
}
